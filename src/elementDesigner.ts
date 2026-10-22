import { randomBytes } from 'crypto';
import * as vscode from 'vscode';
import { CodeTypes, elementContextValue, typeLabel } from './aot';
import { methodUri, objectUri } from './codeView';
import { addEventNodes, copyEventHandler, EventSpec, eventLabel, handlerQuery } from './events';
import { XppServerClient } from './serverClient';
import { methodSpans } from './xppSource';

/** Context passed by the designer tree's right-click menu (data-vscode-context). */
export interface DesignerNodeContext {
    xppPath: string;
    xppKind: string;
    xppType?: string;
    xppEventSpec?: EventSpec;
}

/** Element types whose own methods can have Pre/Post handlers and delegates with subscribers. */
const HandlerElementTypes = new Set(['AxClass', 'AxTable', 'AxForm', 'AxDataEntityView', 'AxView']);

interface NodeTypes {
    collections: { path: string[]; label: string; types: { typeName: string; label: string }[] }[];
}

function baseType(objectType: string): string {
    return ({ AxTableExtension: 'AxTable', AxFormExtension: 'AxForm', AxDataEntityViewExtension: 'AxDataEntityView' } as Record<string, string>)[objectType] ?? objectType;
}

const NamePattern = /^[A-Za-z_][A-Za-z0-9_]*$/;

/**
 * Element designer: the element's node tree and a Properties grid, like Visual Studio's designer and Properties window.
 * Forms also show a rendered preview. Elements in your own models can be changed; everything else is read-only.
 */
export class ElementDesigner {
    private static readonly panels = new Map<string, ElementDesigner>();
    private static active: ElementDesigner | undefined;
    /** Fired after the designer changes an element, so open code views can reload. */
    static readonly onDidChange = new vscode.EventEmitter<{ objectType: string; objectName: string }>();

    static open(context: vscode.ExtensionContext, server: XppServerClient, objectType: string, objectName: string): void {
        const key = `${objectType}/${objectName}`.toLowerCase();
        const existing = ElementDesigner.panels.get(key);
        if (existing) {
            existing.panel.reveal();
            return;
        }
        const panel = vscode.window.createWebviewPanel('xppElementDesigner', `${objectName} [${typeLabel(objectType).replace(/s$/, '')}]`, vscode.ViewColumn.Active, {
            enableScripts: true,
            retainContextWhenHidden: true,
            localResourceRoots: [vscode.Uri.joinPath(context.extensionUri, 'media')],
        });
        const designer = new ElementDesigner(context, server, objectType, objectName, panel, key);
        ElementDesigner.panels.set(key, designer);
        ElementDesigner.active = designer;
    }

    /** Runs a right-click action on the designer the menu was opened in. */
    static async runAction(action: 'new' | 'newMethod' | 'override' | 'delete' | 'openMethod' | 'copyEventHandler' | 'findEventHandlers',
        node: DesignerNodeContext): Promise<void> {
        const designer = ElementDesigner.active;
        if (!designer) {
            return;
        }
        const path: string[] = JSON.parse(node.xppPath ?? '[]');
        try {
            switch (action) {
                case 'new': return await designer.newNode(path);
                case 'newMethod': return await designer.newMethod(node.xppKind === 'folder' ? path.slice(0, -1) : path);
                case 'override': return await designer.overrideMethod(node.xppKind === 'folder' ? path.slice(0, -1) : path);
                case 'delete': return await designer.deleteNode(path, node.xppKind);
                case 'openMethod': return await designer.openMethod(path);
                case 'copyEventHandler': {
                    const spec = node.xppEventSpec ?? await designer.methodEvent(path, true);
                    return spec ? await copyEventHandler(spec) : undefined;
                }
                case 'findEventHandlers': return await designer.findEventHandlers(path, node);
            }
        } catch (err) {
            vscode.window.showErrorMessage((err as Error).message);
        }
    }

    private constructor(
        context: vscode.ExtensionContext,
        private readonly server: XppServerClient,
        private readonly objectType: string,
        private readonly objectName: string,
        private readonly panel: vscode.WebviewPanel,
        key: string,
    ) {
        panel.webview.html = this.html(context, panel.webview);
        panel.onDidDispose(() => {
            ElementDesigner.panels.delete(key);
            if (ElementDesigner.active === this) {
                ElementDesigner.active = undefined;
            }
        });
        panel.onDidChangeViewState(e => {
            if (e.webviewPanel.active) {
                ElementDesigner.active = this;
            }
        });
        panel.webview.onDidReceiveMessage(message => this.onMessage(message).catch(err => this.post({ type: 'error', message: (err as Error).message })));
    }

