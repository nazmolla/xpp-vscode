import { randomUUID } from 'crypto';
import { promises as fs, watch as fsWatch, FSWatcher } from 'fs';
import * as os from 'os';
import * as path from 'path';
import * as vscode from 'vscode';
import { elementContextValue, elementOpenCommand, iconFor as aotIcon, listModels } from './aot';
import { objectUri } from './codeView';

export interface ProjectItem {
    objectType: string;
    objectName: string;
    folder: string;
}

export interface XppProjectInfo {
    file: string;
    name: string;
    model?: string;
    items: ProjectItem[];
}

/** The .rnrproj new objects are added to: the xpp.project setting, else the first one in the workspace. */
export async function findActiveProject(): Promise<string | undefined> {
    const configured = vscode.workspace.getConfiguration('xpp').get<string>('project');
    if (configured) {
        return configured;
    }
    const found = await vscode.workspace.findFiles('**/*.rnrproj', '**/{node_modules,bin,obj}/**', 2);
    return found[0]?.fsPath;
}

export async function readProject(file?: string): Promise<XppProjectInfo | undefined> {
    const projectFile = file ?? await findActiveProject();
    if (!projectFile) {
        return undefined;
    }
    const xml = await fs.readFile(projectFile, 'utf8');
    const model = /<Model>\s*([^<]+?)\s*<\/Model>/.exec(xml)?.[1];
    const items: ProjectItem[] = [];
    for (const m of xml.matchAll(/<Content\s+Include="([^"\\]+)\\([^"]+)"\s*>([\s\S]*?)<\/Content>/g)) {
        const link = /<Link>([^<]*)<\/Link>/.exec(m[3])?.[1];
        items.push({
            objectType: m[1],
            objectName: m[2],
            folder: link ? link.split('\\')[0] : m[1],
        });
    }
    return { file: projectFile, name: path.basename(projectFile, '.rnrproj'), model, items };
}

async function findProjectFiles(dir: string, depth: number, found: string[]): Promise<void> {
    if (depth < 0 || found.length >= 200) {
        return;
    }
    const entries = await fs.readdir(dir, { withFileTypes: true }).catch(() => []);
    for (const entry of entries) {
        const full = path.join(dir, entry.name);
        if (entry.isDirectory() && !/^(\.|node_modules$|bin$|obj$)/i.test(entry.name)) {
            await findProjectFiles(full, depth - 1, found);
        } else if (entry.isFile() && entry.name.toLowerCase().endsWith('.rnrproj')) {
            found.push(full);
        }
    }
}

/** Lets the user pick the project from Visual Studio's repos folder or the workspace; saved in xpp.project. */
export async function selectProject(): Promise<void> {
    const files: string[] = [];
    await findProjectFiles(path.join(os.homedir(), 'source', 'repos'), 4, files);
    for (const uri of await vscode.workspace.findFiles('**/*.rnrproj', '**/{node_modules,bin,obj}/**', 50)) {
        if (!files.some(f => f.toLowerCase() === uri.fsPath.toLowerCase())) {
            files.push(uri.fsPath);
        }
    }
    const browse = { label: '$(folder-opened) Browse…', file: '' };
    const picked = await vscode.window.showQuickPick(
        [...files.map(file => ({ label: path.basename(file, '.rnrproj'), description: path.dirname(file), file })), browse],
        { title: 'Select the D365FO project (.rnrproj)', matchOnDescription: true });
    if (!picked) {
        return;
    }
    let file = picked.file;
    if (!file) {
        const chosen = await vscode.window.showOpenDialog({ canSelectMany: false, filters: { 'D365FO project': ['rnrproj'] }, title: 'Select the D365FO project' });
        if (!chosen?.[0]) {
            return;
        }
        file = chosen[0].fsPath;
    }
    const config = vscode.workspace.getConfiguration('xpp');
    const unchanged = (config.get<string>('project') ?? '').toLowerCase() === file.toLowerCase();
    await config.update('project', file, vscode.ConfigurationTarget.Global);
    // Re-selecting the configured project changes no setting, so the workspace is not synced by the setting listener.
    if (unchanged) {
        await syncWorkspaceFolders();
    }
}

const ProjectTypeGuid = 'fc65038c-1b2f-41e1-a629-bed71d161fff';

