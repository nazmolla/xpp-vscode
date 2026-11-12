import { randomBytes } from 'crypto';
import * as os from 'os';
import * as vscode from 'vscode';
import { elementOpenCommand, typeLabel, typeName } from './aot';
import { XppServerClient } from './serverClient';

export const MetadataScheme = 'xppmeta';

/** Read-only text of an element's metadata (nodes, properties, code), for Compare metadata. */
export class MetadataDocuments implements vscode.TextDocumentContentProvider {
    constructor(private readonly server: XppServerClient) { }

    static uri(objectType: string, objectName: string): vscode.Uri {
        return vscode.Uri.from({ scheme: MetadataScheme, path: `/${objectType}/${objectName}.xppmeta` });
    }

    async provideTextDocumentContent(uri: vscode.Uri): Promise<string> {
        const [objectType, file] = uri.path.replace(/^\//, '').split('/');
        const result = await this.server.bridge('dump_element', { objectType, objectName: file.replace(/\.xppmeta$/, '') });
        return result.success ? result.text : `// ${result.message}`;
    }
}

export interface DiagramNode {
    label: string;
    description?: string;
    objectType?: string;
    objectName?: string;
    /** Opens something more specific than the element (e.g. a reference's line). */
    open?: () => Thenable<unknown> | Promise<unknown>;
    children?: DiagramNode[];
}

/** Hierarchy, references and dependency views (Visual Studio's DGML diagrams) as a clickable tree, exportable as Mermaid. */
export class DiagramPanel {
    static show(title: string, roots: DiagramNode[]): void {
        const panel = vscode.window.createWebviewPanel('xppDiagram', title, vscode.ViewColumn.Active, { enableScripts: true });
        const nonce = randomBytes(16).toString('base64');
        const nodes: DiagramNode[] = [];
        const render = (node: DiagramNode): string => {
            const id = nodes.push(node) - 1;
            const link = node.open || (node.objectType && node.objectName) ? ` class="link" data-id="${id}"` : '';
            const description = node.description ? ` <span class="desc">${escapeHtml(node.description)}</span>` : '';
            const children = node.children?.length ? `<ul>${node.children.map(render).join('')}</ul>` : '';
            return `<li><span class="node"${link}>${escapeHtml(node.label)}</span>${description}${children}</li>`;
        };
        const body = `<ul class="tree">${roots.map(render).join('')}</ul>`;
        panel.webview.html = `<!DOCTYPE html><html><head><meta charset="UTF-8">
<meta http-equiv="Content-Security-Policy" content="default-src 'none'; style-src 'unsafe-inline'; script-src 'nonce-${nonce}';">
<style>
body { font-family: var(--vscode-font-family); color: var(--vscode-foreground); padding: 12px; }
h2 { font-weight: 600; margin: 0 0 8px; }
.bar { margin: 0 0 12px; }
button { background: var(--vscode-button-secondaryBackground); color: var(--vscode-button-secondaryForeground); border: none; padding: 3px 10px; cursor: pointer; margin-right: 6px; }
button:hover { background: var(--vscode-button-secondaryHoverBackground); }
ul { list-style: none; margin: 0; padding-left: 22px; border-left: 1px solid var(--vscode-panel-border); }
ul.tree { padding-left: 0; border: none; }
li { margin: 3px 0; }
.node { display: inline-block; padding: 2px 8px; border: 1px solid var(--vscode-panel-border); border-radius: 3px; background: var(--vscode-editorWidget-background); }
.link { cursor: pointer; color: var(--vscode-textLink-foreground); }
.link:hover { text-decoration: underline; }
.desc { color: var(--vscode-descriptionForeground); margin-left: 6px; }
</style></head><body><h2>${escapeHtml(title)}</h2>
<div class="bar"><button id="copy">Copy Mermaid</button><button id="save">Export Mermaid…</button></div>${body}
<script nonce="${nonce}">
const vscode = acquireVsCodeApi();
document.querySelectorAll('.link').forEach(e => e.addEventListener('click', () => vscode.postMessage({ id: Number(e.dataset.id) })));
document.getElementById('copy').addEventListener('click', () => vscode.postMessage({ command: 'copyMermaid' }));
document.getElementById('save').addEventListener('click', () => vscode.postMessage({ command: 'saveMermaid' }));
</script></body></html>`;
        panel.webview.onDidReceiveMessage(async message => {
            if (message.command === 'copyMermaid') {
                await vscode.env.clipboard.writeText(toMermaid(roots));
                vscode.window.setStatusBarMessage('$(clippy) Mermaid diagram copied', 4000);
                return;
            }
            if (message.command === 'saveMermaid') {
                const target = await vscode.window.showSaveDialog({ title: 'Export diagram', filters: { Mermaid: ['mmd', 'md'] },
                    defaultUri: vscode.Uri.joinPath(vscode.workspace.workspaceFolders?.[0]?.uri ?? vscode.Uri.file(os.homedir()), `${roots[0]?.label ?? 'diagram'}.mmd`.replace(/[^\w.-]+/g, '-')) });
                if (target) {
                    const text = toMermaid(roots);
                    await vscode.workspace.fs.writeFile(target, Buffer.from(target.path.endsWith('.md') ? '```mermaid\n' + text + '```\n' : text, 'utf8'));
                }
                return;
            }
            const node = nodes[message.id];
            if (node?.open) {
                Promise.resolve(node.open()).catch(err => vscode.window.showErrorMessage((err as Error).message));
            } else if (node?.objectType && node.objectName) {
                const open = elementOpenCommand(node.objectType, node.objectName);
                void vscode.commands.executeCommand(open.command, ...(open.arguments ?? []));
            }
        });
    }
}

/** A Mermaid flowchart of the tree: one box per node, an arrow from each node to its children. */
export function toMermaid(roots: DiagramNode[]): string {
    const lines = ['flowchart LR'];
    let next = 0;
    const label = (node: DiagramNode) => (node.description ? `${node.label}<br/><small>${node.description}</small>` : node.label).replace(/"/g, '#quot;');
    const walk = (node: DiagramNode): string => {
        const id = `n${next++}`;
        lines.push(`  ${id}["${label(node)}"]`);
        for (const child of node.children ?? []) {
            lines.push(`  ${id} --> ${walk(child)}`);
        }
        return id;
    };
    roots.forEach(walk);
    return lines.join('\n') + '\n';
}

function escapeHtml(text: string): string {
    return text.replace(/&/g, '&amp;').replace(/</g, '&lt;').replace(/>/g, '&gt;').replace(/"/g, '&quot;');
}

/** Base chain (via Extends) and derived elements (cross-reference ClassExtended) of a class or table. */
export async function hierarchyDiagram(server: XppServerClient, objectType: string, objectName: string): Promise<void> {
    const bases: string[] = [];
    const seen = new Set<string>([objectName.toLowerCase()]);
    for (let name: string | undefined = objectName; name;) {
        const result: any = await server.bridge('get_element_properties', { objectType, objectName: name, path: [] });
        const base: string | undefined = (result.properties as any[] | undefined)?.find(p => p.name === 'Extends')?.value || undefined;
        if (!base || seen.has(base.toLowerCase())) {
            break;
        }
        seen.add(base.toLowerCase());
        bases.unshift(base);
        name = base;
    }
    const derived = await derivedOf(server, objectType, objectName, 2);
    const self: DiagramNode = { label: objectName, description: 'this element', objectType, objectName, children: derived };
    let root = self;
    for (let i = bases.length - 1; i >= 0; i--) {
        root = { label: bases[i], objectType, objectName: bases[i], children: [root] };
    }
    DiagramPanel.show(`${typeName(objectType)} hierarchy: ${objectName}`, [root]);
}

async function derivedOf(server: XppServerClient, objectType: string, objectName: string, depth: number): Promise<DiagramNode[]> {
    const result = await server.bridge('where_used', { objectType, objectName, referenceKind: 'ClassExtended', maxResults: '200' }).catch(() => undefined);
    if (!result?.success) {
        return [];
    }
    const names = [...new Set((result.references as any[]).filter(r => r.objectType === objectType).map(r => String(r.objectName)))].sort();
    const nodes: DiagramNode[] = [];
    for (const name of names) {
        nodes.push({ label: name, objectType, objectName: name, children: depth > 1 ? await derivedOf(server, objectType, name, depth - 1) : undefined });
    }
    return nodes;
}

/** Where-used grouped by referencing element type, like Visual Studio's View references diagram. */
export async function referencesDiagram(server: XppServerClient, objectType: string, objectName: string,
    openReference: (reference: any) => Promise<unknown>): Promise<void> {
    const result = await server.bridge('where_used', { objectType, objectName, maxResults: '1000' });
    if (!result.success) {
        throw new Error(result.message);
    }
    const groups = new Map<string, Map<string, any[]>>();
    for (const r of result.references as any[]) {
        const type = r.objectType ?? 'Other';
        const byElement = groups.get(type) ?? new Map<string, any[]>();
        byElement.set(r.objectName, [...(byElement.get(r.objectName) ?? []), r]);
        groups.set(type, byElement);
    }
    const children: DiagramNode[] = [...groups.entries()].sort(([a], [b]) => a.localeCompare(b)).map(([type, elements]) => ({
        label: typeLabel(type),
        description: String(elements.size),
        children: [...elements.entries()].map(([name, refs]) => ({
            label: name, objectType: type, objectName: name, description: `${refs.length} reference(s)`,
            children: refs.map(r => ({
                label: `${r.member}${r.line > 0 ? ` · line ${r.line}` : ''}`,
                description: r.kind,
                open: () => openReference(r),
            })),
        })),
    }));
    DiagramPanel.show(`References to ${objectName}${result.more ? ' (first 1000)' : ''}`, [{ label: objectName, objectType, objectName, children }]);
}

interface Dependency { objectType: string; objectName: string; references: number; kinds: string[] }

/** What an element uses, from the cross-reference database (Visual Studio's dependency diagram), grouped by element type. */
export async function dependencyDiagram(server: XppServerClient, objectType: string, objectName: string): Promise<void> {
    const result = await server.bridge('element_dependencies', { objectType, objectName, maxResults: '200' });
    if (!result.success) {
        throw new Error(result.message);
    }
    const dependencies = result.dependencies as Dependency[];
    if (!dependencies.length) {
        vscode.window.showInformationMessage(`The cross-reference database records no dependencies of ${objectName}. Build its model with cross references to fill it.`);
        return;
    }
    const byType = new Map<string, Dependency[]>();
    for (const d of dependencies) {
        byType.set(d.objectType, [...(byType.get(d.objectType) ?? []), d]);
    }
    const groups: DiagramNode[] = [...byType.entries()].sort(([a], [b]) => typeLabel(a).localeCompare(typeLabel(b))).map(([type, items]) => ({
        label: typeLabel(type),
        description: String(items.length),
        children: items.sort((a, b) => a.objectName.localeCompare(b.objectName)).map(d => ({
            label: d.objectName, objectType: d.objectType, objectName: d.objectName, description: `${d.references} reference(s): ${d.kinds.join(', ')}`,
        })),
    }));
    DiagramPanel.show(`Dependencies of ${objectName}${result.more ? ' (first 200)' : ''}`,
        [{ label: objectName, objectType, objectName, description: `${dependencies.length} element(s)`, children: groups }]);
}
