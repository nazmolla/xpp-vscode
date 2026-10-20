import * as vscode from 'vscode';
import { readActiveEnvironment } from './environment';
import { registerMcpProvider } from './mcpProvider';
import { checkEnvironmentConnection } from './ude';

export function activate(context: vscode.ExtensionContext): void {
    const log = vscode.window.createOutputChannel('X++');
    context.subscriptions.push(log);

    registerMcpProvider(context, log);

    context.subscriptions.push(vscode.commands.registerCommand('xpp.showEnvironment', async () => {
        try {
            const env = await readActiveEnvironment();
            log.appendLine(JSON.stringify(env, null, 2));
            log.show(true);
        } catch (err) {
            vscode.window.showErrorMessage((err as Error).message);
        }
    }));

    context.subscriptions.push(vscode.commands.registerCommand('xpp.checkEnvironment', () => checkEnvironmentConnection(log)));
}

export function deactivate(): void { }