/** Creates a D365FO project (.rnrproj plus .slnx, as Visual Studio does) for one of your models and selects it. */
export async function createProject(): Promise<void> {
    const models = (await listModels()).filter(m => m.isCustom);
    if (models.length === 0) {
        throw new Error('No custom model found in the active configuration. Create a model first (Visual Studio: Dynamics 365 > Model Management).');
    }
    const model = await vscode.window.showQuickPick(
        models.map(m => ({ label: m.name, description: m.displayName === m.name ? m.packageName : `${m.displayName} (${m.packageName})`, model: m })),
        { title: 'Create D365FO project (1/3): model', placeHolder: 'The model the project\'s objects belong to' });
    if (!model) {
        return;
    }
    const name = await vscode.window.showInputBox({
        title: 'Create D365FO project (2/3): name',
        value: `${model.model.name}Project`,
        validateInput: v => /^[A-Za-z_][A-Za-z0-9_.-]*$/.test(v) ? undefined : 'Letters, digits, _ . - only; start with a letter.',
    });
    if (!name) {
        return;
    }
    const defaultParent = path.join(os.homedir(), 'source', 'repos');
    const where = await vscode.window.showQuickPick(
        [{ label: defaultParent, description: 'Visual Studio\'s repos folder', dir: defaultParent }, { label: '$(folder-opened) Browse…', description: '', dir: '' }],
        { title: `Create D365FO project (3/3): parent folder (the project goes to <parent>\\${name})` });
    if (!where) {
        return;
    }
    let parent = where.dir;
    if (!parent) {
        const chosen = await vscode.window.showOpenDialog({ canSelectFolders: true, canSelectFiles: false, title: 'Parent folder for the project' });
        if (!chosen?.[0]) {
            return;
        }
        parent = chosen[0].fsPath;
    }
    const dir = path.join(parent, name);
    const file = path.join(dir, `${name}.rnrproj`);
    if (await fs.stat(file).then(() => true, () => false)) {
        throw new Error(`${file} already exists. Select it with "Select D365FO Project".`);
    }
    await fs.mkdir(dir, { recursive: true });
    const guid = newGuid();
    await fs.writeFile(file, projectXml(name, model.model.name, guid), 'utf8');
    await fs.writeFile(path.join(dir, `${name}.slnx`),
        `<Solution>\r\n  <Configurations>\r\n    <BuildType Name="Debug" />\r\n  </Configurations>\r\n  <Project Path="${name}.rnrproj" Type="${ProjectTypeGuid}" Id="${guid}" />\r\n</Solution>\r\n`, 'utf8');
    await vscode.workspace.getConfiguration('xpp').update('project', file, vscode.ConfigurationTarget.Global);
    vscode.window.showInformationMessage(`Created D365FO project ${name} for model ${model.model.name}.`);
}

function newGuid(): string {
    return randomUUID();
}

function projectXml(name: string, model: string, guid: string): string {
    return `<?xml version="1.0" encoding="utf-8"?>
<Project ToolsVersion="14.0" DefaultTargets="Build" xmlns="http://schemas.microsoft.com/developer/msbuild/2003">
  <PropertyGroup>
    <Configuration Condition=" '$(Configuration)' == '' ">Debug</Configuration>
    <Platform Condition=" '$(Platform)' == '' ">AnyCPU</Platform>
    <BuildTasksDirectory Condition=" '$(BuildTasksDirectory)' == ''">$(MSBuildProgramFiles32)\\MSBuild\\Microsoft\\Dynamics\\AX</BuildTasksDirectory>
    <Model>${model}</Model>
    <TargetFrameworkVersion>v4.8</TargetFrameworkVersion>
    <OutputPath>bin</OutputPath>
    <SchemaVersion>2.0</SchemaVersion>
    <GenerateCrossReferences>True</GenerateCrossReferences>
    <RunAppCheckerRules>False</RunAppCheckerRules>
    <LogAppcheckerDiagsAsErrors>False</LogAppcheckerDiagsAsErrors>
    <DeployOnline>False</DeployOnline>
    <ProjectGuid>{${guid}}</ProjectGuid>
    <Name>${name}</Name>
    <RootNamespace>${name}</RootNamespace>
    <StartupType>Class</StartupType>
  </PropertyGroup>
  <PropertyGroup Condition="'$(Configuration)|$(Platform)' == 'Debug|AnyCPU'">
    <Configuration>Debug</Configuration>
    <DBSyncInBuild>False</DBSyncInBuild>
    <GenerateFormAdaptors>False</GenerateFormAdaptors>
    <Company></Company>
    <Partition>initial</Partition>
    <PlatformTarget>AnyCPU</PlatformTarget>
    <DataEntityExpandParentChildRelations>False</DataEntityExpandParentChildRelations>
    <DataEntityUseLabelTextAsFieldName>False</DataEntityUseLabelTextAsFieldName>
  </PropertyGroup>
  <PropertyGroup Condition=" '$(Configuration)' == 'Debug' ">
    <DebugSymbols>true</DebugSymbols>
    <EnableUnmanagedDebugging>false</EnableUnmanagedDebugging>
  </PropertyGroup>
  <Import Project="$(MSBuildBinPath)\\Microsoft.Common.targets" />
  <Import Project="$(BuildTasksDirectory)\\Microsoft.Dynamics.Framework.Tools.BuildTasks.17.0.targets" />
</Project>
`.replace(/\r?\n/g, '\r\n');
}

/**
 * Makes the workspace show the active project: its folder (.rnrproj, .slnx, instructions, source control) and the
 * folder of its model (the metadata the tools edit), as a multi-root workspace. Folders of earlier projects and
 * models are replaced; other folders stay.
 */
