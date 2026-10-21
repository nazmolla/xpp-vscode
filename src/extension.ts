import * as vscode from 'vscode';
import { AotTreeProvider } from './aot';
import { XppBuilder } from './build';
import { XppCodeFileSystem, XppScheme } from './codeView';
import { FormDesigner } from './designer';
import { readActiveEnvironment } from './environment';
import { registerMcpProvider } from './mcpProvider';
import { openObject, XppDocumentSelector, XppNavigation } from './navigation';
import { PacCommands } from './pac';
import { findActiveProject, ProjectTreeProvider, readProject, selectProject } from './project';
import { XppServerClient } from './serverClient';
import { connectEnvironment } from './setup';
import { checkEnvironmentConnection } from './ude';

export function activate(context: vscode.ExtensionContext): void {
    const log = vscode.window.createOutputChannel('X++');
    const buildOutput = vscode.window.createOutputChannel('X++ Build');
    const server = new XppServerClient(context, log, findActiveProject);
    const builder = new XppBuilder(buildOutput);
    const projectTree = new ProjectTreeProvider();
    const aotTree = new AotTreeProvider();
    const codeFs = new XppCodeFileSystem(server);
    const navigation = new XppNavigation(server);
    const pac = new PacCommands();
    context.subscriptions.push(log, buildOutput, server, builder);

    registerMcpProvider(context, log);

    context.subscriptions.push(
        vscode.workspace.registerFileSystemProvider(XppScheme, codeFs, { isCaseSensitive: false }),
        projectTree.view = vscode.window.createTreeView('xpp.project', { treeDataProvider: projectTree }),
        vscode.window.createTreeView('xpp.aot', { treeDataProvider: aotTree, showCollapseAll: true }),
        vscode.languages.registerDefinitionProvider(XppDocumentSelector, navigation),
        vscode.languages.registerHoverProvider(XppDocumentSelector, navigation),
        vscode.workspace.onDidSaveTextDocument(doc => {
            if (doc.fileName.endsWith('.rnrproj')) {
                projectTree.refresh();
            }
        }),
        vscode.workspace.onDidChangeConfiguration(e => {
            if (e.affectsConfiguration('xpp.project')) {
                server.restart();
                projectTree.refresh();
            }
        }),
    );

    const command = (id: string, handler: (...args: any[]) => unknown) =>
        context.subscriptions.push(vscode.commands.registerCommand(id, async (...args: any[]) => {
            try {
                await handler(...args);
            } catch (err) {
                vscode.window.showErrorMessage((err as Error).message);
            }
        }));

    command('xpp.showEnvironment', async () => {
        log.appendLine(JSON.stringify(await readActiveEnvironment(), null, 2));
        log.show(true);
    });
    command('xpp.checkEnvironment', () => checkEnvironmentConnection(log));
    command('xpp.connectEnvironment', async () => {
        if (await connectEnvironment(context, log)) {
            server.restart();
            vscode.window.showInformationMessage('Environment connected. Reload the window so the X++ tools use the new configuration.', 'Reload')
                .then(choice => choice === 'Reload' ? vscode.commands.executeCommand('workbench.action.reloadWindow') : undefined);
        }
    });
    command('xpp.openObject', () => openObject(server));
    command('xpp.openFormDesigner', async (arg?: string | { name?: string; item?: { objectName: string } }) => {
        // Tree context menus pass the clicked node; the designer opens the base form of an extension.
        const fromNode = typeof arg === 'object' ? arg.name ?? arg.item?.objectName : arg;
        const name = fromNode?.split('.')[0] ?? await vscode.window.showInputBox({ title: 'Form name', placeHolder: 'CustTable' });
        if (name) {
            FormDesigner.open(context, server, name);
        }
    });
    command('xpp.refreshProject', () => projectTree.refresh());
    command('xpp.refreshAot', () => aotTree.refresh());
    command('xpp.selectProject', () => selectProject());
    command('xpp.build', async () => {
        const project = await readProject();
        await vscode.window.withProgress(
            { location: vscode.ProgressLocation.Notification, title: `Building ${project?.model ?? 'model'}…`, cancellable: true },
            async (_progress, token) => {
                const ok = await builder.build(project?.model, token);
                vscode.window.setStatusBarMessage(ok ? '$(check) X++ build succeeded' : '$(error) X++ build failed', 10000);
            });
    });
    command('xpp.pac.init', () => pac.initSolution());
    command('xpp.pac.signIn', () => pac.signIn());
    command('xpp.pac.deploy', () => pac.deploy());
    command('xpp.pac.dbSync', () => pac.syncDatabase());
}

export function deactivate(): void { }
