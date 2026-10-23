import { promises as fs } from 'fs';
import * as path from 'path';
import * as vscode from 'vscode';
import { elementOpenCommand, listModels, typeLabel } from '../aot';
import { elementOf, pickElement, TreeArg } from '../d365Tools';
import { CommandRegistrar, Element, ProductivityContext } from '../productivity/common';
import { exportText, Inline, SecurityPanel, SecurityReport } from './securityPanel';
import { AccessTypes, LicenseTypes, objectTypeOf, RoleInfo, security, SecurityNode, SecurityTreeProvider } from './securityView';

/** Built-in segregation of duties rules, written as the starting point by Edit Segregation of Duties Rules. */
const DefaultSodRules = [
    { Name: 'Vendor master vs vendor payments', First: 'VendVendorMasterMaintain', Second: 'PaymVendorPaymentsMaintain', Severity: 'High', Reason: 'Create or change a vendor (bank account) and pay it.' },
    { Name: 'Vendor master vs vendor invoices', First: 'VendVendorMasterMaintain', Second: 'VendInvoiceVendorInvoicesMaintain', Severity: 'High', Reason: 'Create a vendor and invoice it.' },
    { Name: 'Purchase orders vs vendor invoices', First: 'PurchOrderMaintain', Second: 'VendInvoiceVendorInvoicesMaintain', Severity: 'Medium', Reason: 'Order and invoice the same goods.' },
    { Name: 'Vendor payments vs payment approval', First: 'PaymVendorPaymentsMaintain', Second: 'PaymVendorPaymentTransactionsApprove', Severity: 'High', Reason: 'Enter and approve the same payment.' },
    { Name: 'Bank accounts vs vendor payments', First: 'BankBankAccountsMaintain', Second: 'PaymVendorPaymentsMaintain', Severity: 'High', Reason: 'Change bank accounts and pay.' },
    { Name: 'Customer master vs customer payments', First: 'CustCustomersMaintain', Second: 'PaymCustomerPaymentsMaintain', Severity: 'Medium', Reason: 'Create a customer and post its payments.' },
    { Name: 'Customer payments vs payment approval', First: 'PaymCustomerPaymentsMaintain', Second: 'PaymCustPaymentTransactionsApprove', Severity: 'Medium', Reason: 'Enter and approve the same payment.' },
    { Name: 'Credit limits vs sales orders', First: 'CredManCustCreditAdjustmentsMaintain', Second: 'SalesOrderMaintain', Severity: 'Medium', Reason: 'Raise a credit limit and sell against it.' },
    { Name: 'Fixed assets vs fixed asset approval', First: 'AssetFixedAssetsMaintain', Second: 'AssetFixedAssetsApprove', Severity: 'Medium', Reason: 'Create and approve the same asset.' },
    { Name: 'Security administration vs journals', First: 'SysSecSecurityMaintain', Second: 'LedgerJournalsAndTransactionsMaintain', Severity: 'High', Reason: 'Grant yourself access and post.' },
];

const KindLabels: Record<string, string> = { AxSecurityRole: 'Role', AxSecurityDuty: 'Duty', AxSecurityPrivilege: 'Privilege' };

/** The Security view, license analysis, who can access, gap checks, segregation of duties and the sign-off report. */
export function registerSecurity(context: vscode.ExtensionContext, command: CommandRegistrar, ctx: ProductivityContext): void {
    const tree = new SecurityTreeProvider(ctx);
    const view = vscode.window.createTreeView('xpp.security', { treeDataProvider: tree, showCollapseAll: true, canSelectMany: true });
    tree.view = view;
    context.subscriptions.push(view);
    void vscode.commands.executeCommand('setContext', 'xpp.security.filtered', false);

    command('xpp.security.refresh', async () => {
        await withProgress('Refreshing security metadata…', () => security(ctx, 'refresh'));
        tree.refresh();
    });
    command('xpp.security.filter', async () => {
        const filter = await vscode.window.showInputBox({ title: 'Filter roles', prompt: 'Part of a role name or label', value: tree.filter });
        if (filter !== undefined) {
            tree.setFilter(filter);
        }
    });
    command('xpp.security.clearFilter', () => tree.setFilter(''));
    command('xpp.security.analyzeLicense', (arg?: TreeArg) => analyzeLicense(ctx, arg));
    command('xpp.security.whoCanAccess', (arg?: TreeArg) => whoCanAccess(ctx, arg));
    command('xpp.security.findGaps', () => findGaps(ctx));
    command('xpp.security.checkSod', (arg?: TreeArg) => checkSod(ctx, arg));
    command('xpp.security.report', (arg?: TreeArg, selected?: SecurityNode[]) => report(ctx, arg, selected ?? view.selection));
    command('xpp.security.editSodRules', () => editSodRules());
}

