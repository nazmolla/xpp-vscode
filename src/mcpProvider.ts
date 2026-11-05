import { createHash } from 'crypto';
import { promises as fs } from 'fs';
import * as vscode from 'vscode';
import { onDidChangeEnvironment, readActiveEnvironment } from './environment';
import { findActiveProject } from './project';

const ServerVersion = '0.1.0';
const RulesFile = '.xpp/agent-rules.md';

/** The team's rules for the agent (naming, prefixes, patterns) in the first workspace folder that has them. */
async function teamRulesFile(): Promise<{ file: string; stamp: string } | undefined> {
    for (const folder of vscode.workspace.workspaceFolders ?? []) {
        const file = vscode.Uri.joinPath(folder.uri, RulesFile).fsPath;
        const stat = await fs.stat(file).catch(() => undefined);
        if (stat?.isFile()) {
            return { file, stamp: `${stat.mtimeMs}` };
        }
    }
    return undefined;
}

export function registerMcpProvider(context: vscode.ExtensionContext, log: vscode.OutputChannel): void {
    const changed = new vscode.EventEmitter<void>();
    const rulesWatcher = vscode.workspace.createFileSystemWatcher(`**/${RulesFile}`);
    context.subscriptions.push(changed, rulesWatcher,
        rulesWatcher.onDidCreate(() => changed.fire()), rulesWatcher.onDidChange(() => changed.fire()), rulesWatcher.onDidDelete(() => changed.fire()));

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
                const args = ['--config', env.configFile, '--activity-log'];
                const project = await findActiveProject();
                if (project) {
                    args.push('--project', project);
                }
                const rules = await teamRulesFile();
                if (rules) {
                    args.push('--rules', rules.file);
                }
                log.appendLine(`X++ MCP server: config ${env.configFile}, project ${project ?? 'none'}${rules ? `, team rules ${rules.file}` : ''}`);
                // A different version makes VS Code restart the running server with the new arguments.
                const version = `${ServerVersion}+${createHash('sha1').update(`${env.configFile}|${project ?? ''}|${rules?.stamp ?? ''}`).digest('hex').slice(0, 8)}`;
                return [new vscode.McpStdioServerDefinition('F&O Studio', exe, args, {}, version)];
            } catch (err) {
                log.appendLine(`MCP server not started: ${(err as Error).message}`);
                return [];
            }
        },
    }));
}
