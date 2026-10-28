import { ErdPanel } from './erd';
import { promises as fs } from 'fs';
import * as path from 'path';
import * as vscode from 'vscode';
import { CodeExtensionFunctions, CodeTypes, ExtensionTypes, listModels, ModelLocation, typeLabel } from './aot';
import { methodUri, objectUri } from './codeView';
import { DiagramNode, DiagramPanel, hierarchyDiagram, MetadataDocuments, MetadataScheme, referencesDiagram } from './diagrams';
import { readActiveEnvironment, selectedEnvironmentUrl } from './environment';
import { findActiveProject, readProject } from './project';
import { XppServerClient } from './serverClient';
import { compilerLineToDocumentLine } from './xppSource';

/** An Application Explorer element, a project item, or a model node passed by a tree context menu. */
export type TreeArg = {
    objectType?: string;
    entry?: { name: string };
    item?: { objectType: string; objectName: string };
    model?: { name: string };
};

export interface ToolNode {
    label: string;
    icon: string;
    command?: string;
    commandArgs?: unknown[];
    description?: string;
    tooltip?: string;
    children?: ToolNode[];
    /** Children computed when the node is expanded (e.g. the Code Intelligence switches). */
    dynamicChildren?: () => ToolNode[];
    /** Shows a checkbox bound to this state; toggling calls onToggle. */
    checked?: boolean;
    onToggle?: (checked: boolean) => void;
}

/** Extra top-level nodes contributed by other parts of the extension, with a change event to refresh them. */
export interface ToolsContribution {
    nodes(): ToolNode[];
    onDidChange: vscode.Event<void>;
}

/** The Dynamics 365 menu of Visual Studio, as a view. */
const ToolsMenu: ToolNode[] = [
    {
        label: 'Deploy', icon: 'cloud-upload', children: [
            { label: 'Create Deployment Package…', icon: 'package', command: 'xpp.tools.createPackage' },
            { label: 'Deploy Models to Online Environment…', icon: 'cloud-upload', command: 'xpp.pac.deploy', description: 'pac' },
            { label: 'Initialize Deployment Solution…', icon: 'new-folder', command: 'xpp.pac.init', description: 'pac' },
        ],
    },
    {
        label: 'Model Management', icon: 'library', children: [
            { label: 'Create Model…', icon: 'add', command: 'xpp.tools.createModel' },
            { label: 'Update Model Parameters…', icon: 'settings', command: 'xpp.tools.updateModel' },
            { label: 'View Model Dependencies…', icon: 'type-hierarchy', command: 'xpp.tools.modelDependencies' },
            { label: 'Refresh Models', icon: 'refresh', command: 'xpp.refreshAot' },
        ],
    },
    { label: 'Build Models…', icon: 'tools', command: 'xpp.build' },
    { label: 'Synchronize Database', icon: 'database', command: 'xpp.pac.dbSync', description: 'pac' },
    {
        label: 'Debug', icon: 'debug-alt', children: [
            { label: 'Launch Debugger', icon: 'debug-start', command: 'xpp.debug.start' },
            { label: 'Infolog', icon: 'output', command: 'xpp.infolog.focus' },
        ],
    },
    {
        label: 'Project', icon: 'project', children: [
            { label: 'Select Project…', icon: 'folder-opened', command: 'xpp.selectProject' },
            { label: 'Export Project…', icon: 'export', command: 'xpp.tools.exportProject' },
            { label: 'Import Project…', icon: 'cloud-download', command: 'xpp.tools.importProject' },
        ],
    },
    { label: 'Find Labels…', icon: 'symbol-string', command: 'xpp.tools.findLabels' },
    { label: 'Find Tag References…', icon: 'tag', command: 'xpp.tools.findTagReferences' },
    { label: 'Metadata Search…', icon: 'search', command: 'xpp.openObject' },
    { label: 'Find References…', icon: 'references', command: 'xpp.findReferences' },
    { label: 'Search Code…', icon: 'search-fuzzy', command: 'xpp.searchCode', description: 'all packages' },
    { label: 'Find Message Source…', icon: 'search', command: 'xpp.findMessageSource', description: 'error text to code' },
    { label: 'Find Similar Standard Implementations…', icon: 'lightbulb', command: 'xpp.findExamples' },
    {
        label: 'Security', icon: 'shield', children: [
            { label: 'Find Security Gaps…', icon: 'checklist', command: 'xpp.security.findGaps' },
            { label: 'Segregation of Duties…', icon: 'law', command: 'xpp.security.checkSod' },
            { label: 'Security Report…', icon: 'file-text', command: 'xpp.security.report' },
            { label: 'Who Can Access…', icon: 'person', command: 'xpp.security.whoCanAccess' },
            { label: 'Analyze License…', icon: 'credit-card', command: 'xpp.security.analyzeLicense' },
            { label: 'Edit Segregation of Duties Rules', icon: 'edit', command: 'xpp.security.editSodRules' },
        ],
    },
    {
        label: 'View', icon: 'type-hierarchy', children: [
            { label: 'View Hierarchy…', icon: 'type-hierarchy-sub', command: 'xpp.viewHierarchy' },
            { label: 'View References…', icon: 'references', command: 'xpp.viewReferences' },
            { label: 'Compare Code…', icon: 'diff', command: 'xpp.compare' },
            { label: 'Compare Metadata…', icon: 'diff-multiple', command: 'xpp.compareMetadata' },
        ],
    },
    {
        label: 'Run', icon: 'play', children: [
            { label: 'Open in Browser…', icon: 'globe', command: 'xpp.openInBrowser' },
            { label: 'Open Table Browser…', icon: 'table', command: 'xpp.openTableBrowser' },
        ],
    },
    // Environment (EnvironmentStatus) and Code Intelligence are contributed here, before Options.
    { label: 'Options…', icon: 'gear', command: 'xpp.tools.options' },
];

