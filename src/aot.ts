import { promises as fs } from 'fs';
import * as path from 'path';
import * as vscode from 'vscode';
import { objectUri } from './codeView';
import { readActiveEnvironment } from './environment';

interface ModelLocation {
    name: string;
    displayName: string;
    packageName: string;
    folder: string;
    isCustom: boolean;
}

interface ElementEntry {
    name: string;
    models: ModelLocation[];
}

type AotNode =
    | { kind: 'model'; model: ModelLocation }
    | { kind: 'category'; category: string; model?: ModelLocation; types: string[] }
    | { kind: 'type'; objectType: string; model?: ModelLocation }
    | { kind: 'element'; objectType: string; entry: ElementEntry; scope?: ModelLocation }
    | { kind: 'more'; count: number };

/** Application Explorer categories and the element types under each, in Visual Studio's order. */
const Categories: [string, string[]][] = [
    ['Data Types', ['AxEnum', 'AxEnumExtension', 'AxEdt', 'AxEdtExtension']],
    ['Data Model', ['AxTable', 'AxTableExtension', 'AxView', 'AxViewExtension', 'AxQuery', 'AxQuerySimpleExtension', 'AxDataEntityView',
        'AxDataEntityViewExtension', 'AxCompositeDataEntityView', 'AxAggregateDataEntity', 'AxMap', 'AxMapExtension', 'AxTableCollection']],
    ['Code', ['AxClass', 'AxMacroDictionary']],
    ['User Interface', ['AxForm', 'AxFormExtension', 'AxTile', 'AxTileExtension', 'AxMenu', 'AxMenuExtension', 'AxMenuItemDisplay', 'AxMenuItemDisplayExtension',
        'AxMenuItemAction', 'AxMenuItemActionExtension', 'AxMenuItemOutput', 'AxMenuItemOutputExtension', 'AxFormPart', 'AxInfoPart', 'AxPartCue', 'AxPartCueGroup']],
    ['AI', ['AxSkill']],
    ['Analytics', ['AxAggregateDimension', 'AxAggregateMeasurement', 'AxKPI', 'AxAggregateCalculatedMeasureTemplate', 'AxAggregateCalculatedMeasureTemplateOtherPeriod']],
    ['Reports', ['AxReport', 'AxReportEmbeddedImage', 'AxReportExternalDataSource', 'AxReportLayoutTemplate', 'AxReportListStyleTemplate',
        'AxReportMatrixStyleTemplate', 'AxReportPieDoughnutChartStyleTemplate', 'AxReportTableStyleTemplate', 'AxReportXYChartStyleTemplate']],
    ['Business Process and Workflow', ['AxWorkflowCategory', 'AxWorkflowApproval', 'AxWorkflowApprovalExtension', 'AxWorkflowTask', 'AxWorkflowTaskExtension',
        'AxWorkflowAutomatedTask', 'AxWorkflowTemplate', 'AxWorkflowTemplateExtension', 'AxWorkflowDueDateCalculationProvider',
        'AxWorkflowHierarchyAssignmentProvider', 'AxWorkflowParticipantAssignmentProvider', 'AxWorkflowQueueAssignmentProvider']],
    ['Label Files', ['AxLabelFile']],
    ['Resources', ['AxResource']],
    ['Configuration', ['AxConfigurationKey', 'AxConfigurationKeyGroup', 'AxLicenseCode']],
    ['Security', ['AxSecurityRole', 'AxSecurityRoleExtension', 'AxSecurityDuty', 'AxSecurityDutyExtension', 'AxSecurityPrivilege', 'AxSecurityPolicy',
        'AxSecurityDirectAccessPermission']],
    ['References', ['AxReference']],
    ['Services', ['AxService', 'AxServiceGroup']],
];

