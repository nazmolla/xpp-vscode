import { promises as fs } from 'fs';
import * as path from 'path';
import * as vscode from 'vscode';
import { objectUri } from './codeView';
import { readActiveEnvironment } from './environment';

interface ModelLocation {
    name: string;
    packageName: string;
    folder: string;
    isCustom: boolean;
}

type AotNode =
    | { kind: 'model'; model: ModelLocation }
    | { kind: 'type'; model: ModelLocation; objectType: string; count: number }
    | { kind: 'group'; model: ModelLocation; objectType: string; label: string; names: string[] }
    | { kind: 'object'; model: ModelLocation; objectType: string; name: string };

const TypeLabels: Record<string, string> = {
    AxClass: 'Classes', AxTable: 'Tables', AxTableExtension: 'Table extensions', AxForm: 'Forms', AxFormExtension: 'Form extensions',
    AxEdt: 'Extended data types', AxEdtExtension: 'EDT extensions', AxEnum: 'Base enums', AxEnumExtension: 'Base enum extensions',
    AxView: 'Views', AxViewExtension: 'View extensions', AxQuery: 'Queries', AxQuerySimpleExtension: 'Query extensions',
    AxDataEntityView: 'Data entities', AxDataEntityViewExtension: 'Data entity extensions', AxMap: 'Maps', AxMapExtension: 'Map extensions',
    AxMenu: 'Menus', AxMenuExtension: 'Menu extensions', AxMenuItemDisplay: 'Display menu items', AxMenuItemDisplayExtension: 'Display menu item extensions',
    AxMenuItemOutput: 'Output menu items', AxMenuItemOutputExtension: 'Output menu item extensions', AxMenuItemAction: 'Action menu items',
    AxMenuItemActionExtension: 'Action menu item extensions', AxReport: 'Reports', AxSecurityPrivilege: 'Security privileges',
    AxSecurityDuty: 'Security duties', AxSecurityDutyExtension: 'Security duty extensions', AxSecurityRole: 'Security roles',
    AxSecurityRoleExtension: 'Security role extensions', AxSecurityPolicy: 'Security policies', AxLabelFile: 'Label files',
    AxConfigurationKey: 'Configuration keys', AxLicenseCode: 'License codes', AxService: 'Services', AxServiceGroup: 'Service groups',
    AxWorkflowType: 'Workflow types', AxWorkflowApproval: 'Workflow approvals', AxWorkflowTask: 'Workflow tasks', AxWorkflowCategory: 'Workflow categories',
    AxTile: 'Tiles', AxKPI: 'KPIs', AxAggregateDataEntity: 'Aggregate data entities', AxAggregateMeasurement: 'Aggregate measurements',
    AxAggregateDimension: 'Aggregate dimensions', AxCompositeDataEntityView: 'Composite data entities', AxResource: 'Resources',
    AxReference: 'References', AxMacroDictionary: 'Macros', AxTableCollection: 'Table collections', AxNumberSequenceModule: 'Number sequence modules',
};

/** Largest number of objects shown under one node before they are split into A–Z groups. */
const GroupThreshold = 500;

/** Application Explorer (model view) straight from the metadata folders of the active configuration. */
export class AotTreeProvider implements vscode.TreeDataProvider<AotNode> {
    private readonly changed = new vscode.EventEmitter<void>();
    readonly onDidChangeTreeData = this.changed.event;
    private models?: Promise<ModelLocation[]>;

    refresh(): void {
        this.models = undefined;
        this.changed.fire();
    }

    getTreeItem(node: AotNode): vscode.TreeItem {
        switch (node.kind) {
            case 'model': {
                const item = new vscode.TreeItem(node.model.name, vscode.TreeItemCollapsibleState.Collapsed);
                item.description = node.model.isCustom ? 'custom' : node.model.packageName !== node.model.name ? node.model.packageName : undefined;
                item.iconPath = new vscode.ThemeIcon(node.model.isCustom ? 'package' : 'library');
                item.tooltip = node.model.folder;
                return item;
            }
            case 'type': {
                const item = new vscode.TreeItem(TypeLabels[node.objectType] ?? node.objectType, vscode.TreeItemCollapsibleState.Collapsed);
                item.description = String(node.count);
                item.iconPath = vscode.ThemeIcon.Folder;
                return item;
            }
            case 'group': {
                const item = new vscode.TreeItem(node.label, vscode.TreeItemCollapsibleState.Collapsed);
                item.description = String(node.names.length);
                item.iconPath = vscode.ThemeIcon.Folder;
                return item;
            }
            case 'object': {
                const item = new vscode.TreeItem(node.name, vscode.TreeItemCollapsibleState.None);
                item.iconPath = new vscode.ThemeIcon(iconFor(node.objectType));
                item.contextValue = node.objectType === 'AxForm' || node.objectType === 'AxFormExtension' ? 'xppForm' : 'xppObject';
                item.command = { command: 'vscode.open', title: 'Open', arguments: [objectUri(node.objectType, node.name)] };
                return item;
            }
        }
    }