export class ToolsTreeProvider implements vscode.TreeDataProvider<ToolNode> {
    private readonly changed = new vscode.EventEmitter<void>();
    readonly onDidChangeTreeData = this.changed.event;

    constructor(private readonly contributions: ToolsContribution[] = []) {
        for (const c of contributions) {
            c.onDidChange(() => this.changed.fire());
        }
    }

    getTreeItem(node: ToolNode): vscode.TreeItem {
        const hasChildren = !!node.children || !!node.dynamicChildren;
        const item = new vscode.TreeItem(node.label, hasChildren
            ? (node.dynamicChildren ? vscode.TreeItemCollapsibleState.Collapsed : vscode.TreeItemCollapsibleState.Expanded)
            : vscode.TreeItemCollapsibleState.None);
        item.iconPath = new vscode.ThemeIcon(node.icon);
        item.description = node.description;
        item.tooltip = node.tooltip;
        // Stable ids keep the expanded state when the tree refreshes.
        item.id = node.label + '|' + (node.command ?? '') + '|' + (node.checked === undefined ? '' : 'cb');
        if (node.checked !== undefined) {
            item.checkboxState = node.checked ? vscode.TreeItemCheckboxState.Checked : vscode.TreeItemCheckboxState.Unchecked;
        }
        if (node.command) {
            item.command = { command: node.command, title: node.label, arguments: node.commandArgs };
        }
        return item;
    }

    getChildren(node?: ToolNode): ToolNode[] {
        if (!node) {
            return [...ToolsMenu.slice(0, -1), ...this.contributions.flatMap(c => c.nodes()), ...ToolsMenu.slice(-1)];
        }
        return node.dynamicChildren ? node.dynamicChildren() : node.children ?? [];
    }
}

/** Registers the Dynamics 365 tools and the Application Explorer context actions. */
export function registerTools(
    context: vscode.ExtensionContext,
    server: XppServerClient,
    refreshTrees: () => void,
    command: (id: string, handler: (...args: any[]) => unknown) => void,
    contributions: ToolsContribution[] = [],
): void {
    const view = vscode.window.createTreeView('xpp.tools', { treeDataProvider: new ToolsTreeProvider(contributions) });
    context.subscriptions.push(view, view.onDidChangeCheckboxState(e => {
        for (const [node, state] of e.items) {
            node.onToggle?.(state === vscode.TreeItemCheckboxState.Checked);
        }
    }));

    command('xpp.tools.options', () => vscode.commands.executeCommand('workbench.action.openSettings', '@ext:MNazmi.xpp-vscode'));
    command('xpp.tools.createModel', () => createModel(server, refreshTrees));
    command('xpp.tools.updateModel', (arg?: TreeArg) => updateModel(server, arg?.model?.name));
    command('xpp.tools.modelDependencies', (arg?: TreeArg) => modelDependencies(arg?.model?.name));
    command('xpp.tools.createPackage', () => createDeploymentPackage());
    command('xpp.tools.findLabels', () => findLabels(server));
    command('xpp.addToProject', (arg?: TreeArg) => addToProject(server, arg));
    command('xpp.deleteObject', (arg?: TreeArg) => deleteObject(server, arg, refreshTrees));
    command('xpp.excludeFromProject', (arg?: TreeArg) => excludeFromProject(server, arg));
    command('xpp.createExtension', (arg?: TreeArg) => createExtension(server, arg, refreshTrees));
    command('xpp.createCodeExtension', (arg?: TreeArg) => createCodeExtension(server, arg, refreshTrees));
    command('xpp.findReferences', (arg?: TreeArg) => findReferences(server, arg));
    command('xpp.openInBrowser', (arg?: TreeArg) => openInBrowser(arg, false, server));
    command('xpp.openTableBrowser', (arg?: TreeArg) => openInBrowser(arg ?? { objectType: 'AxTable' }, true, server));
    command('xpp.tools.exportProject', () => exportProject(server));
    command('xpp.tools.importProject', () => importProject(server, refreshTrees));
    command('xpp.tools.findTagReferences', () => findTagReferences(server));
    command('xpp.compare', (arg?: TreeArg) => compareElements(server, arg, false));
    command('xpp.compareMetadata', (arg?: TreeArg) => compareElements(server, arg, true));
    command('xpp.viewHierarchy', async (arg?: TreeArg) => {
        const element = elementOf(arg) ?? await pickElement(server, 'View hierarchy', ['AxClass', 'AxTable']);
        if (element) {
            await hierarchyDiagram(server, element.objectType, element.objectName);
        }
    });
    command('xpp.viewErd', async (arg?: TreeArg) => {
        const element = elementOf(arg) ?? await pickElement(server, 'Entity relationship diagram', ['AxTable']);
        if (element) {
            await ErdPanel.show(server, element.objectName);
        }
    });
    command('xpp.viewReferences', async (arg?: TreeArg) => {
        const element = elementOf(arg) ?? await pickElement(server, 'View references');
        if (element) {
            await referencesDiagram(server, element.objectType, element.objectName, openReference);
        }
    });
    context.subscriptions.push(vscode.workspace.registerTextDocumentContentProvider(MetadataScheme, new MetadataDocuments(server)));
}