function withProgress<T>(title: string, task: () => Promise<T>): Thenable<T> {
    return vscode.window.withProgress({ location: vscode.ProgressLocation.Notification, title }, task);
}

/** A link that opens the element the way the Application Explorer does. */
function openLink(text: string, objectType: string, objectName = text): Inline {
    return {
        text, open: () => {
            const open = elementOpenCommand(objectType, objectName);
            return vscode.commands.executeCommand(open.command, ...(open.arguments ?? []));
        },
    };
}

/** An element passed by a tree node, a designer root or a tree item of the Security view; extensions resolve to their base element. */
function elementArg(arg: TreeArg | undefined): Element | undefined {
    const element = elementOf(arg);
    if (element?.objectType.endsWith('Extension')) {
        return { objectType: element.objectType.replace(/Extension$/, ''), objectName: element.objectName.split('.')[0] };
    }
    return element;
}

// ── Analyze license ──

interface LicenseResult {
    kind: string;
    name: string;
    license: string | null;
    entryPoints: number;
    byLicense: Record<string, number>;
    drivers: { menuItem: string; type: string; grant: string; license: string; viewLicense: string; maintainLicense: string; via: string[] }[];
    driverCount: number;
    lowerWithReadOnly: string[];
    roles: string[];
    duties: number;
    privileges: number;
    missing: string[];
}

async function analyzeLicense(ctx: ProductivityContext, arg?: TreeArg): Promise<void> {
    let element = elementArg(arg);
    if (element && !LicenseTypes.includes(element.objectType)) {
        throw new Error(`${typeLabel(element.objectType)} have no license of their own. Analyze a role, duty or privilege.`);
    }
    element ??= await pickSecurityObject(ctx, 'Analyze license');
    if (element) {
        await showLicense(ctx, element.objectType, element.objectName);
    }
}

export async function showLicense(ctx: ProductivityContext, objectType: string, objectName: string): Promise<void> {
    const r = await withProgress(`Analyzing the license of ${objectName}…`, () => security<LicenseResult>(ctx, 'license', { objectType, objectName }));
    const kind = r.kind || KindLabels[objectType] || typeLabel(objectType);
    const name = r.name || objectName;
    const doc = new SecurityReport(`${kind} ${name}: ${r.license ?? 'unknown'}`, `license-${name}`);
    doc.text(
        `${r.entryPoints ?? 0} entry point(s)`,
        r.duties !== undefined ? `, ${r.duties} dut${r.duties === 1 ? 'y' : 'ies'}` : '',
        r.privileges !== undefined ? `, ${r.privileges} privilege(s)` : '',
        '. The license is the highest View or Maintain user license of the menu items it grants, depending on the access.');
    if (r.roles?.length) {
        doc.heading('Roles');
        doc.list(r.roles.map(role => ({ text: role, open: () => showLicense(ctx, 'AxSecurityRole', role) })));
    }
    const counts = Object.entries(r.byLicense ?? {});
    if (counts.length) {
        doc.heading('Entry points by license');
        doc.table(['License', 'Entry points'], counts.map(([license, count]) => [license, String(count)]));
    }
    const drivers = r.drivers ?? [];
    if (drivers.length) {
        doc.heading(`What drives the license${r.driverCount > drivers.length ? ` (first ${drivers.length} of ${r.driverCount})` : ''}`);
        doc.table(['Menu item', 'Type', 'Grant', 'License', 'View license', 'Maintain license', 'Via'], drivers.map(d => [
            openLink(d.menuItem, objectTypeOf(d.type), d.menuItem), d.type.replace(/^Ax/, ''), d.grant ?? '', d.license ?? '',
            d.viewLicense ?? '', d.maintainLicense ?? '', (d.via ?? []).join('; '),
        ]));
    }
    if (r.lowerWithReadOnly?.length) {
        doc.heading('Read-only alternatives');
        doc.note('Granting only Read on these menu items would lower the license:');
        doc.list(r.lowerWithReadOnly);
    }
    if (r.missing?.length) {
        doc.heading('Missing objects');
        doc.note('Referenced but not found in the metadata:');
        doc.list(r.missing);
    }
    SecurityPanel.show(doc);
}