const TypeLabels: Record<string, string> = {
    AxAggregateDataEntity: 'Aggregate Data Entities', AxAggregateDimension: 'Aggregate Dimensions', AxAggregateMeasurement: 'Aggregate Measurements',
    AxKPI: 'KPIs', AxPerspective: 'Perspectives', AxWorkflowApproval: 'Workflow Approvals', AxWorkflowApprovalExtension: 'Workflow Approval Extensions',
    AxWorkflowAutomatedTask: 'Workflow Automated Tasks', AxWorkflowCategory: 'Workflow Categories', AxWorkflowHierarchyProvider: 'Workflow Hierarchy Providers',
    AxWorkflowProvider: 'Workflow Providers', AxWorkflowTask: 'Workflow Tasks', AxWorkflowTaskExtension: 'Workflow Task Extensions', AxWorkflowType: 'Workflow Types',
    AxWorkflowTypeExtension: 'Workflow Type Extensions', AxClass: 'Classes', AxMacroDictionary: 'Macros', AxConfigurationKey: 'Configuration Keys',
    AxConfigurationKeyGroup: 'Configuration Key Groups', AxLicenseCode: 'License Codes', AxCompositeDataEntityView: 'Composite Data Entities',
    AxDataEntityView: 'Data Entities', AxDataEntityViewExtension: 'Data Entity Extensions', AxMap: 'Maps', AxMapExtension: 'Map Extensions', AxQuery: 'Queries',
    AxQuerySimpleExtension: 'Query Extensions', AxTable: 'Tables', AxTableExtension: 'Table Extensions', AxTableCollection: 'Table Collections', AxView: 'Views',
    AxViewExtension: 'View Extensions', AxEnum: 'Base Enums', AxEnumExtension: 'Base Enum Extensions', AxEdt: 'Extended Data Types', AxEdtExtension: 'Extended Data Type Extensions',
    AxLabelFile: 'Label Files', AxResource: 'Resources', AxReference: 'References', AxReport: 'Reports', AxReportDataSource: 'Report Data Sources',
    AxReportEmbeddedImage: 'Report Embedded Images', AxReportImage: 'Report Images', AxReportLayout: 'Report Layouts', AxReportStyleTemplate: 'Report Style Templates',
    AxSecurityDuty: 'Security Duties', AxSecurityDutyExtension: 'Security Duty Extensions', AxSecurityPermission: 'Security Permissions', AxSecurityPolicy: 'Security Policies',
    AxSecurityPrivilege: 'Security Privileges', AxSecurityRole: 'Security Roles', AxSecurityRoleExtension: 'Security Role Extensions', AxService: 'Services',
    AxServiceExtension: 'Service Extensions', AxServiceGroup: 'Service Groups', AxServiceGroupExtension: 'Service Group Extensions', AxForm: 'Forms',
    AxFormExtension: 'Form Extensions', AxMenu: 'Menus', AxMenuExtension: 'Menu Extensions', AxMenuItemAction: 'Action Menu Items',
    AxMenuItemActionExtension: 'Action Menu Item Extensions', AxMenuItemDisplay: 'Display Menu Items', AxMenuItemDisplayExtension: 'Display Menu Item Extensions',
    AxMenuItemOutput: 'Output Menu Items', AxMenuItemOutputExtension: 'Output Menu Item Extensions', AxTile: 'Tiles', AxTileExtension: 'Tile Extensions',
    AxSkill: 'Skills', AxFormPart: 'Form Parts', AxInfoPart: 'Info Parts', AxPartCue: 'Cues', AxPartCueGroup: 'Cue Groups',
    AxAggregateCalculatedMeasureTemplate: 'Calculated Measure Templates', AxAggregateCalculatedMeasureTemplateOtherPeriod: 'Calculated Measure Templates (Other Period)',
    AxReportExternalDataSource: 'Report Data Sources', AxReportLayoutTemplate: 'Report Layout Templates', AxReportListStyleTemplate: 'List Style Templates',
    AxReportMatrixStyleTemplate: 'Matrix Style Templates', AxReportPieDoughnutChartStyleTemplate: 'Pie/Doughnut Chart Style Templates',
    AxReportTableStyleTemplate: 'Table Style Templates', AxReportXYChartStyleTemplate: 'XY Chart Style Templates',
    AxWorkflowTemplate: 'Workflow Types', AxWorkflowTemplateExtension: 'Workflow Type Extensions',
    AxWorkflowDueDateCalculationProvider: 'Due Date Calculation Providers', AxWorkflowHierarchyAssignmentProvider: 'Hierarchy Assignment Providers',
    AxWorkflowParticipantAssignmentProvider: 'Participant Assignment Providers', AxWorkflowQueueAssignmentProvider: 'Queue Assignment Providers',
    AxSecurityDirectAccessPermission: 'Direct Access Permissions',
};