export function elementOf(arg: TreeArg | undefined): { objectType: string; objectName: string } | undefined {
    if (arg?.item) {
        return { objectType: arg.item.objectType, objectName: arg.item.objectName };
    }
    if (arg?.objectType && arg.entry) {
        return { objectType: arg.objectType, objectName: arg.entry.name };
    }
    return undefined;
}

/** Asks for an element when a command runs from the palette or the tools view rather than a tree node. */
export async function pickElement(server: XppServerClient, title: string, types?: string[]): Promise<{ objectType: string; objectName: string } | undefined> {
    const name = await vscode.window.showInputBox({ title, prompt: 'Element name (or part of it)', ignoreFocusOut: true });
    if (!name || name.trim().length < 3) {
        return undefined;
    }
    const result = await server.bridge('find_object', { objectName: name.trim(), maxResults: 50 });
    const matches: any[] = (result.matches ?? []).filter((o: any) => !types || types.includes(o.objectType));
    if (matches.length === 0) {
        vscode.window.showInformationMessage(`No element matching '${name}'.`);
        return undefined;
    }
    const picked = await vscode.window.showQuickPick(
        matches.map(o => ({ label: o.objectName, description: typeLabel(o.objectType), detail: o.modelName, element: o })),
        { title, matchOnDescription: true });
    return picked ? { objectType: picked.element.objectType, objectName: picked.label } : undefined;
}

async function customModels(): Promise<ModelLocation[]> {
    return (await listModels()).filter(m => m.isCustom);
}

/** The model new objects go to: the project's model, else one the user picks. */
export async function targetModel(title: string): Promise<string | undefined> {
    const project = await readProject().catch(() => undefined);
    if (project?.model) {
        return project.model;
    }
    const picked = await vscode.window.showQuickPick((await customModels()).map(m => ({ label: m.name, description: m.displayName })), { title: `${title}: model` });
    return picked?.label;
}

export async function addCreatedToProject(server: XppServerClient, objectType: string, objectName: string): Promise<void> {
    if (await findActiveProject()) {
        await server.bridge('add_to_project', { objectType, objectName });
    }
}

/** Label file IDs with their languages, read from the models' AxLabelFile folders. */
export async function listLabelFiles(customOnly = false): Promise<Map<string, Set<string>>> {
    const files = new Map<string, Set<string>>();
    for (const model of await listModels()) {
        if (customOnly && !model.isCustom) {
            continue;
        }
        const names = await fs.readdir(path.join(model.folder, 'AxLabelFile')).catch(() => [] as string[]);
        for (const file of names.filter(n => n.toLowerCase().endsWith('.xml'))) {
            const match = /^(.+)_([a-z]{2}(-[A-Za-z]+)?)\.xml$/i.exec(file);
            if (match) {
                const languages = files.get(match[1]) ?? new Set<string>();
                languages.add(match[2]);
                files.set(match[1], languages);
            }
        }
    }
    return files;
}

// ── Model management ──