    async getChildren(node?: AotNode): Promise<AotNode[]> {
        if (!node) {
            this.models ??= loadModels();
            return (await this.models).map(model => ({ kind: 'model', model }));
        }
        switch (node.kind) {
            case 'model':
                return listTypes(node.model);
            case 'type': {
                const names = await listNames(node.model, node.objectType);
                return names.length > GroupThreshold
                    ? groupByLetter(names).map(([label, group]) => ({ kind: 'group', model: node.model, objectType: node.objectType, label, names: group }))
                    : names.map(name => ({ kind: 'object', model: node.model, objectType: node.objectType, name }));
            }
            case 'group':
                return node.names.map(name => ({ kind: 'object', model: node.model, objectType: node.objectType, name }));
            default:
                return [];
        }
    }
}

async function loadModels(): Promise<ModelLocation[]> {
    const env = await readActiveEnvironment().catch(() => undefined);
    if (!env) {
        return [];
    }
    const roots: [string, boolean][] = [];
    if (env.modelStoreFolder) {
        roots.push([env.modelStoreFolder, true]);
    }
    for (const root of env.referencePackagesPaths) {
        roots.push([root, false]);
    }

    const models = new Map<string, ModelLocation>();
    for (const [root, isCustom] of roots) {
        for (const pkg of await subdirectories(root)) {
            const descriptors = await fs.readdir(path.join(root, pkg, 'Descriptor')).catch(() => [] as string[]);
            for (const descriptor of descriptors.filter(d => d.toLowerCase().endsWith('.xml'))) {
                const name = descriptor.slice(0, -4);
                const folder = path.join(root, pkg, name);
                // Custom roots are read first, so a custom model wins over a same-named reference copy.
                if (!models.has(name.toLowerCase()) && await isDirectory(folder)) {
                    models.set(name.toLowerCase(), { name, packageName: pkg, folder, isCustom });
                }
            }
        }
    }
    return [...models.values()].sort((a, b) => Number(b.isCustom) - Number(a.isCustom) || a.name.localeCompare(b.name));
}

async function listTypes(model: ModelLocation): Promise<AotNode[]> {
    const types = (await subdirectories(model.folder)).filter(d => d.startsWith('Ax'));
    const nodes = await Promise.all(types.map(async objectType => {
        const files = await fs.readdir(path.join(model.folder, objectType)).catch(() => [] as string[]);
        const count = files.filter(f => f.toLowerCase().endsWith('.xml')).length;
        return { kind: 'type' as const, model, objectType, count };
    }));
    return nodes
        .filter(n => n.count > 0)
        .sort((a, b) => (TypeLabels[a.objectType] ?? a.objectType).localeCompare(TypeLabels[b.objectType] ?? b.objectType));
}

async function listNames(model: ModelLocation, objectType: string): Promise<string[]> {
    const files = await fs.readdir(path.join(model.folder, objectType)).catch(() => [] as string[]);
    return files
        .filter(f => f.toLowerCase().endsWith('.xml'))
        .map(f => f.slice(0, -4))
        .sort((a, b) => a.localeCompare(b, undefined, { sensitivity: 'base' }));
}

function groupByLetter(names: string[]): [string, string[]][] {
    const groups = new Map<string, string[]>();
    for (const name of names) {
        const letter = /^[a-z]/i.test(name) ? name[0].toUpperCase() : '#';
        groups.set(letter, [...(groups.get(letter) ?? []), name]);
    }
    return [...groups.entries()].sort(([a], [b]) => a.localeCompare(b));
}

async function subdirectories(dir: string): Promise<string[]> {
    const entries = await fs.readdir(dir, { withFileTypes: true }).catch(() => []);
    return entries.filter(e => e.isDirectory() || e.isSymbolicLink()).map(e => e.name);
}

async function isDirectory(dir: string): Promise<boolean> {
    return (await fs.stat(dir).catch(() => undefined))?.isDirectory() ?? false;
}

export function iconFor(objectType: string): string {
    if (objectType.endsWith('Extension')) {
        return 'extensions';
    }
    switch (objectType) {
        case 'AxClass': return 'symbol-class';
        case 'AxTable': return 'table';
        case 'AxForm': return 'window';
        case 'AxEnum': return 'symbol-enum';
        case 'AxEdt': return 'symbol-field';
        case 'AxQuery': return 'search';
        case 'AxView': return 'eye';
        case 'AxDataEntityView': return 'database';
        default: return objectType.startsWith('AxSecurity') ? 'shield' : objectType.startsWith('AxMenuItem') ? 'link' : 'symbol-misc';
    }
}
