import * as vscode from 'vscode';
import { ServerState, XppServerClient } from './serverClient';

/**
 * The metadata servers' state in the status bar: shown while one starts and when one stopped unexpectedly (click to
 * restart; details in the X++ output); hidden while they run normally.
 */
export class ServerStatus implements vscode.Disposable {
    private readonly item = vscode.window.createStatusBarItem('xpp.server', vscode.StatusBarAlignment.Left, -2);
    private readonly subscriptions: vscode.Disposable[] = [];
    private readonly states = new Map<XppServerClient, ServerState>();

    constructor(servers: XppServerClient[]) {
        this.item.name = 'F&O Studio server';
        for (const server of servers) {
            this.subscriptions.push(server.onDidChangeState(state => {
                this.states.set(server, state);
                this.render();
            }));
        }
    }

    private render(): void {
        const states = [...this.states.values()];
        const failed = states.find((s): s is Extract<ServerState, { kind: 'failed' }> => s.kind === 'failed');
        if (failed) {
            this.item.text = '$(error) X++ server stopped';
            this.item.tooltip = `${failed.message}\nClick to restart; details are in the X++ output.`;
            this.item.command = 'xpp.restartServer';
            this.item.backgroundColor = new vscode.ThemeColor('statusBarItem.errorBackground');
            this.item.show();
        } else if (states.some(s => s.kind === 'starting')) {
            this.item.text = '$(loading~spin) X++ server';
            this.item.tooltip = 'Loading the D365FO metadata…';
            this.item.command = undefined;
            this.item.backgroundColor = undefined;
            this.item.show();
        } else {
            this.item.hide();
        }
    }

    dispose(): void {
        this.subscriptions.forEach(s => s.dispose());
        this.item.dispose();
    }
}
