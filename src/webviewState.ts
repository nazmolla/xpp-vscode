import * as vscode from 'vscode';

/**
 * Webview tabs survive a window reload: the panel's HTML carries what it shows (data-xpp-state on <body>), its script stores
 * that with setState, and the serializer reopens the tab from it.
 */
export function stateAttribute(state: object): string {
    return ` data-xpp-state="${JSON.stringify(state).replace(/[&<>"']/g, c => `&#${c.charCodeAt(0)};`)}"`;
}

export function registerPanelSerializer<T>(context: vscode.ExtensionContext, viewType: string, revive: (panel: vscode.WebviewPanel, state: T) => void | Promise<void>): void {
    context.subscriptions.push(vscode.window.registerWebviewPanelSerializer(viewType, {
        async deserializeWebviewPanel(panel: vscode.WebviewPanel, state: { xpp?: T } | undefined) {
            if (!state?.xpp) {
                panel.dispose();
                return;
            }
            panel.webview.options = { enableScripts: true, localResourceRoots: [vscode.Uri.joinPath(context.extensionUri, 'media')] };
            try {
                await revive(panel, state.xpp);
            } catch {
                panel.dispose();
            }
        },
    }));
}
