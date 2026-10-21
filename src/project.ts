import { promises as fs } from 'fs';
import * as path from 'path';
import * as vscode from 'vscode';
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

type ProjectNode = { kind: 'folder'; folder: string; items: ProjectItem[] } | { kind: 'item'; item: ProjectItem };

/** Solution-explorer style view of the active project, grouped by folder. */
export class ProjectTreeProvider implements vscode.TreeDataProvider<ProjectNode> {
    private readonly changed = new vscode.EventEmitter<void>();
    readonly onDidChangeTreeData = this.changed.event;

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