    private post(message: object): void {
        void this.panel.webview.postMessage(message);
    }

    private async onMessage(message: any): Promise<void> {
        switch (message.command) {
            case 'ready':
            case 'refresh':
                return this.loadTree();
            case 'select':
                return this.loadProperties(message.path, message.source ?? undefined);
            case 'setProperty':
                return this.setProperty(message.path, message.property, message.value);
            case 'openMethod':
                return this.openMethod(message.path);
            case 'copyEventHandler':
                return copyEventHandler(message.spec);
            case 'viewCode':
                await vscode.commands.executeCommand('vscode.open', objectUri(this.objectType, this.objectName));
                return;
            case 'formLayout':
                await vscode.commands.executeCommand('xpp.openFormDesigner', this.objectName);
                return;
        }
    }

    private async loadTree(): Promise<void> {
        this.post({ type: 'busy', on: true });
        try {
            const tree = await this.server.bridge('get_element_tree', { objectType: this.objectType, objectName: this.objectName });
            if (!tree.success) {
                throw new Error(tree.message ?? `${this.objectName} not found.`);
            }
            addEventNodes(this.objectType, this.objectName, tree.root);
            this.post({
                type: 'tree',
                data: tree,
                typeLabel: typeLabel(this.objectType),
                hasCode: CodeTypes.has(this.objectType),
                isForm: this.objectType === 'AxForm',
                element: { objectType: this.objectType, objectName: this.objectName, context: elementContextValue(this.objectType, !!tree.editable) },
            });
            if (this.objectType === 'AxForm' || this.objectType === 'AxFormExtension') {
                const form = await this.server.bridge('get_form_tree', { formName: this.objectName.split('.')[0] });
                if (form.success) {
                    this.post({ type: 'formTree', data: form });
                }
            }
        } finally {
            this.post({ type: 'busy', on: false });
        }
    }

    private async changed(selectPath?: string[]): Promise<void> {
        if (selectPath) {
            this.post({ type: 'selectPath', path: selectPath });
        }
        ElementDesigner.onDidChange.fire({ objectType: this.objectType, objectName: this.objectName });
        await this.loadTree();
    }

    private async newNode(path: string[]): Promise<void> {
        const types = await this.server.bridge<NodeTypes & { success: boolean; message?: string }>('get_node_types', { objectType: this.objectType, objectName: this.objectName, path });
        if (!types.success) {
            throw new Error(types.message);
        }
        const choices = types.collections.flatMap(c => c.types.map(t => ({
            label: t.label,
            description: types.collections.length > 1 ? c.label : undefined,
            collection: c,
            type: t,
        })));
        if (choices.length === 0) {
            throw new Error('Nothing can be added here.');
        }
        const picked = choices.length === 1 ? choices[0] : await vscode.window.showQuickPick(choices, {
            title: `New in ${path[path.length - 1] ?? this.objectName}`,
            placeHolder: 'What to add',
            matchOnDescription: true,
        });
        if (!picked) {
            return;
        }
        const base = picked.type.label.replace(/[^A-Za-z0-9]/g, '') || 'Node';
        const name = await vscode.window.showInputBox({
            title: `New ${picked.type.label}`,
            prompt: 'Name',
            value: `${base}1`,
            validateInput: v => NamePattern.test(v) ? undefined : 'Use letters, digits and underscores, starting with a letter.',
        });
        if (!name) {
            return;
        }
        const result = await this.server.bridge('add_element_node', {
            objectType: this.objectType, objectName: this.objectName, path: picked.collection.path, typeName: picked.type.typeName, name,
        });
        if (!result.success) {
            throw new Error(result.message);
        }
        await this.changed([...picked.collection.path, name]);
    }

    private async newMethod(nodePath: string[]): Promise<void> {
        const name = await vscode.window.showInputBox({
            title: 'New method',
            prompt: 'Method name',
            value: 'method1',
            validateInput: v => NamePattern.test(v) ? undefined : 'Use letters, digits and underscores, starting with a letter.',
        });
        if (!name) {
            return;
        }
        const indent = ' '.repeat(nodePath.length === 0 ? 4 : nodePath.length >= 4 && nodePath[2] === 'Fields' ? 12 : 8);
        await this.saveMethod(nodePath, `${indent}private void ${name}()\n${indent}{\n${indent}}\n`, name);
    }