export async function syncWorkspaceFolders(): Promise<void> {
    const project = await readProject().catch(() => undefined);
    if (!project) {
        return;
    }
    const models = await listModels();
    const model = models.find(m => m.name.toLowerCase() === (project.model ?? '').toLowerCase());
    const wanted: { uri: vscode.Uri; name: string }[] = [{ uri: vscode.Uri.file(path.dirname(project.file)), name: `${project.name} (project)` }];
    if (model) {
        wanted.push({ uri: vscode.Uri.file(model.folder), name: `${model.name} (model)` });
    }

    const same = (a: vscode.Uri, b: vscode.Uri) => a.fsPath.toLowerCase() === b.fsPath.toLowerCase();
    const current = vscode.workspace.workspaceFolders ?? [];
    if (wanted.every((w, i) => current[i] && same(current[i].uri, w.uri))) {
        return;
    }
    const modelFolders = models.map(m => m.folder.toLowerCase());
    const isOurs = async (f: vscode.WorkspaceFolder) =>
        modelFolders.includes(f.uri.fsPath.toLowerCase())
        || (await fs.readdir(f.uri.fsPath).catch(() => [] as string[])).some(n => n.toLowerCase().endsWith('.rnrproj'));
    const keep: { uri: vscode.Uri; name?: string }[] = [];
    for (const folder of current) {
        if (!wanted.some(w => same(w.uri, folder.uri)) && !await isOurs(folder)) {
            keep.push({ uri: folder.uri, name: folder.name });
        }
    }
    vscode.workspace.updateWorkspaceFolders(0, current.length, ...wanted, ...keep);
}

type ProjectNode = { kind: 'folder'; folder: string; items: ProjectItem[] } | { kind: 'item'; item: ProjectItem };

/** Solution-explorer style view of the active project, grouped by folder. */
export class ProjectTreeProvider implements vscode.TreeDataProvider<ProjectNode>, vscode.Disposable {
    private readonly changed = new vscode.EventEmitter<void>();
    readonly onDidChangeTreeData = this.changed.event;
    view?: vscode.TreeView<ProjectNode>;
    private watcher?: FSWatcher;
    private watchedFile?: string;
    private debounce?: NodeJS.Timeout;

    refresh(): void {
        this.changed.fire();
    }

    dispose(): void {
        this.watcher?.close();
        this.changed.dispose();
    }

    /** Tools, the MCP server and other editors write the .rnrproj directly; follow the file itself. */
    private watch(file: string | undefined): void {
        if (file?.toLowerCase() === this.watchedFile?.toLowerCase()) {
            return;
        }
        this.watcher?.close();
        this.watcher = undefined;
        this.watchedFile = file;
        if (!file) {
            return;
        }
        try {
            // Watch the folder: writers may replace the file rather than change it in place.
            this.watcher = fsWatch(path.dirname(file), (_event, name) => {
                if (name && name.toString().toLowerCase() !== path.basename(file).toLowerCase()) {
                    return;
                }
                clearTimeout(this.debounce);
                this.debounce = setTimeout(() => this.refresh(), 400);
            });
            this.watcher.on('error', () => { this.watcher?.close(); this.watcher = undefined; this.watchedFile = undefined; });
        } catch {
            this.watchedFile = undefined;
        }
    }

    getTreeItem(node: ProjectNode): vscode.TreeItem {
        if (node.kind === 'folder') {
            const item = new vscode.TreeItem(node.folder, vscode.TreeItemCollapsibleState.Expanded);
            item.iconPath = vscode.ThemeIcon.Folder;
            item.description = String(node.items.length);
            return item;
        }
        const { objectType, objectName } = node.item;
        const item = new vscode.TreeItem(objectName, vscode.TreeItemCollapsibleState.None);
        item.description = objectType;
        // Project items always belong to the project's own (custom) model.
        item.contextValue = elementContextValue(objectType, true);
        item.iconPath = new vscode.ThemeIcon(iconFor(objectType));
        item.command = elementOpenCommand(objectType, objectName);
        return item;
    }

    async getChildren(node?: ProjectNode): Promise<ProjectNode[]> {
        if (node?.kind === 'folder') {
            return node.items
                .sort((a, b) => a.objectName.localeCompare(b.objectName))
                .map(item => ({ kind: 'item', item }));
        }
        if (node) {
            return [];
        }
        const project = await readProject().catch(() => undefined);
        this.watch(project?.file);
        // The welcome text depends on it: "no project selected" vs. "the project is empty".
        void vscode.commands.executeCommand('setContext', 'xpp.hasProject', !!project);
        if (this.view) {
            this.view.description = project ? `${project.name}${project.model ? ` (${project.model})` : ''}` : undefined;
        }
        if (!project) {
            return [];
        }
        const folders = new Map<string, ProjectItem[]>();
        for (const item of project.items) {
            folders.set(item.folder, [...(folders.get(item.folder) ?? []), item]);
        }
        return [...folders.entries()]
            .sort(([a], [b]) => a.localeCompare(b))
            .map(([folder, items]) => ({ kind: 'folder', folder, items }));
    }
}

function iconFor(objectType: string): string {
    return aotIcon(objectType);
}