/** Element types that have X++ code. */
export const CodeTypes = new Set(['AxClass', 'AxTable', 'AxForm', 'AxView', 'AxDataEntityView', 'AxQuery', 'AxMap']);

/** Most elements listed under one node while a filter is active. */
const FilteredLimit = 2000;

export function typeLabel(objectType: string): string {
    return TypeLabels[objectType] ?? objectType.replace(/^Ax/, '');
}

/** What clicking an element does in Visual Studio: classes open in the code editor, everything else in its designer. */
export function elementOpenCommand(objectType: string, objectName: string): vscode.Command {
    return objectType === 'AxClass'
        ? { command: 'vscode.open', title: 'View Code', arguments: [objectUri(objectType, objectName)] }
        : { command: 'xpp.openDesigner', title: 'Open Designer', arguments: [objectType, objectName] };
}

export function elementContextValue(objectType: string, isCustom = false): string {
    return ['xppElement', CodeTypes.has(objectType) ? 'code' : '', objectType === 'AxForm' ? 'form' : '',
        ExtensionTypes[objectType] ? 'extensible' : '', CodeExtensionFunctions[objectType] ? 'coc' : '',
        BrowserTypes.has(objectType) ? 'browser' : '', objectType === 'AxTable' ? 'table' : '', WhereUsedTypes.has(objectType) ? 'refs' : '',
        objectType === 'AxClass' || objectType === 'AxTable' ? 'hier' : '', objectType === 'AxClass' ? 'class' : '',
        objectType.startsWith('AxMenuItem') ? 'menuitem' : '', objectType === 'AxDataEntityView' ? 'entity' : '',
        objectType === 'AxSecurityRole' ? 'role' : '', objectType === 'AxSecurityDuty' ? 'duty' : '',
        objectType === 'AxSecurityPrivilege' ? 'privilege' : '',
        isCustom ? 'custom' : '',
    ].filter(Boolean).join(' ');
}

/** Element types that have a metadata extension type (Create extension). */
export const ExtensionTypes: Record<string, string> = {
    AxTable: 'AxTableExtension', AxForm: 'AxFormExtension', AxEnum: 'AxEnumExtension', AxEdt: 'AxEdtExtension',
    AxDataEntityView: 'AxDataEntityViewExtension', AxSecurityDuty: 'AxSecurityDutyExtension', AxSecurityRole: 'AxSecurityRoleExtension',
};

/** Element types that can have a Chain of Command class (Create code extension), with the intrinsic naming them. */
export const CodeExtensionFunctions: Record<string, string> = {
    AxClass: 'classStr', AxTable: 'tableStr', AxForm: 'formStr', AxDataEntityView: 'tableStr', AxView: 'tableStr', AxMap: 'mapStr',
};

/** Elements that can be opened in the web client. */
export const BrowserTypes = new Set(['AxMenuItemDisplay', 'AxMenuItemOutput', 'AxMenuItemAction', 'AxClass', 'AxTable', 'AxForm']);

