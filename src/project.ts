import { createHash, randomUUID } from 'crypto';
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

/**
 * The project of this window: the xpp.project setting of the open workspace (written by Open Project into the
 * project's own workspace). User settings and a .rnrproj that happens to be in the folder don't select a project.
 */
export async function findActiveProject(): Promise<string | undefined> {
    return vscode.workspace.getConfiguration('xpp').inspect<string>('project')?.workspaceValue || undefined;
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

const RecentProjectsKey = 'xpp.recentProjects';
const MaxRecentProjects = 10;

/**
 * Opening a project opens its workspace: the project folder (.rnrproj, .slnx, source control) and its model folder
 * (the metadata the tools edit), with the project in the workspace's settings. Other windows have no project.
 * The workspace files live in the extension's storage, so repositories stay clean; VS Code's Open Recent lists them too.
 */
export class ProjectWorkspaces {
    constructor(private readonly context: vscode.ExtensionContext) { }

    /** Projects opened before, newest first (only those that still exist). */
    async recent(): Promise<string[]> {
        const all = this.context.globalState.get<string[]>(RecentProjectsKey, []);
        const existing: string[] = [];
        for (const file of all) {
            if (await fs.stat(file).then(s => s.isFile(), () => false)) {
                existing.push(file);
            }
        }
        return existing;
    }

    async open(file: string): Promise<void> {
        const workspaceFile = await this.prepare(file);
        if (workspaceFile && vscode.workspace.workspaceFile?.fsPath.toLowerCase() !== workspaceFile.fsPath.toLowerCase()) {
            await vscode.commands.executeCommand('vscode.openFolder', workspaceFile, { forceNewWindow: false });
        }
    }

    /** Writes the project's workspace file and remembers the project; returns the workspace file. */
    async prepare(file: string): Promise<vscode.Uri | undefined> {
        const project = await readProject(file);
        if (!project) {
            return undefined;
        }
        const model = (await listModels()).find(m => m.name.toLowerCase() === (project.model ?? '').toLowerCase());
        const workspaceFile = await this.writeWorkspace(project, model?.folder);
        await this.remember(file);
        return workspaceFile;
    }

    /** Picks a project: recent ones first, then the ones in Visual Studio's repos folder and the workspace, or browse. */
    async select(): Promise<void> {
        const recent = await this.recent();
        const files = [...recent];
        const found: string[] = [];
        await findProjectFiles(path.join(os.homedir(), 'source', 'repos'), 4, found);
        for (const uri of await vscode.workspace.findFiles('**/*.rnrproj', '**/{node_modules,bin,obj}/**', 50)) {
            found.push(uri.fsPath);
        }
        for (const file of found) {
            if (!files.some(f => f.toLowerCase() === file.toLowerCase())) {
                files.push(file);
            }
        }
        const browse = { label: '$(folder-opened) Browse…', description: '', file: '' };
        const items = files.map(file => ({
            label: path.basename(file, '.rnrproj'),
            description: path.dirname(file),
            detail: recent.includes(file) ? 'recently opened' : undefined,
            file,
        }));
        const picked = await vscode.window.showQuickPick([...items, browse], { title: 'Open D365FO project (.rnrproj)', matchOnDescription: true });
        if (!picked) {
            return;
        }
        let file = picked.file;
        if (!file) {
            const chosen = await vscode.window.showOpenDialog({ canSelectMany: false, filters: { 'D365FO project': ['rnrproj'] }, title: 'Open D365FO project' });
            if (!chosen?.[0]) {
                return;
            }
            file = chosen[0].fsPath;
        }
        await this.open(file);
    }

    async openRecent(): Promise<void> {
        const recent = await this.recent();
        if (recent.length === 0) {
            vscode.window.showInformationMessage('No recent D365FO projects. Open one with Open D365FO Project.');
            return;
        }
        const picked = await vscode.window.showQuickPick(
            recent.map(file => ({ label: path.basename(file, '.rnrproj'), description: path.dirname(file), file })),
            { title: 'Open recent D365FO project', matchOnDescription: true });
        if (picked) {
            await this.open(picked.file);
        }
    }

    /** Closes the project's workspace; the window keeps running without a project. */
    async close(): Promise<void> {
        if (await findActiveProject()) {
            await vscode.commands.executeCommand('workbench.action.closeFolder');
        }
    }

    /** Earlier versions kept the project in the user settings, so every window opened it; it becomes a recent project. */
    async migrateUserSetting(): Promise<void> {
        const config = vscode.workspace.getConfiguration('xpp');
        const global = config.inspect<string>('project')?.globalValue;
        if (global) {
            await this.remember(global);
            await config.update('project', undefined, vscode.ConfigurationTarget.Global);
        }
    }

    private async remember(file: string): Promise<void> {
        const recent = this.context.globalState.get<string[]>(RecentProjectsKey, []).filter(f => f.toLowerCase() !== file.toLowerCase());
        await this.context.globalState.update(RecentProjectsKey, [file, ...recent].slice(0, MaxRecentProjects));
    }

    /** Writes the project's workspace file, keeping folders and settings the user added to it. */
    private async writeWorkspace(project: XppProjectInfo, modelFolder: string | undefined): Promise<vscode.Uri> {
        const folder = vscode.Uri.joinPath(this.context.globalStorageUri, 'workspaces');
        await fs.mkdir(folder.fsPath, { recursive: true });
        const id = createHash('sha256').update(project.file.toLowerCase()).digest('hex').slice(0, 8);
        const uri = vscode.Uri.joinPath(folder, `${project.name}-${id}.code-workspace`);
        const existing = JSON.parse(await fs.readFile(uri.fsPath, 'utf8').catch(() => '{}')) as {
            folders?: { path: string; name?: string }[];
            settings?: Record<string, unknown>;
        };
        const ours: { path: string; name: string }[] = [{ path: path.dirname(project.file), name: `${project.name} (project)` }];
        if (modelFolder) {
            ours.push({ path: modelFolder, name: `${project.model} (model)` });
        }
        const added = (existing.folders ?? []).filter(f => !f.name?.endsWith(' (project)') && !f.name?.endsWith(' (model)'));
        const content = { ...existing, folders: [...ours, ...added], settings: { ...existing.settings, 'xpp.project': project.file } };
        await fs.writeFile(uri.fsPath, JSON.stringify(content, null, 2), 'utf8');
        return uri;
    }
}

const ProjectTypeGuid = 'fc65038c-1b2f-41e1-a629-bed71d161fff';

/** Creates a D365FO project (.rnrproj plus .slnx, as Visual Studio does) for one of your models; returns its file. */
export async function createProject(): Promise<string | undefined> {
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
    vscode.window.showInformationMessage(`Created D365FO project ${name} for model ${model.model.name}.`);
    return file;
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