async function packagePicker(server: XppServerClient, selected: string[], title: string): Promise<string[] | undefined> {
    const result = await server.bridge('list_packages', {});
    if (!result.success) {
        throw new Error(result.message);
    }
    const chosen = new Set(selected.map(s => s.toLowerCase()));
    const picked = await vscode.window.showQuickPick(
        (result.packages as any[]).map(p => ({ label: p.name, description: (p.models as string[]).join(', '), picked: chosen.has(String(p.name).toLowerCase()) })),
        { title, canPickMany: true, matchOnDescription: true, ignoreFocusOut: true });
    return picked?.map(p => p.label);
}

async function createModel(server: XppServerClient, refreshTrees: () => void): Promise<void> {
    const name = await vscode.window.showInputBox({
        title: 'Create model (1/4)', prompt: 'Model name', ignoreFocusOut: true,
        validateInput: v => /^[A-Za-z][A-Za-z0-9_]*$/.test(v) ? undefined : 'Letters, digits and underscores, starting with a letter.',
    });
    if (!name) {
        return;
    }
    const publisher = await vscode.window.showInputBox({ title: 'Create model (2/4)', prompt: 'Publisher', ignoreFocusOut: true });
    if (publisher === undefined) {
        return;
    }
    const layer = await vscode.window.showQuickPick(['USR', 'CUS', 'VAR', 'ISV'], { title: 'Create model (3/4): layer', ignoreFocusOut: true });
    if (!layer) {
        return;
    }
    const references = await packagePicker(server, ['ApplicationPlatform', 'ApplicationFoundation', 'ApplicationSuite'], 'Create model (4/4): referenced packages');
    if (!references) {
        return;
    }
    const result = await server.bridge('create_model', { name, displayName: name, publisher, layer, references });
    if (!result.success) {
        throw new Error(result.message);
    }
    refreshTrees();
    vscode.window.showInformationMessage(`${result.message} Create a project for it with X++: Select D365FO Project, or add objects to it from the agent tools.`);
}

async function chooseCustomModel(title: string, name?: string): Promise<string | undefined> {
    if (name) {
        return name;
    }
    const picked = await vscode.window.showQuickPick((await customModels()).map(m => ({ label: m.name, description: m.displayName })), { title });
    return picked?.label;
}

async function updateModel(server: XppServerClient, name?: string): Promise<void> {
    const modelName = await chooseCustomModel('Update model parameters', name);
    if (!modelName) {
        return;
    }
    const model = await server.bridge('get_model', { name: modelName });
    if (!model.success) {
        throw new Error(model.message);
    }
    if (!model.isCustom) {
        throw new Error(`${modelName} is not one of your models.`);
    }
    const displayName = await vscode.window.showInputBox({ title: `${modelName}: display name`, value: model.displayName, ignoreFocusOut: true });
    if (displayName === undefined) {
        return;
    }
    const description = await vscode.window.showInputBox({ title: `${modelName}: description`, value: model.description, ignoreFocusOut: true });
    if (description === undefined) {
        return;
    }
    const references = await packagePicker(server, model.references, `${modelName}: referenced packages`);
    if (!references) {
        return;
    }
    const result = await server.bridge('update_model', { name: modelName, displayName, description, references });
    if (!result.success) {
        throw new Error(result.message);
    }
    vscode.window.showInformationMessage(result.message);
}

/** Packages a model references (outgoing) and the models that reference its package (incoming). */
async function modelDependencies(name?: string): Promise<void> {
    const models = await listModels();
    const modelName = name ?? (await vscode.window.showQuickPick(models.map(m => ({ label: m.name, description: m.displayName })), { title: 'View model dependencies' }))?.label;
    const model = models.find(m => m.name === modelName);
    if (!model) {
        return;
    }
    const references = new Map<string, string[]>();
    await Promise.all(models.map(async m => references.set(m.name, await descriptorReferences(m))));
    const outgoing = references.get(model.name) ?? [];
    const incoming = models.filter(m => m.name !== model.name && (references.get(m.name) ?? []).some(r => r.toLowerCase() === model.packageName.toLowerCase()));
    const roots: DiagramNode[] = [
        { label: `References (${outgoing.length})`, children: outgoing.map(r => ({ label: r, description: 'package' })) },
        { label: `Referenced by (${incoming.length})`, children: incoming.map(m => ({ label: m.displayName, description: m.name })) },
    ];
    DiagramPanel.show(`Dependencies of ${model.displayName} [${model.name}], package ${model.packageName}`, roots);
}

async function descriptorReferences(model: ModelLocation): Promise<string[]> {
    const descriptor = path.join(path.dirname(model.folder), 'Descriptor', `${model.name}.xml`);
    const text = await fs.readFile(descriptor, 'utf8').catch(() => '');
    const block = /<ModuleReferences[^>]*>([\s\S]*?)<\/ModuleReferences>/.exec(text)?.[1] ?? '';
    return [...block.matchAll(/<[\w:]*string>([^<]+)<\/[\w:]*string>/g)].map(m => m[1].trim());
}

