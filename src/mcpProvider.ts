import * as vscode from 'vscode';
import { readActiveEnvironment } from './environment';

const ServerVersion = '0.1.0';

/** The .rnrproj the tools add objects to: the xpp.project setting, else the first one in the workspace. */
export async function findActiveProject(): Promise<string | undefined> {
    const configured = vscode.workspace.getConfiguration('xpp').get<string>('project');
    if (configured) {
        return configured;
    }
    const found = await vscode.workspace.findFiles('**/*.rnrproj', '**/{node_modules,bin,obj}/**', 2);
    return found[0]?.fsPath;
}

export function registerMcpProvider(context: vscode.ExtensionContext, log: vscode.OutputChannel): void {
    const changed = new vscode.EventEmitter<void>();
    context.subscriptions.push(changed);

    context.subscriptions.push(vscode.workspace.onDidChangeConfiguration(e => {
        if (e.affectsConfiguration('xpp.project')) {
            changed.fire();
        }
    }));

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
                return [new vscode.McpStdioServerDefinition('X++ (D365FO)', exe, args, {}, ServerVersion)];
            } catch (err) {
                log.appendLine(`MCP server not started: ${(err as Error).message}`);
                return [];
            }
        },
    }));
}