/** A role (QuickPick fed by the server with a typed filter), duty or privilege. */
async function pickSecurityObject(ctx: ProductivityContext, title: string): Promise<Element | undefined> {
    const kind = await vscode.window.showQuickPick([
        { label: 'Role', objectType: 'AxSecurityRole' },
        { label: 'Duty', objectType: 'AxSecurityDuty' },
        { label: 'Privilege', objectType: 'AxSecurityPrivilege' },
    ], { title });
    if (!kind) {
        return undefined;
    }
    if (kind.objectType === 'AxSecurityRole') {
        const roles = await pickRoles(ctx, title, false);
        return roles?.[0] ? { objectType: 'AxSecurityRole', objectName: roles[0] } : undefined;
    }
    return pickElement(ctx.server, `${title}: ${kind.label.toLowerCase()}`, [kind.objectType]);
}

/** Roles by name or label; an empty filter lists the roles of your models. */
async function pickRoles(ctx: ProductivityContext, title: string, many: boolean): Promise<string[] | undefined> {
    const filter = await vscode.window.showInputBox({
        title, prompt: 'Part of a role name or label (empty: roles of your models)', ignoreFocusOut: true,
    });
    if (filter === undefined) {
        return undefined;
    }
    const result = await withProgress('Finding roles…', () =>
        security<{ roles: RoleInfo[] }>(ctx, 'roles', filter.trim() ? { filter: filter.trim() } : { customOnly: true }));
    const roles = result.roles ?? [];
    if (roles.length === 0) {
        vscode.window.showInformationMessage(filter.trim() ? `No roles match '${filter}'.` : 'Your models have no roles and extend none.');
        return undefined;
    }
    const items = roles.map(r => ({
        label: r.name, description: r.label && r.label !== r.name ? r.label : undefined,
        detail: [r.model, r.custom ? 'your model' : '', r.extended ? 'extended by your models' : ''].filter(Boolean).join(' · ') || undefined,
    }));
    if (many) {
        const picked = await vscode.window.showQuickPick(items, { title, canPickMany: true, matchOnDescription: true, ignoreFocusOut: true });
        return picked?.length ? picked.map(p => p.label) : undefined;
    }
    const picked = await vscode.window.showQuickPick(items, { title, matchOnDescription: true });
    return picked ? [picked.label] : undefined;
}

// ── Who can access ──

interface AccessResult {
    menuItems: { name: string; type: string; viewLicense: string; maintainLicense: string }[];
    privileges: { privilege: string; model: string; grant: string; duties: string[]; roles: string[] }[];
    roleCount: number;
    viaForms: string[];
}

async function whoCanAccess(ctx: ProductivityContext, arg?: TreeArg): Promise<void> {
    const element = elementArg(arg) ?? await pickElement(ctx.server, 'Who can access', AccessTypes);
    if (!element) {
        return;
    }
    if (!AccessTypes.includes(element.objectType)) {
        throw new Error('Who Can Access works on menu items, forms, tables and data entities.');
    }
    const r = await withProgress(`Finding who can access ${element.objectName}…`, () => security<AccessResult>(ctx, 'access', element));
    const typeName = typeLabel(element.objectType).replace(/ies$/, 'y').replace(/s$/, '');
    const doc = new SecurityReport(`Who can access ${typeName} ${element.objectName}`, `access-${element.objectName}`);
    const privileges = r.privileges ?? [];
    doc.text(`${privileges.length} privilege(s) grant access, in ${r.roleCount ?? 0} role(s).`);
    if (r.menuItems?.length) {
        doc.heading('Menu items');
        doc.table(['Menu item', 'Type', 'View license', 'Maintain license'], r.menuItems.map(m => [
            openLink(m.name, objectTypeOf(m.type)), m.type.replace(/^Ax/, ''), m.viewLicense ?? '', m.maintainLicense ?? '',
        ]));
    }
    if (privileges.length) {
        doc.heading('Privileges');
        doc.table(['Privilege', 'Model', 'Grant', 'Duties', 'Roles'], privileges.map(p => [
            { text: p.privilege, open: () => vscode.commands.executeCommand('xpp.openDesigner', 'AxSecurityPrivilege', p.privilege) },
            p.model ?? '', p.grant ?? '',
            (p.duties ?? []).map(d => ({ text: d, open: () => vscode.commands.executeCommand('xpp.openDesigner', 'AxSecurityDuty', d) })),
            (p.roles ?? []).map(role => ({ text: role, open: () => showLicense(ctx, 'AxSecurityRole', role) })),
        ]));
        doc.note('Select a role to analyze its license.');
    } else {
        doc.note('No privilege grants access. Use Add to Privilege on its menu item.');
    }
    if (r.viaForms?.length) {
        doc.heading('Through forms');
        doc.list(r.viaForms.map(f => openLink(f, 'AxForm')));
    }
    SecurityPanel.show(doc);
}