    private async overrideMethod(nodePath: string[]): Promise<void> {
        const result = await this.server.bridge('get_overrides', { objectType: this.objectType, objectName: this.objectName, path: nodePath });
        if (!result.success) {
            throw new Error(result.message);
        }
        const overrides: { name: string; signature: string; source: string }[] = result.overrides;
        if (overrides.length === 0) {
            vscode.window.showInformationMessage('There are no more methods to override here.');
            return;
        }
        const picked = await vscode.window.showQuickPick(overrides.map(o => ({ label: o.name, description: o.signature, override: o })), {
            title: 'Override method',
            matchOnDescription: true,
        });
        if (picked) {
            await this.saveMethod(nodePath, picked.override.source, picked.override.name);
        }
    }

    private async saveMethod(nodePath: string[], source: string, name: string): Promise<void> {
        const result = await this.server.bridge('set_element_method', { objectType: this.objectType, objectName: this.objectName, path: nodePath, source });
        if (!result.success) {
            throw new Error(result.message);
        }
        const methodPath = [...nodePath, 'Methods', name];
        await this.changed(methodPath);
        await this.openMethod(methodPath);
    }

    private async deleteNode(path: string[], kind: string): Promise<void> {
        const name = path[path.length - 1];
        const confirm = await vscode.window.showWarningMessage(`Delete ${kind === 'method' ? 'method' : ''} '${name}' from ${this.objectName}?`, { modal: true }, 'Delete');
        if (confirm !== 'Delete') {
            return;
        }
        const result = await this.server.bridge('delete_element_node', { objectType: this.objectType, objectName: this.objectName, path });
        if (!result.success) {
            throw new Error(result.message);
        }
        await this.changed(path.slice(0, -1));
    }

    /** Properties of a node; base element nodes in an extension's designer are read from the base element, read-only. */
    private async loadProperties(path: string[], source?: { objectType: string; objectName: string }): Promise<void> {
        const result = await this.server.bridge('get_element_properties', {
            objectType: source?.objectType ?? this.objectType, objectName: source?.objectName ?? this.objectName, path,
        });
        if (!result.success) {
            throw new Error(result.message);
        }
        if (source) {
            result.editable = false;
        }
        this.post({ type: 'properties', path, data: result });
    }

    private async setProperty(path: string[], property: string, value: string): Promise<void> {
        const result = await this.server.bridge('set_element_property', { objectType: this.objectType, objectName: this.objectName, path, property, value });
        if (!result.success) {
            this.post({ type: 'error', message: result.message });
        } else if (this.objectType === 'AxForm') {
            await this.changed(path);
            return;
        }
        await this.loadProperties(path);
    }

