import { randomBytes } from 'crypto';
import * as vscode from 'vscode';
import { XppServerClient } from './serverClient';

/**
 * Label editor for an AxLabelFile: the label entries (ID, text per language, comment) with search, the file's other
 * languages side by side, and add / edit / delete for label files of your own models, like Visual Studio's label editor.
 */
export class LabelEditor {
    private static readonly panels = new Map<string, LabelEditor>();

    static open(context: vscode.ExtensionContext, server: XppServerClient, labelFile: string, showProperties: () => void): void {
        const key = labelFile.toLowerCase();
        const existing = LabelEditor.panels.get(key);
        if (existing) {
            existing.panel.reveal();
            return;
        }
        const panel = vscode.window.createWebviewPanel('xppLabelEditor', `${labelFile} [Label file]`, vscode.ViewColumn.Active, {
            enableScripts: true,
            retainContextWhenHidden: true,
            localResourceRoots: [vscode.Uri.joinPath(context.extensionUri, 'media')],
        });
        LabelEditor.panels.set(key, new LabelEditor(context, server, labelFile, panel, showProperties));
        panel.onDidDispose(() => LabelEditor.panels.delete(key));
    }

    private constructor(
        context: vscode.ExtensionContext,
        private readonly server: XppServerClient,
        private readonly labelFile: string,
        private readonly panel: vscode.WebviewPanel,
        private readonly showProperties: () => void,
    ) {
        panel.webview.html = this.html(context, panel.webview);
        panel.webview.onDidReceiveMessage(msg => void this.onMessage(msg));
    }

    private async onMessage(msg: any): Promise<void> {
        try {
            switch (msg.type) {
                case 'load': {
                    const data = await this.server.bridge('label_file_entries', {
                        labelFile: this.labelFile, search: msg.search ?? '', limit: '500',
                        ...(Array.isArray(msg.languages) ? { languages: msg.languages } : {}),
                    });
                    if (!data?.success) {
                        throw new Error(data?.message ?? 'Could not read the label file.');
                    }
                    await this.panel.webview.postMessage({ type: 'data', data });
                    return;
                }
                case 'save': {
                    const result = await this.server.bridge('save_label', { labelFile: this.labelFile, labelId: msg.labelId, texts: msg.texts, comment: msg.comment ?? '' });
                    if (!result?.success) {
                        throw new Error(result?.message ?? 'Save failed.');
                    }
                    vscode.window.setStatusBarMessage(result.message, 5000);
                    await this.panel.webview.postMessage({ type: 'saved', labelId: msg.labelId });
                    return;
                }
                case 'delete': {
                    const ok = await vscode.window.showWarningMessage(`Delete label ${msg.labelId} in every language of this label file?`,
                        { modal: true, detail: 'Code and properties that reference it will show the raw label ID.' }, 'Delete');
                    if (ok !== 'Delete') {
                        return;
                    }
                    const result = await this.server.bridge('delete_label', { labelFile: this.labelFile, labelId: msg.labelId });
                    if (!result?.success) {
                        throw new Error(result?.message ?? 'Delete failed.');
                    }
                    vscode.window.setStatusBarMessage(result.message, 5000);
                    await this.panel.webview.postMessage({ type: 'saved' });
                    return;
                }
                case 'copy':
                    await vscode.env.clipboard.writeText(msg.text);
                    vscode.window.setStatusBarMessage(`Copied ${msg.text}`, 3000);
                    return;
                case 'properties':
                    this.showProperties();
                    return;
            }
        } catch (err) {
            await this.panel.webview.postMessage({ type: 'error', message: (err as Error).message });
        }
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
<link href="${media('labelEditor.css')}" rel="stylesheet">
</head>
<body>
<div id="toolbar">
  <span id="title"></span>
  <span id="badge"></span>
  <input id="search" type="search" placeholder="Search ID, text or comment" aria-label="Search labels">
  <button id="languages" title="Languages shown side by side">Languages…</button>
  <span class="spacer"></span>
  <button id="add" hidden>New label</button>
  <button id="properties">Properties</button>
  <button id="refresh">Refresh</button>
</div>
<div id="languagePicker" hidden></div>
<div id="error" class="error" hidden></div>
<table id="labels"><thead></thead><tbody></tbody></table>
<div id="footer" class="muted"></div>
<script nonce="${nonce}" src="${media('labelEditor.js')}"></script>
</body>
</html>`;
    }
}
