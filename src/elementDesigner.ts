import { randomBytes } from 'crypto';
import * as vscode from 'vscode';
import { CodeTypes, typeLabel } from './aot';
import { objectUri } from './codeView';
import { XppServerClient } from './serverClient';
import { methodSpans } from './xppSource';

/**
 * Element designer: the element's node tree and a Properties grid, like Visual Studio's designer and Properties window.
 * Properties of elements in your own models can be edited; everything else is read-only.
 */
export class ElementDesigner {
    private static readonly panels = new Map<string, ElementDesigner>();

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
        ElementDesigner.panels.set(key, new ElementDesigner(context, server, objectType, objectName, panel, key));
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
        panel.onDidDispose(() => ElementDesigner.panels.delete(key));
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
                return this.loadProperties(message.path);
            case 'setProperty':
                return this.setProperty(message.path, message.property, message.value);
            case 'openMethod':
                return this.openMethod(message.path);
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
            this.post({
                type: 'tree',
                data: tree,
                typeLabel: typeLabel(this.objectType),
                hasCode: CodeTypes.has(this.objectType),
                isForm: this.objectType === 'AxForm',
            });
        } finally {
            this.post({ type: 'busy', on: false });
        }
    }

    private async loadProperties(path: string[]): Promise<void> {
        const result = await this.server.bridge('get_element_properties', { objectType: this.objectType, objectName: this.objectName, path });
        if (!result.success) {
            throw new Error(result.message);
        }
        this.post({ type: 'properties', path, data: result });
    }

    private async setProperty(path: string[], property: string, value: string): Promise<void> {
        const result = await this.server.bridge('set_element_property', { objectType: this.objectType, objectName: this.objectName, path, property, value });
        if (!result.success) {
            this.post({ type: 'error', message: result.message });
        }
        await this.loadProperties(path);
    }

    /** Opens the code view at the method; only the element's own methods are in the code view. */
    private async openMethod(path: string[]): Promise<void> {
        if (path.length !== 2 || !CodeTypes.has(this.objectType)) {
            vscode.window.showInformationMessage('Methods of data sources, controls and other nodes can\'t be opened in the code view yet.');
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
<meta http-equiv="Content-Security-Policy" content="default-src 'none'; style-src ${webview.cspSource}; script-src 'nonce-${nonce}';">
<meta name="viewport" content="width=device-width, initial-scale=1.0">
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
</main>
<script nonce="${nonce}" src="${media('element.js')}"></script>
</body>
</html>`;
    }
}