// ── Deploy ──

/** Runs the same script Visual Studio uses for Dynamics 365 > Deploy > Create Deployment Package. */
async function createDeploymentPackage(): Promise<void> {
    const env = await readActiveEnvironment();
    const picked = await vscode.window.showQuickPick((await customModels()).map(m => ({ label: m.packageName, description: m.displayName })), {
        title: 'Create deployment package: packages', canPickMany: true,
    });
    if (!picked?.length) {
        return;
    }
    const version = await vscode.window.showInputBox({ title: 'Package version', value: '1.0.0.0', validateInput: v => /^\d+(\.\d+){3}$/.test(v) ? undefined : 'Use a version like 1.0.0.0' });
    if (!version) {
        return;
    }
    const folder = await vscode.window.showOpenDialog({ canSelectFiles: false, canSelectFolders: true, title: 'Output folder for the deployable package', openLabel: 'Create package here' });
    if (!folder?.[0]) {
        return;
    }
    const script = path.join(env.binDirectory, 'RunPackageCreationAll.ps1');
    const names = picked.map(p => `'${p.label.replace(/'/g, "''")}'`).join(',');
    const quote = (p: string) => `'${p.replace(/'/g, "''")}'`;
    const commandLine = `& ${quote(script)} -packageNames @(${names}) -metadataDir ${quote(env.modelStoreFolder)} -modelDir ${quote(env.modelStoreFolder)} -binDir ${quote(env.binDirectory)} -packageVersion ${quote(version)} -outputDir ${quote(folder[0].fsPath)}`;
    const task = new vscode.Task({ type: 'xpp', task: 'createPackage' }, vscode.TaskScope.Workspace, 'Create deployment package', 'X++',
        new vscode.ProcessExecution('powershell.exe', ['-NoProfile', '-ExecutionPolicy', 'Bypass', '-Command', commandLine]));
    await vscode.tasks.executeTask(task);
}

// ── Labels ──

async function findLabels(server: XppServerClient): Promise<void> {
    const files = await listLabelFiles();
    const fileId = await vscode.window.showQuickPick([...files.keys()].sort((a, b) => a.localeCompare(b)), { title: 'Find labels: label file' });
    if (!fileId) {
        return;
    }
    const languages = [...(files.get(fileId) ?? [])].sort();
    const language = languages.includes('en-US') ? 'en-US' : languages[0];
    const text = await vscode.window.showInputBox({ title: `Find labels in ${fileId} (${language})`, prompt: 'Text to search for' });
    if (!text) {
        return;
    }
    const result = await server.bridge('read_label', { labelFileId: fileId, language, searchText: text, maxResults: '100' });
    if (!result.success) {
        throw new Error(result.message);
    }
    const reference = (id: string) => id.startsWith('@') ? id : `@${fileId}:${id}`;
    const picked = await vscode.window.showQuickPick(
        (result.labels as { id: string; text: string }[]).map(l => ({ label: l.text, description: reference(l.id) })),
        { title: `${result.labels.length} label(s) — select one to insert or copy its reference`, matchOnDescription: true });
    if (!picked?.description) {
        return;
    }
    const editor = vscode.window.activeTextEditor;
    if (editor && !editor.document.isUntitled && editor.document.uri.scheme === 'xpp') {
        await editor.edit(edit => edit.replace(editor.selection, `"${picked.description}"`));
    } else {
        await vscode.env.clipboard.writeText(picked.description);
        vscode.window.showInformationMessage(`Copied ${picked.description}.`);
    }
}

// ── Application Explorer actions ──

async function addToProject(server: XppServerClient, arg?: TreeArg): Promise<void> {
    const element = elementOf(arg) ?? await pickElement(server, 'Add to project');
    if (!element) {
        return;
    }
    if (!await findActiveProject()) {
        await vscode.commands.executeCommand('xpp.selectProject');
        if (!await findActiveProject()) {
            return;
        }
    }
    const result = await server.bridge('add_to_project', element);
    if (!result.success) {
        throw new Error(result.message);
    }
    await vscode.commands.executeCommand('xpp.refreshProject');
    vscode.window.showInformationMessage(`Added ${element.objectName} to the project.`);
}

