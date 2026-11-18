import { createHash, randomUUID } from 'crypto';
import { promises as fs, watch as fsWatch, FSWatcher } from 'fs';
import * as os from 'os';
import * as path from 'path';
import * as vscode from 'vscode';
import { elementContextValue, elementOpenCommand, iconFor as aotIcon, listModels, typeName } from './aot';
import { objectUri } from './codeView';
import { csharpProjectTree, FileNode, findSolution, projectReferences, ReferenceInfo, SolutionInfo } from './solution';

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
        const csharp = (await solutionCsharpProjects(file).catch(() => ({ projects: [] as { file: string }[] }))).projects.map(p => path.dirname(p.file));
        const workspaceFile = await this.writeWorkspace(project, model?.folder, csharp);
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

    /**
     * Writes the project's workspace file, keeping folders and settings the user added to it. The C# projects of the
     * solution (or that the project references) are folders too, so the C# extension loads them.
     */
    private async writeWorkspace(project: XppProjectInfo, modelFolder: string | undefined, csharpFolders: string[] = []): Promise<vscode.Uri> {
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
        for (const folder of csharpFolders) {
            if (!ours.some(f => f.path.toLowerCase() === folder.toLowerCase())) {
                ours.push({ path: folder, name: `${path.basename(folder)} (C#)` });
            }
        }
        const added = (existing.folders ?? []).filter(f => !f.name?.endsWith(' (project)') && !f.name?.endsWith(' (model)') && !f.name?.endsWith(' (C#)'));
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

type ProjectNode =
    | { kind: 'xppProject'; file: string; name: string; model?: string; active: boolean }
    | { kind: 'references'; project: string }
    | { kind: 'reference'; ref: ReferenceInfo }
    | { kind: 'folder'; folder: string; items: ProjectItem[]; active: boolean }
    | { kind: 'item'; item: ProjectItem; active: boolean }
    | { kind: 'hint'; label: string; command: string }
    | { kind: 'csProject'; file: string; name: string; inSolution: boolean }
    | { kind: 'file'; node: FileNode; project: string };

/** The C# projects of a project's solution and those the project references, with the solution (if any). */
export async function solutionCsharpProjects(projectFile: string): Promise<{ solution?: SolutionInfo; projects: { file: string; inSolution: boolean }[] }> {
    const solution = await findSolution(projectFile);
    const xml = await fs.readFile(projectFile, 'utf8').catch(() => '');
    const projects = new Map<string, { file: string; inSolution: boolean }>();
    for (const file of solution?.projects.filter(p => /\.csproj$/i.test(p)) ?? []) {
        projects.set(file.toLowerCase(), { file, inSolution: true });
    }
    for (const ref of projectReferences(projectFile, xml).filter(r => r.kind === 'project' && r.path && r.exists)) {
        if (!projects.has(ref.path!.toLowerCase())) {
            projects.set(ref.path!.toLowerCase(), { file: ref.path!, inSolution: false });
        }
    }
    return { solution, projects: [...projects.values()] };
}

/**
 * Visual Studio's Solution Explorer for the open project: the X++ project (its references and elements by folder), the
 * other X++ projects of its solution, and the C# projects of the solution or that the project references, with their
 * files to edit.
 */
export class ProjectTreeProvider implements vscode.TreeDataProvider<ProjectNode>, vscode.Disposable {
    private readonly changed = new vscode.EventEmitter<void>();
    readonly onDidChangeTreeData = this.changed.event;
    view?: vscode.TreeView<ProjectNode>;
    private watcher?: FSWatcher;
    private watchedFile?: string;
    private debounce?: NodeJS.Timeout;
    private readonly folderWatchers = new Map<string, vscode.Disposable>();

    refresh(): void {
        this.changed.fire();
    }

    dispose(): void {
        this.watcher?.close();
        this.folderWatchers.forEach(w => w.dispose());
        this.changed.dispose();
    }

    private refreshSoon(): void {
        clearTimeout(this.debounce);
        this.debounce = setTimeout(() => this.refresh(), 400);
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
                this.refreshSoon();
            });
            this.watcher.on('error', () => { this.watcher?.close(); this.watcher = undefined; this.watchedFile = undefined; });
        } catch {
            this.watchedFile = undefined;
        }
    }

    /** Files added to or removed from the C# projects (and the solution file) show up without a refresh; build output doesn't count. */
    private watchFolders(folders: { folder: string; pattern: string }[]): void {
        const wanted = new Set(folders.map(f => `${f.folder.toLowerCase()}|${f.pattern}`));
        for (const [key, watcher] of this.folderWatchers) {
            if (!wanted.has(key)) {
                watcher.dispose();
                this.folderWatchers.delete(key);
            }
        }
        for (const { folder, pattern } of folders) {
            const key = `${folder.toLowerCase()}|${pattern}`;
            if (this.folderWatchers.has(key)) {
                continue;
            }
            const watcher = vscode.workspace.createFileSystemWatcher(new vscode.RelativePattern(vscode.Uri.file(folder), pattern));
            const changed = (uri: vscode.Uri) => {
                if (!/[\\/](bin|obj|\.vs|\.git)[\\/]/i.test(uri.fsPath)) {
                    this.refreshSoon();
                }
            };
            watcher.onDidCreate(changed);
            watcher.onDidDelete(changed);
            if (pattern !== '**/*') {
                watcher.onDidChange(changed);
            } else {
                watcher.onDidChange(uri => /\.csproj$/i.test(uri.fsPath) && changed(uri));
            }
            this.folderWatchers.set(key, watcher);
        }
    }

    getTreeItem(node: ProjectNode): vscode.TreeItem {
        switch (node.kind) {
            case 'xppProject': {
                const item = new vscode.TreeItem(node.name, node.active ? vscode.TreeItemCollapsibleState.Expanded : vscode.TreeItemCollapsibleState.Collapsed);
                item.iconPath = new vscode.ThemeIcon('project');
                item.description = `${node.model ?? ''}${node.active ? '' : ' · not open'}`.trim();
                item.tooltip = `${node.file}${node.active ? '' : '\nOpen This Project to work on it.'}`;
                item.contextValue = node.active ? 'xppSolutionProject active' : 'xppSolutionProject other';
                item.id = `xpp:${node.file.toLowerCase()}`;
                return item;
            }
            case 'references': {
                const item = new vscode.TreeItem('References', vscode.TreeItemCollapsibleState.Collapsed);
                item.iconPath = new vscode.ThemeIcon('references');
                item.tooltip = 'C# projects and assemblies this X++ project uses. The build copies them into the package.';
                item.contextValue = 'xppReferences';
                item.id = `refs:${node.project.toLowerCase()}`;
                return item;
            }
            case 'reference': {
                const { ref } = node;
                const item = new vscode.TreeItem(ref.name, vscode.TreeItemCollapsibleState.None);
                item.iconPath = new vscode.ThemeIcon(!ref.exists ? 'warning' : ref.kind === 'project' ? 'file-code' : 'library');
                item.description = `${ref.kind === 'project' ? 'C# project' : 'assembly'}${ref.exists ? '' : ' · not found'}`;
                item.tooltip = ref.path ?? ref.name;
                item.contextValue = `xppReference ${ref.kind}`;
                if (ref.kind === 'project' && ref.path && ref.exists) {
                    item.command = { command: 'vscode.open', title: 'Open Project File', arguments: [vscode.Uri.file(ref.path)] };
                }
                return item;
            }
            case 'folder': {
                const item = new vscode.TreeItem(node.folder, node.active ? vscode.TreeItemCollapsibleState.Expanded : vscode.TreeItemCollapsibleState.Collapsed);
                item.iconPath = vscode.ThemeIcon.Folder;
                item.description = String(node.items.length);
                item.contextValue = node.active ? 'xppProjectFolder' : 'xppOtherProjectFolder';
                return item;
            }
            case 'item': {
                const { objectType, objectName } = node.item;
                const item = new vscode.TreeItem(objectName, vscode.TreeItemCollapsibleState.None);
                item.description = typeName(objectType);
                // Project items always belong to the project's own (custom) model; items of another project can't be excluded from this one.
                item.contextValue = elementContextValue(objectType, true) + (node.active ? '' : ' otherProject');
                item.iconPath = new vscode.ThemeIcon(iconFor(objectType));
                item.command = elementOpenCommand(objectType, objectName);
                // Lets Git change decorations (ChangeDecorations) mark the item.
                item.resourceUri = objectUri(objectType, objectName);
                return item;
            }
            case 'hint': {
                const item = new vscode.TreeItem(node.label, vscode.TreeItemCollapsibleState.None);
                item.iconPath = new vscode.ThemeIcon('add');
                item.command = { command: node.command, title: node.label };
                return item;
            }
            case 'csProject': {
                const item = new vscode.TreeItem(node.name, vscode.TreeItemCollapsibleState.Collapsed);
                item.resourceUri = vscode.Uri.file(node.file);
                item.iconPath = vscode.ThemeIcon.File;
                item.description = node.inSolution ? 'C#' : 'C# · not in the solution';
                item.tooltip = node.file;
                item.contextValue = 'xppCsProject';
                item.id = `cs:${node.file.toLowerCase()}`;
                return item;
            }
            case 'file': {
                const folder = !!node.node.children;
                const item = new vscode.TreeItem(node.node.name, folder ? vscode.TreeItemCollapsibleState.Collapsed : vscode.TreeItemCollapsibleState.None);
                item.resourceUri = vscode.Uri.file(node.node.path);
                item.iconPath = folder ? vscode.ThemeIcon.Folder : vscode.ThemeIcon.File;
                item.contextValue = folder ? 'xppCsFolder' : 'xppCsFile';
                // Two projects in one folder list the same files; ids stay unique per project.
                item.id = `file:${node.project.toLowerCase()}|${node.node.path.toLowerCase()}`;
                if (!folder) {
                    item.command = { command: 'vscode.open', title: 'Open', arguments: [item.resourceUri] };
                }
                return item;
            }
        }
    }

    async getChildren(node?: ProjectNode): Promise<ProjectNode[]> {
        if (!node) {
            return this.roots();
        }
        switch (node.kind) {
            case 'xppProject': {
                const project = await readProject(node.file).catch(() => undefined);
                if (!project) {
                    return [];
                }
                const folders = new Map<string, ProjectItem[]>();
                for (const item of project.items) {
                    folders.set(item.folder, [...(folders.get(item.folder) ?? []), item]);
                }
                const children: ProjectNode[] = node.active ? [{ kind: 'references', project: node.file }] : [];
                children.push(...[...folders.entries()]
                    .sort(([a], [b]) => a.localeCompare(b))
                    .map(([folder, items]) => ({ kind: 'folder' as const, folder, items, active: node.active })));
                if (node.active && project.items.length === 0) {
                    children.push({ kind: 'hint', label: 'No elements yet: New Element…', command: 'xpp.newElement' });
                }
                return children;
            }
            case 'references': {
                const xml = await fs.readFile(node.project, 'utf8').catch(() => '');
                const refs = projectReferences(node.project, xml).sort((a, b) => a.name.localeCompare(b.name));
                return refs.length
                    ? refs.map(ref => ({ kind: 'reference' as const, ref }))
                    : [{ kind: 'hint', label: 'Add Reference…', command: 'xpp.addReference' }];
            }
            case 'folder':
                return node.items
                    .sort((a, b) => a.objectName.localeCompare(b.objectName))
                    .map(item => ({ kind: 'item', item, active: node.active }));
            case 'csProject':
                return (await csharpProjectTree(node.file)).map(file => ({ kind: 'file', node: file, project: node.file }));
            case 'file':
                return (node.node.children ?? []).map(file => ({ kind: 'file', node: file, project: node.project }));
            default:
                return [];
        }
    }

    private async roots(): Promise<ProjectNode[]> {
        const project = await readProject().catch(() => undefined);
        this.watch(project?.file);
        // The welcome text depends on it: "no project selected".
        void vscode.commands.executeCommand('setContext', 'xpp.hasProject', !!project);
        if (!project) {
            if (this.view) {
                this.view.description = undefined;
            }
            this.watchFolders([]);
            return [];
        }
        const { solution, projects } = await solutionCsharpProjects(project.file);
        if (this.view) {
            this.view.description = solution ? `${solution.name} (solution)` : `${project.name}${project.model ? ` (${project.model})` : ''}`;
        }
        const roots: ProjectNode[] = [{ kind: 'xppProject', file: project.file, name: project.name, model: project.model, active: true }];
        for (const file of solution?.projects.filter(p => /\.rnrproj$/i.test(p) && p.toLowerCase() !== project.file.toLowerCase()) ?? []) {
            const other = await readProject(file).catch(() => undefined);
            if (other) {
                roots.push({ kind: 'xppProject', file, name: other.name, model: other.model, active: false });
            }
        }
        roots.push(...projects.map(p => ({ kind: 'csProject' as const, file: p.file, name: path.basename(p.file).replace(/\.csproj$/i, ''), inSolution: p.inSolution })));
        this.watchFolders([
            ...projects.map(p => ({ folder: path.dirname(p.file), pattern: '**/*' })),
            ...(solution ? [{ folder: path.dirname(solution.file), pattern: path.basename(solution.file) }] : []),
        ]);
        return roots;
    }
}

function iconFor(objectType: string): string {
    return aotIcon(objectType);
}