// ── Gaps ──

interface GapsResult {
    models: string[];
    findings: { severity: 'Error' | 'Warning' | 'Info'; kind: string; objectType: string; name: string; message: string }[];
}

async function findGaps(ctx: ProductivityContext): Promise<void> {
    const models = await listModels();
    const picked = await vscode.window.showQuickPick(
        models.map(m => ({ label: m.name, description: m.displayName !== m.name ? m.displayName : undefined, picked: m.isCustom })),
        { title: 'Find security gaps: models', canPickMany: true, matchOnDescription: true, ignoreFocusOut: true });
    if (!picked?.length) {
        return;
    }
    const r = await withProgress('Checking security…', () => security<GapsResult>(ctx, 'gaps', { models: picked.map(p => p.label) }));
    const checked = r.models ?? picked.map(p => p.label);
    const doc = new SecurityReport(`Security gaps: ${checked.join(', ')}`, 'security-gaps');
    const findings = r.findings ?? [];
    if (findings.length === 0) {
        doc.text('No gaps found.');
    }
    for (const severity of ['Error', 'Warning', 'Info'] as const) {
        const group = findings.filter(f => f.severity === severity);
        if (group.length === 0) {
            continue;
        }
        doc.heading(`${severity === 'Info' ? 'Information' : severity + 's'} (${group.length})`);
        doc.table(['Object', 'Type', 'Check', 'Message'], group.map(f => [
            openLink(f.name, objectTypeOf(f.objectType)), typeLabel(objectTypeOf(f.objectType)).replace(/ies$/, 'y').replace(/s$/, ''), f.kind ?? '', f.message ?? '',
        ]), () => severity);
    }
    SecurityPanel.show(doc);
}

// ── Segregation of duties ──

interface SodResult {
    roles: string[];
    rulesChecked: number;
    rulesSkipped: string[];
    conflicts: { role: string; rule: string; severity: string; reason: string; first: string; second: string }[];
}

async function checkSod(ctx: ProductivityContext, arg?: TreeArg): Promise<void> {
    let role: string | undefined;
    const element = elementArg(arg);
    if (element?.objectType === 'AxSecurityRole') {
        role = element.objectName;
    } else if (!element) {
        const scope = await vscode.window.showQuickPick([
            { label: 'Roles of your models', id: 'mine' },
            { label: 'Pick a role…', id: 'pick' },
        ], { title: 'Check segregation of duties' });
        if (!scope) {
            return;
        }
        if (scope.id === 'pick') {
            role = (await pickRoles(ctx, 'Check segregation of duties', false))?.[0];
            if (!role) {
                return;
            }
        }
    }
    const rules = await readSodRules();
    const r = await withProgress('Checking segregation of duties…', () =>
        security<SodResult>(ctx, 'sod', { role, rulesJson: rules?.json }));
    const doc = new SecurityReport(`Segregation of duties${role ? `: ${role}` : ''}`, `sod${role ? '-' + role : ''}`);
    const conflicts = r.conflicts ?? [];
    doc.text(`${r.roles?.length ?? 0} role(s), ${r.rulesChecked ?? 0} rule(s) checked, ${conflicts.length} conflict(s). Rules: `,
        rules ? rules.file : 'built-in', ' (', { text: 'Edit rules', open: () => vscode.commands.executeCommand('xpp.security.editSodRules') }, ')');
    if (conflicts.length) {
        doc.heading('Conflicts');
        const duty = (name: string): Inline => ({ text: name, open: () => vscode.commands.executeCommand('xpp.openDesigner', 'AxSecurityDuty', name) });
        doc.table(['Role', 'Rule', 'Severity', 'Reason', 'First', 'Second'], conflicts.map(c => [
            { text: c.role, open: () => showLicense(ctx, 'AxSecurityRole', c.role) }, c.rule ?? '', c.severity ?? '', c.reason ?? '', duty(c.first), duty(c.second),
        ]), i => conflicts[i].severity === 'High' ? 'Error' : 'Warning');
    } else {
        doc.text('No conflicts.');
    }
    if (r.rulesSkipped?.length) {
        doc.heading('Skipped rules');
        doc.note('These rules name duties that do not exist in the metadata:');
        doc.list(r.rulesSkipped);
    }
    SecurityPanel.show(doc);
}