/** Visual Studio's Delete: removes the object from its model (only your own models) and from the project. */
async function deleteObject(server: XppServerClient, arg: TreeArg | undefined, refreshTrees: () => void): Promise<void> {
    const element = elementOf(arg);
    if (!element) {
        return;
    }
    const confirm = await vscode.window.showWarningMessage(
        `Delete ${typeLabel(element.objectType)} '${element.objectName}' from its model? The metadata file is removed.`, { modal: true }, 'Delete');
    if (confirm !== 'Delete') {
        return;
    }
    let result = await server.tool('xpp_delete_object', element);
    if (result.isError && /still referenced/.test(result.text)) {
        const force = await vscode.window.showWarningMessage(result.text, { modal: true }, 'Delete Anyway');
        if (force !== 'Delete Anyway') {
            return;
        }
        result = await server.tool('xpp_delete_object', { ...element, force: true });
    }
    if (result.isError) {
        throw new Error(result.text);
    }
    refreshTrees();
    vscode.window.showInformationMessage(result.text.split('\n')[0]);
}

/** Visual Studio's Exclude From Project: the object stays in the model. */
async function excludeFromProject(server: XppServerClient, arg: TreeArg | undefined): Promise<void> {
    const element = elementOf(arg);
    if (!element) {
        return;
    }
    const result = await server.bridge('remove_from_project', element);
    if (!result.success) {
        throw new Error(result.message);
    }
    await vscode.commands.executeCommand('xpp.refreshProject');
}

async function createExtension(server: XppServerClient, arg: TreeArg | undefined, refreshTrees: () => void): Promise<void> {
    const element = elementOf(arg) ?? await pickElement(server, 'Create extension', Object.keys(ExtensionTypes));
    if (!element) {
        return;
    }
    const extensionType = ExtensionTypes[element.objectType];
    if (!extensionType) {
        throw new Error(`${typeLabel(element.objectType)} can't be extended this way.`);
    }
    const model = await targetModel('Create extension');
    if (!model) {
        return;
    }
    const name = await vscode.window.showInputBox({
        title: 'Create extension', prompt: 'Extension name', value: `${element.objectName}.${model}`,
        validateInput: v => v.startsWith(`${element.objectName}.`) ? undefined : `The name must start with '${element.objectName}.'`,
    });
    if (!name) {
        return;
    }
    const result = await server.bridge('create_object', { objectType: extensionType, objectName: name, modelName: model });
    if (!result.success) {
        throw new Error(result.message);
    }
    await addCreatedToProject(server, extensionType, name);
    refreshTrees();
    await vscode.commands.executeCommand('xpp.openDesigner', extensionType, name);
}

/** A Chain of Command class for the element, like Visual Studio's Create code extension. */
async function createCodeExtension(server: XppServerClient, arg: TreeArg | undefined, refreshTrees: () => void): Promise<void> {
    const element = elementOf(arg) ?? await pickElement(server, 'Create code extension', Object.keys(CodeExtensionFunctions));
    if (!element) {
        return;
    }
    const intrinsic = CodeExtensionFunctions[element.objectType];
    if (!intrinsic) {
        throw new Error(`${typeLabel(element.objectType)} can't have a code extension.`);
    }
    const model = await targetModel('Create code extension');
    if (!model) {
        return;
    }
    const name = await vscode.window.showInputBox({
        title: 'Create code extension', prompt: 'Class name (must end with _Extension)', value: `${element.objectName}_${model}_Extension`,
        validateInput: v => /^[A-Za-z_]\w*_Extension$/.test(v) ? undefined : 'Use a valid class name ending with _Extension.',
    });
    if (!name) {
        return;
    }
    const declaration = `[ExtensionOf(${intrinsic}(${element.objectName}))]\nfinal class ${name}\n{\n}`;
    const result = await server.bridge('create_object', { objectType: 'AxClass', objectName: name, modelName: model, declaration, formatCode: false });
    if (!result.success) {
        throw new Error(result.message);
    }
    await addCreatedToProject(server, 'AxClass', name);
    refreshTrees();
    await vscode.commands.executeCommand('vscode.open', objectUri('AxClass', name));
}

async function findReferences(server: XppServerClient, arg?: TreeArg): Promise<void> {
    const element = elementOf(arg) ?? await pickElement(server, 'Find references');
    if (!element) {
        return;
    }
    const result = await vscode.window.withProgress({ location: vscode.ProgressLocation.Notification, title: `Finding references to ${element.objectName}…` },
        () => server.bridge('where_used', { ...element, maxResults: '1000' }));
    if (!result.success) {
        throw new Error(result.message);
    }
    const refs: any[] = result.references;
    if (refs.length === 0) {
        vscode.window.showInformationMessage(`No references to ${element.objectName} from other elements.`);
        return;
    }
    const picked = await vscode.window.showQuickPick(
        refs.map(r => ({ label: `${r.objectName}`, description: `${r.member} · line ${r.line} · ${r.kind}`, detail: `${typeLabel(r.objectType ?? '')} · ${r.module}`, ref: r })),
        { title: `${refs.length}${result.more ? '+' : ''} reference(s) to ${element.objectName}`, matchOnDescription: true, matchOnDetail: true });
    if (picked) {
        await openReference(picked.ref);
    }
}