    /** The element's own methods open in its code view; methods of other nodes open on their own. */
    /** Pre/Post handler or delegate subscriber of one of the element's own methods. */
    private async methodEvent(path: string[], askPrePost: boolean): Promise<EventSpec | undefined> {
        if (path.length !== 2 || path[0] !== 'Methods' || !HandlerElementTypes.has(this.objectType)) {
            vscode.window.showInformationMessage('Event handlers can be written for methods of classes, tables, views, data entities and forms (not for methods of nested nodes or extensions).');
            return undefined;
        }
        const document = await vscode.workspace.openTextDocument(objectUri(this.objectType, this.objectName));
        const lines = document.getText().split(/\r?\n/);
        const span = methodSpans(document.getText()).find(s => s.name.toLowerCase() === path[1].toLowerCase());
        let header = '';
        for (let i = span?.startLine ?? 0; i < lines.length && i < (span?.startLine ?? 0) + 30; i++) {
            header += ' ' + lines[i].replace(/\/\/.*$/, '');
            if (/\)/.test(lines[i]) && new RegExp(`\\b${path[1]}\\s*\\(`, 'i').test(header)) {
                break;
            }
        }
        const base = { element: this.objectName, elementType: this.objectType, method: path[1], isStatic: /\bstatic\b/.test(header) };
        if (/\bdelegate\b/.test(header)) {
            const params = new RegExp(`\\b${path[1]}\\s*\\(([^)]*)\\)`, 'i').exec(header)?.[1].replace(/\s+/g, ' ').trim() ?? '';
            return { ...base, kind: 'Delegate', parameters: params };
        }
        if (!askPrePost) {
            return { ...base, kind: 'Post' };
        }
        const picked = await vscode.window.showQuickPick([
            { label: 'Post-event handler', handler: 'Post' as const },
            { label: 'Pre-event handler', handler: 'Pre' as const },
        ], { title: `Copy event handler for ${this.objectName}.${path[1]}` });
        return picked ? { ...base, kind: picked.handler } : undefined;
    }

    /** Lists the handlers of an event, of all events of a node (Events folder) or of a method, from the extension index. */
    private async findEventHandlers(path: string[], node: DesignerNodeContext): Promise<void> {
        let query: { path: string; event?: string; includeChildren: boolean };
        let title: string;
        if (node.xppEventSpec) {
            query = handlerQuery(node.xppEventSpec);
            title = eventLabel(node.xppEventSpec);
        } else if (node.xppKind === 'method') {
            const spec = await this.methodEvent(path, false);
            if (!spec) {
                return;
            }
            query = { ...handlerQuery(spec), event: undefined };
            title = `${this.objectName}.${path[1]}`;
        } else {
            const owner = path[path.length - 1] === 'Events' ? path.slice(0, -1) : path;
            const spec: EventSpec = owner.length === 0 ? { kind: 'Data', element: this.objectName.split('.')[0], elementType: baseType(this.objectType) }
                : owner[0] === 'DataSources' && owner.length === 2 ? { kind: 'FormDataSource', element: this.objectName.split('.')[0], elementType: 'AxForm', dataSource: owner[1] }
                : owner[0] === 'DataSources' && owner.length === 4 ? { kind: 'FormDataField', element: this.objectName.split('.')[0], elementType: 'AxForm', dataSource: owner[1], field: owner[3] }
                : { kind: 'FormControl', element: this.objectName.split('.')[0], elementType: 'AxForm', control: owner[owner.length - 1] };
            query = handlerQuery(spec, true);
            title = [spec.element, spec.dataSource, spec.field, spec.control].filter(Boolean).join('.');
        }
        await vscode.commands.executeCommand('xpp.codeIntel.eventHandlers', query, title);
    }

    private async openMethod(path: string[]): Promise<void> {
        if (path.length !== 2 || !CodeTypes.has(this.objectType)) {
            await vscode.window.showTextDocument(methodUri(this.objectType, this.objectName, path));
            return;
        }
        const document = await vscode.workspace.openTextDocument(objectUri(this.objectType, this.objectName));
        const name = path[1].toLowerCase();
        const span = methodSpans(document.getText()).find(s => s.name.toLowerCase() === name);
        const position = new vscode.Position(span?.startLine ?? 0, 0);
        await vscode.window.showTextDocument(document, { selection: new vscode.Range(position, position) });
    }

    private html(context: vscode.ExtensionContext, webview: vscode.Webview): string {
        const nonce = randomBytes(16).toString('base64');
        const media = (file: string) => webview.asWebviewUri(vscode.Uri.joinPath(context.extensionUri, 'media', file));
        return `<!DOCTYPE html>
<html lang="en">
<head>
<meta charset="UTF-8">
<meta http-equiv="Content-Security-Policy" content="default-src 'none'; style-src ${webview.cspSource}; font-src ${webview.cspSource}; script-src 'nonce-${nonce}';">
<meta name="viewport" content="width=device-width, initial-scale=1.0">
<link href="${media('codicons/codicon.css')}" rel="stylesheet">
<link href="${media('formPreview.css')}" rel="stylesheet">
<link href="${media('element.css')}" rel="stylesheet">
</head>
<body>
<div id="toolbar">
  <span id="title"></span>
  <span id="badge"></span>
  <span class="spacer"></span>
  <button id="viewCode" hidden>View code</button>
  <button id="formLayout" hidden>Form layout</button>
  <button id="refresh">Refresh</button>
</div>
<div id="error" class="error" hidden></div>
<main>
  <section id="tree" tabindex="0"></section>
  <section id="properties"><div class="muted">Select a node to see its properties.</div></section>
  <section id="preview" hidden aria-label="Form preview"></section>
</main>
<script nonce="${nonce}" src="${media('formPreview.js')}"></script>
<script nonce="${nonce}" src="${media('element.js')}"></script>
</body>
</html>`;
    }
}