/** Element types the cross-reference database indexes. */
export const WhereUsedTypes = new Set(['AxClass', 'AxTable', 'AxForm', 'AxEnum', 'AxEdt', 'AxView', 'AxQuery', 'AxDataEntityView', 'AxMap', 'AxMenu',
    'AxMenuItemDisplay', 'AxMenuItemAction', 'AxMenuItemOutput', 'AxTableExtension', 'AxEnumExtension', 'AxReport', 'AxSecurityRole',
    'AxSecurityPrivilege', 'AxSecurityDuty', 'AxTile', 'AxConfigurationKey']);

/**
 * Application Explorer, read straight from the metadata folders of the active configuration.
 * Classic view: every model's elements grouped by type. Model view: each model, then the same grouping.
 */
export class AotTreeProvider implements vscode.TreeDataProvider<AotNode> {
    private readonly changed = new vscode.EventEmitter<void>();
    readonly onDidChangeTreeData = this.changed.event;
    private models?: Promise<ModelLocation[]>;
    private readonly elementCache = new Map<string, Promise<ElementEntry[]>>();
    view?: vscode.TreeView<AotNode>;
    modelView = false;
    filter = '';

    refresh(): void {
        this.models = undefined;
        this.elementCache.clear();
        this.update();
    }

    setModelView(on: boolean): void {
        this.modelView = on;
        void vscode.commands.executeCommand('setContext', 'xpp.aot.modelView', on);
        this.update();
    }

    setFilter(filter: string): void {
        this.filter = filter.trim();
        void vscode.commands.executeCommand('setContext', 'xpp.aot.filtered', this.filter.length > 0);
        this.update();
    }

    private update(): void {
        if (this.view) {
            const parts = [this.modelView ? 'Model view' : 'Classic view', this.filter ? `filter: ${this.filter}` : ''];
            this.view.description = parts.filter(Boolean).join(' · ');
        }
        this.changed.fire();
    }

    getTreeItem(node: AotNode): vscode.TreeItem {
        const expanded = this.filter ? vscode.TreeItemCollapsibleState.Expanded : vscode.TreeItemCollapsibleState.Collapsed;
        switch (node.kind) {
            case 'model': {
                const item = new vscode.TreeItem(node.model.displayName, expanded);
                item.id = `model/${node.model.name}`;
                item.description = node.model.displayName !== node.model.name ? node.model.name : undefined;
                item.iconPath = new vscode.ThemeIcon(node.model.isCustom ? 'package' : 'library');
                item.contextValue = node.model.isCustom ? 'xppModel custom' : 'xppModel';
                item.tooltip = `${node.model.displayName} [${node.model.name}]\nPackage ${node.model.packageName}\n${node.model.folder}${node.model.isCustom ? '\nCustom model' : ''}`;
                return item;
            }
            case 'category': {
                const item = new vscode.TreeItem(node.category, expanded);
                item.id = `${node.model?.name ?? ''}/${node.category}`;
                item.iconPath = vscode.ThemeIcon.Folder;
                return item;
            }
            case 'type': {
                const item = new vscode.TreeItem(typeLabel(node.objectType), expanded);
                item.id = `${node.model?.name ?? ''}/${node.objectType}`;
                item.iconPath = vscode.ThemeIcon.Folder;
                return item;
            }
            case 'element': {
                const { objectType, entry } = node;
                const item = new vscode.TreeItem(entry.name, vscode.TreeItemCollapsibleState.None);
                item.id = `${node.scope?.name ?? ''}/${objectType}/${entry.name}`;
                item.description = node.scope ? undefined : entry.models.map(m => m.displayName).join(', ');
                item.iconPath = new vscode.ThemeIcon(iconFor(objectType));
                item.contextValue = elementContextValue(objectType, entry.models.some(m => m.isCustom));
                item.command = elementOpenCommand(objectType, entry.name);
                item.tooltip = `${entry.name} (${typeLabel(objectType)})\n${entry.models.map(m => `${m.displayName} [${m.name}]`).join('\n')}`;
                return item;
            }
            case 'more': {
                const item = new vscode.TreeItem(`${node.count} more… refine the filter`, vscode.TreeItemCollapsibleState.None);
                item.iconPath = new vscode.ThemeIcon('ellipsis');
                return item;
            }
        }
    }

