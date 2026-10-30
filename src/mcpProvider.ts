import { createHash } from 'crypto';
import * as vscode from 'vscode';
import { onDidChangeEnvironment, readActiveEnvironment } from './environment';
import { findActiveProject } from './project';

const ServerVersion = '0.1.0';

export function registerMcpProvider(context: vscode.ExtensionContext, log: vscode.OutputChannel): void {
    const changed = new vscode.EventEmitter<void>();
    context.subscriptions.push(changed);

    context.subscriptions.push(vscode.workspace.onDidChangeConfiguration(e => {
        if (e.affectsConfiguration('xpp.project')) {
            changed.fire();
        }
    }), onDidChangeEnvironment(() => changed.fire()));

    context.subscriptions.push(vscode.lm.registerMcpServerDefinitionProvider('xpp.mcp', {
        onDidChangeMcpServerDefinitions: changed.event,
        provideMcpServerDefinitions: async () => {
            try {
                const env = await readActiveEnvironment();
                const exe = context.asAbsolutePath('server/bin/XppVsCode.Server.exe');
                const args = ['--config', env.configFile];
                const project = await findActiveProject();
                if (project) {
                    args.push('--project', project);
                }
                log.appendLine(`X++ MCP server: config ${env.configFile}, project ${project ?? 'none'}`);
                // A different version makes VS Code restart the running server with the new arguments.
                const version = `${ServerVersion}+${createHash('sha1').update(`${env.configFile}|${project ?? ''}`).digest('hex').slice(0, 8)}`;
                return [new vscode.McpStdioServerDefinition('F&O Studio', exe, args, {}, version)];
            } catch (err) {
                log.appendLine(`MCP server not started: ${(err as Error).message}`);
                return [];
            }
        },
    }));
}