export async function openReference(ref: { objectType?: string; objectName: string; memberPath: string[]; line: number }): Promise<void> {
    if (!ref.objectType) {
        return;
    }
    const path = ref.memberPath ?? [];
    const nested = path.length > 2 && path[path.length - 2] === 'Methods';
    if (nested) {
        await vscode.window.showTextDocument(methodUri(ref.objectType, ref.objectName, path));
        return;
    }
    if (!CodeTypes.has(ref.objectType)) {
        await vscode.commands.executeCommand('xpp.openDesigner', ref.objectType, ref.objectName);
        return;
    }
    const document = await vscode.workspace.openTextDocument(objectUri(ref.objectType, ref.objectName));
    const line = ref.line > 0 ? compilerLineToDocumentLine(document.getText(), ref.line) : 0;
    const position = new vscode.Position(Math.min(line, document.lineCount - 1), 0);
    await vscode.window.showTextDocument(document, { selection: new vscode.Range(position, position) });
}

/** The web client URL for an element: menu items, class runner and table browser, like Visual Studio's startup object. */
export async function openInBrowser(arg: TreeArg | undefined, tableBrowser: boolean, server: XppServerClient): Promise<void> {
    let element = elementOf(arg) ?? (tableBrowser
        ? await pickTable()
        : undefined);
    if (!element) {
        if (!tableBrowser) {
            vscode.window.showInformationMessage('Right-click a form, menu item, class or table in Application Explorer and choose Open in Browser.');
        }
        return;
    }
    if (element.objectType === 'AxForm' && !tableBrowser) {
        element = await menuItemForForm(server, element.objectName);
        if (!element) {
            return;
        }
    }
    const baseUrl = await financeAndOperationsUrl();
    if (!baseUrl) {
        return;
    }
    const name = encodeURIComponent(element.objectName);
    let query: string;
    switch (tableBrowser ? 'AxTable' : element.objectType) {
        case 'AxMenuItemDisplay': query = `mi=${name}`; break;
        case 'AxMenuItemOutput': query = `mi=output:${name}`; break;
        case 'AxMenuItemAction': query = `mi=action:${name}`; break;
        case 'AxClass': query = `mi=SysClassRunner&cls=${name}`; break;
        case 'AxTable': query = `mi=SysTableBrowser&tableName=${name}`; break;
        default: throw new Error(`${typeLabel(element.objectType)} can't be opened in the browser.`);
    }
    await vscode.env.openExternal(vscode.Uri.parse(`${baseUrl}/?${query}`));
}

async function pickTable(): Promise<{ objectType: string; objectName: string } | undefined> {
    const name = await vscode.window.showInputBox({ title: 'Open table browser', prompt: 'Table name', validateInput: v => /^\w+$/.test(v) ? undefined : 'Enter a table name.' });
    return name ? { objectType: 'AxTable', objectName: name } : undefined;
}

/** The web client of the active environment; xpp.financeAndOperationsUrl when its configuration has none. */
async function financeAndOperationsUrl(): Promise<string | undefined> {
    const env = await readActiveEnvironment().catch(() => undefined);
    // A selected environment without metadata here runs on another environment's configuration, which names the wrong web client.
    if (env?.finOpsUrl && (env.dataverseUrl || !selectedEnvironmentUrl())) {
        return env.finOpsUrl;
    }
    const config = vscode.workspace.getConfiguration('xpp');
    let url = config.get<string>('financeAndOperationsUrl');
    if (!url) {
        url = await vscode.window.showInputBox({
            title: 'Finance and operations URL', prompt: 'URL of your environment\'s web client', placeHolder: 'https://yourorg.operations.dynamics.com',
            validateInput: v => /^https:\/\/[^\s/]+/.test(v) ? undefined : 'Enter an https URL.', ignoreFocusOut: true,
        });
        if (!url) {
            return undefined;
        }
        await config.update('financeAndOperationsUrl', url, vscode.ConfigurationTarget.Global);
    }
    return url.replace(/\/+$/, '');
}

/** A form opens in the web client through a display menu item that references it. */
async function menuItemForForm(server: XppServerClient, form: string): Promise<{ objectType: string; objectName: string } | undefined> {
    const result = await server.bridge('where_used', { objectType: 'AxForm', objectName: form, maxResults: '500' });
    const items = result.success
        ? [...new Set((result.references as any[]).filter(r => r.objectType === 'AxMenuItemDisplay').map(r => String(r.objectName)))]
        : [];
    if (items.length === 0) {
        vscode.window.showWarningMessage(`No display menu item opens ${form}. Create one, then open it in the browser.`);
        return undefined;
    }
    const name = items.length === 1 ? items[0] : await vscode.window.showQuickPick(items, { title: `Display menu items that open ${form}` });
    return name ? { objectType: 'AxMenuItemDisplay', objectName: name } : undefined;
}