    async getChildren(node?: AotNode): Promise<AotNode[]> {
        if (!node) {
            const models = await this.loadModels();
            if (models.length === 0) {
                return [];
            }
            if (this.modelView) {
                const shown = this.filter ? await this.filterModels(models) : models;
                return shown.map(model => ({ kind: 'model', model }));
            }
            return this.categoryNodes(undefined);
        }
        switch (node.kind) {
            case 'model':
                return this.categoryNodes(node.model);
            case 'category':
                return this.typeNodes(node.types, node.model);
            case 'type': {
                const entries = this.applyFilter(await this.elements(node.objectType, node.model));
                const nodes: AotNode[] = entries.slice(0, this.filter ? FilteredLimit : entries.length)
                    .map(entry => ({ kind: 'element', objectType: node.objectType, entry, scope: node.model }));
                if (this.filter && entries.length > FilteredLimit) {
                    nodes.push({ kind: 'more', count: entries.length - FilteredLimit });
                }
                return nodes;
            }
            default:
                return [];
        }
    }

    private async categoryNodes(model: ModelLocation | undefined): Promise<AotNode[]> {
        const present = model ? new Set(await subdirectories(model.folder)) : undefined;
        const known = new Set(Categories.flatMap(([, types]) => types));
        const nodes: AotNode[] = [];
        for (const [category, types] of Categories) {
            const shown = present ? types.filter(t => present.has(t)) : types;
            if (shown.length > 0 && (await this.typeNodes(shown, model)).length > 0) {
                // A category holding only its own type (Label Files, Resources, References) is that type's node, like Visual Studio.
                nodes.push(types.length === 1 && typeLabel(types[0]) === category
                    ? { kind: 'type', objectType: types[0], model }
                    : { kind: 'category', category, model, types: shown });
            }
        }
        // Types this version doesn't know yet still show up, under Other.
        const others = present ? [...present].filter(t => t.startsWith('Ax') && !known.has(t)) : [];
        if (others.length > 0) {
            nodes.push({ kind: 'category', category: 'Other', model, types: others.sort() });
        }
        return nodes;
    }

    private async typeNodes(types: string[], model: ModelLocation | undefined): Promise<AotNode[]> {
        if (!this.filter) {
            return types.map(objectType => ({ kind: 'type', objectType, model }));
        }
        const matching = await Promise.all(types.map(async t => this.applyFilter(await this.elements(t, model)).length > 0 ? t : undefined));
        return matching.filter((t): t is string => !!t).map(objectType => ({ kind: 'type', objectType, model }));
    }

    private async filterModels(models: ModelLocation[]): Promise<ModelLocation[]> {
        const all = Categories.flatMap(([, types]) => types);
        const hits = await Promise.all(models.map(async model => {
            const present = new Set(await subdirectories(model.folder));
            for (const t of all.filter(t => present.has(t))) {
                if (this.applyFilter(await this.elements(t, model)).length > 0) {
                    return model;
                }
            }
            return undefined;
        }));
        return hits.filter((m): m is ModelLocation => !!m);
    }

    private applyFilter(entries: ElementEntry[]): ElementEntry[] {
        if (!this.filter) {
            return entries;
        }
        const needle = this.filter.toLowerCase();
        return entries.filter(e => e.name.toLowerCase().includes(needle));
    }

    /** Elements of one type, in one model or (classic view) across all models. */
    private elements(objectType: string, model: ModelLocation | undefined): Promise<ElementEntry[]> {
        const key = `${model?.name ?? '*'}/${objectType}`;
        let cached = this.elementCache.get(key);
        if (!cached) {
            cached = this.listElements(objectType, model);
            this.elementCache.set(key, cached);
        }
        return cached;
    }

