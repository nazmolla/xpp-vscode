import { promises as fs } from 'fs';
import * as os from 'os';
import * as path from 'path';
import * as vscode from 'vscode';
import { iconFor as aotIcon } from './aot';
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
    await vscode.workspace.getConfiguration('xpp').update('project', file, vscode.ConfigurationTarget.Global);
}

type ProjectNode = { kind: 'folder'; folder: string; items: ProjectItem[] } | { kind: 'item'; item: ProjectItem };

/** Solution-explorer style view of the active project, grouped by folder. */
export class ProjectTreeProvider implements vscode.TreeDataProvider<ProjectNode> {
    private readonly changed = new vscode.EventEmitter<void>();
    readonly onDidChangeTreeData = this.changed.event;
    view?: vscode.TreeView<ProjectNode>;

    refresh(): void {
        this.changed.fire();
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
        item.contextValue = objectType === 'AxForm' || objectType === 'AxFormExtension' ? 'xppForm' : 'xppObject';
        item.iconPath = new vscode.ThemeIcon(iconFor(objectType));
        item.command = objectType === 'AxFormExtension'
            ? { command: 'xpp.openFormDesigner', title: 'Open designer', arguments: [objectName.split('.')[0]] }
            : { command: 'vscode.open', title: 'Open', arguments: [objectUri(objectType, objectName)] };
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