// ── Export / import project (.axpp, Visual Studio's format) ──

async function exportProject(server: XppServerClient): Promise<void> {
    const projectFile = await findActiveProject();
    if (!projectFile) {
        throw new Error('Select a D365FO project first (X++: Select D365FO Project).');
    }
    const project = await readProject(projectFile);
    const target = await vscode.window.showSaveDialog({
        title: `Export project ${project?.name ?? ''}`, filters: { 'Dynamics 365 project export': ['axpp'] },
        defaultUri: vscode.Uri.file(path.join(path.dirname(projectFile), `${project?.name ?? 'project'}.axpp`)),
    });
    if (!target) {
        return;
    }
    const result = await server.bridge('export_project', { projectFile, outputFile: target.fsPath });
    if (!result.success) {
        throw new Error(result.message);
    }
    vscode.window.showInformationMessage(result.message);
}

async function importProject(server: XppServerClient, refreshTrees: () => void): Promise<void> {
    const files = await vscode.window.showOpenDialog({ title: 'Import project', filters: { 'Dynamics 365 project export': ['axpp'] }, canSelectMany: false });
    if (!files?.[0]) {
        return;
    }
    const file = files[0].fsPath;
    const info = await server.bridge('inspect_project_package', { file });
    if (!info.success) {
        throw new Error(info.message);
    }
    const existing = (info.items as any[]).filter(i => i.exists).length;
    const summary = `${info.project}: ${info.items.length} element(s) for model ${info.model}${info.modelExists ? '' : ' (the model will be created)'}.`
        + (existing ? ` ${existing} already exist.` : '');
    const choice = await vscode.window.showInformationMessage(summary, { modal: true }, ...(existing ? ['Import and overwrite', 'Import new only'] : ['Import']));
    if (!choice) {
        return;
    }
    const folder = await vscode.window.showOpenDialog({ title: 'Folder for the project', canSelectFiles: false, canSelectFolders: true, openLabel: 'Import here' });
    if (!folder?.[0]) {
        return;
    }
    const result = await server.bridge('import_project', { file, targetFolder: folder[0].fsPath, overwrite: String(choice === 'Import and overwrite') });
    if (!result.success) {
        throw new Error(result.message);
    }
    refreshTrees();
    if (await vscode.window.showInformationMessage(result.message, 'Use this project')) {
        await vscode.workspace.getConfiguration('xpp').update('project', result.projectFile, vscode.ConfigurationTarget.Global);
    }
}

// ── Tags, compare ──

async function findTagReferences(server: XppServerClient): Promise<void> {
    const filter = await vscode.window.showInputBox({ title: 'Find tag references', prompt: 'Tag (or part of it)' });
    if (filter === undefined) {
        return;
    }
    const tags = await server.bridge('list_tags', { filter });
    if (!tags.success) {
        throw new Error(tags.message);
    }
    const list = tags.tags as string[];
    const tag = list.length === 1 ? list[0] : await vscode.window.showQuickPick(list, { title: list.length ? 'Tags' : 'No tags found' });
    if (!tag) {
        return;
    }
    const result = await server.bridge('tag_references', { tag });
    if (!result.success) {
        throw new Error(result.message);
    }
    const picked = await vscode.window.showQuickPick(
        (result.references as any[]).map(r => ({ label: String(r.objectName), description: r.member, detail: `${typeLabel(r.objectType ?? '')} · ${r.module}`, ref: r })),
        { title: `${result.references.length} element(s) tagged '${tag}'`, matchOnDescription: true });
    if (picked?.ref.objectType) {
        await vscode.commands.executeCommand('xpp.openDesigner', picked.ref.objectType, picked.ref.objectName);
    }
}

/** Compare the code (or metadata) of two elements in the diff editor. */
async function compareElements(server: XppServerClient, arg: TreeArg | undefined, metadata: boolean): Promise<void> {
    const left = elementOf(arg) ?? await pickElement(server, metadata ? 'Compare metadata: first element' : 'Compare code: first element');
    if (!left) {
        return;
    }
    const right = await pickElement(server, `Compare ${left.objectName} with`, [left.objectType]);
    if (!right) {
        return;
    }
    const useCode = !metadata && CodeTypes.has(left.objectType);
    const uri = (e: { objectType: string; objectName: string }) => useCode ? objectUri(e.objectType, e.objectName) : MetadataDocuments.uri(e.objectType, e.objectName);
    await vscode.commands.executeCommand('vscode.diff', uri(left), uri(right), `${left.objectName} ↔ ${right.objectName}${useCode ? '' : ' (metadata)'}`);
}