    private async listElements(objectType: string, model: ModelLocation | undefined): Promise<ElementEntry[]> {
        const models = model ? [model] : await this.loadModels();
        const byName = new Map<string, ElementEntry>();
        await Promise.all(models.map(async m => {
            const files = await fs.readdir(path.join(m.folder, objectType)).catch(() => [] as string[]);
            for (const file of files) {
                if (!file.toLowerCase().endsWith('.xml')) {
                    continue;
                }
                const name = file.slice(0, -4);
                const entry = byName.get(name.toLowerCase());
                if (entry) {
                    entry.models.push(m);
                } else {
                    byName.set(name.toLowerCase(), { name, models: [m] });
                }
            }
        }));
        return [...byName.values()].sort((a, b) => a.name.localeCompare(b.name, undefined, { sensitivity: 'base' }));
    }

    private loadModels(): Promise<ModelLocation[]> {
        this.models ??= loadModels();
        return this.models;
    }
}

/** Models of the active configuration (custom first), for the Dynamics 365 tools. */
export function listModels(): Promise<ModelLocation[]> {
    return loadModels();
}

export type { ModelLocation };

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

    // Cloud-hosted and VHD environments keep every package in one folder; as the server decides, a package there is
    // standard when every model in it is published by Microsoft or sealed (Customization=DoNotAllow).
    const shared = env.referencePackagesPaths.some(p => path.resolve(p).toLowerCase() === path.resolve(env.modelStoreFolder ?? '').toLowerCase());
    const models = new Map<string, ModelLocation>();
    const scanned = new Set<string>();
    for (const [root, isCustom] of roots) {
        if (scanned.has(path.resolve(root).toLowerCase())) {
            continue;
        }
        scanned.add(path.resolve(root).toLowerCase());
        await Promise.all((await subdirectories(root)).map(async pkg => {
            const descriptorDir = path.join(root, pkg, 'Descriptor');
            const descriptors = (await fs.readdir(descriptorDir).catch(() => [] as string[])).filter(d => d.toLowerCase().endsWith('.xml'));
            const texts = await Promise.all(descriptors.map(d => fs.readFile(path.join(descriptorDir, d), 'utf8').catch(() => '')));
            const standard = shared && texts.length > 0 && texts.every(text =>
                /^Microsoft/i.test(descriptorValue(text, 'Publisher') ?? '') || /^DoNotAllow$/i.test(descriptorValue(text, 'Customization') ?? ''));
            for (const [i, descriptor] of descriptors.entries()) {
                const name = descriptor.slice(0, -4);
                const folder = path.join(root, pkg, name);
                // Custom roots are read first, so a custom model wins over a same-named reference copy.
                if (models.has(name.toLowerCase()) || !await isDirectory(folder)) {
                    continue;
                }
                const displayName = descriptorValue(texts[i], 'DisplayName') ?? name;
                if (!models.has(name.toLowerCase())) {
                    models.set(name.toLowerCase(), { name, displayName, packageName: pkg, folder, isCustom: isCustom && !standard });
                }
            }
        }));
    }
    return [...models.values()].sort((a, b) => Number(b.isCustom) - Number(a.isCustom) || a.displayName.localeCompare(b.displayName));
}

/** The model's display name ("Application Suite") from its descriptor. */
function descriptorValue(text: string, element: string): string | undefined {
    return new RegExp(`<${element}>([^<]*)</${element}>`).exec(text)?.[1].trim() || undefined;
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
        case 'AxLabelFile': return 'symbol-string';
        case 'AxReport': return 'graph';
        default: return objectType.startsWith('AxSecurity') ? 'shield' : objectType.startsWith('AxMenuItem') ? 'link' : 'symbol-misc';
    }
}