/** The rules file of xpp.security.sodRulesFile (relative paths are under the first workspace folder). */
function sodRulesPath(): string | undefined {
    const setting = (vscode.workspace.getConfiguration('xpp').get<string>('security.sodRulesFile') ?? '').trim();
    if (!setting) {
        return undefined;
    }
    const folder = vscode.workspace.workspaceFolders?.[0]?.uri.fsPath ?? '';
    const resolved = setting.replace(/\$\{workspaceFolder\}/g, folder);
    return path.isAbsolute(resolved) ? resolved : path.join(folder, resolved);
}

async function readSodRules(): Promise<{ file: string; json: string } | undefined> {
    const file = sodRulesPath();
    if (!file) {
        return undefined;
    }
    let json: string;
    try {
        json = (await fs.readFile(file, 'utf8')).replace(/^\uFEFF/, '');
    } catch {
        throw new Error(`The segregation of duties rules file ${file} was not found. Fix xpp.security.sodRulesFile or clear it to use the built-in rules.`);
    }
    try {
        JSON.parse(json);
    } catch (err) {
        throw new Error(`${path.basename(file)} is not valid JSON: ${(err as Error).message}`);
    }
    return { file, json };
}

async function editSodRules(): Promise<void> {
    const config = vscode.workspace.getConfiguration('xpp');
    const folder = vscode.workspace.workspaceFolders?.[0];
    let file = sodRulesPath();
    const isSet = !!file;
    if (!file) {
        if (folder) {
            file = path.join(folder.uri.fsPath, '.xpp', 'sod-rules.json');
        } else {
            const target = await vscode.window.showSaveDialog({ title: 'Segregation of duties rules', filters: { JSON: ['json'] } });
            if (!target) {
                return;
            }
            file = target.fsPath;
        }
    }
    const exists = await fs.stat(file).then(() => true, () => false);
    if (!exists) {
        await fs.mkdir(path.dirname(file), { recursive: true });
        await fs.writeFile(file, JSON.stringify(DefaultSodRules, null, 4) + '\n', 'utf8');
    }
    if (!isSet) {
        await config.update('security.sodRulesFile', file, folder ? vscode.ConfigurationTarget.Workspace : vscode.ConfigurationTarget.Global);
    }
    await vscode.window.showTextDocument(vscode.Uri.file(file));
}

// ── Report ──

async function report(ctx: ProductivityContext, arg: TreeArg | undefined, selected: readonly SecurityNode[] | undefined): Promise<void> {
    const fromSelection = (selected?.length ? selected : [])
        .filter((n): n is Extract<SecurityNode, { kind: 'element' }> => n.kind === 'element' && n.secKind === 'Role')
        .map(n => n.name);
    const element = elementArg(arg);
    let roles: string[] | undefined = element?.objectType === 'AxSecurityRole'
        ? [...new Set([element.objectName, ...fromSelection])]
        : undefined;
    let model: string | undefined;
    if (!roles) {
        const scope = await vscode.window.showQuickPick([
            ...(fromSelection.length ? [{ label: `Selected roles (${fromSelection.length})`, id: 'selected' }] : []),
            { label: 'Roles of model…', id: 'model' },
            { label: 'Pick roles…', id: 'pick' },
        ], { title: 'Security report' });
        if (!scope) {
            return;
        }
        if (scope.id === 'selected') {
            roles = fromSelection;
        } else if (scope.id === 'model') {
            const picked = await vscode.window.showQuickPick(
                (await listModels()).map(m => ({ label: m.name, description: m.displayName !== m.name ? m.displayName : undefined })),
                { title: 'Security report: model', matchOnDescription: true });
            if (!picked) {
                return;
            }
            model = picked.label;
        } else {
            roles = await pickRoles(ctx, 'Security report: roles', true);
            if (!roles) {
                return;
            }
        }
    }
    const format = await vscode.window.showQuickPick([
        { label: 'Markdown', id: 'markdown' as const, description: 'sign-off document' },
        { label: 'CSV', id: 'csv' as const, description: 'for Excel' },
    ], { title: 'Security report: format' });
    if (!format) {
        return;
    }
    const r = await withProgress('Writing the security report…', () =>
        security<{ text: string; roles: string[] }>(ctx, 'report', { roles, model, format: format.id }));
    const baseName = `security-report${model ? '-' + model : roles?.length === 1 ? '-' + roles[0] : ''}`;
    if (format.id === 'csv') {
        await exportText(r.text ?? '', baseName, 'csv');
        return;
    }
    const document = await vscode.workspace.openTextDocument({ language: 'markdown', content: r.text ?? '' });
    await vscode.window.showTextDocument(document);
    await vscode.commands.executeCommand('markdown.showPreviewToSide');
}
