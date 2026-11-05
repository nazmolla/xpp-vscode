import { promises as fs } from 'fs';
import * as os from 'os';
import * as path from 'path';
import * as vscode from 'vscode';
import { AotTreeProvider } from './aot';
import { XppBuilder } from './build';
import { XppReferenceLinks } from './codeLinks';
import { ElementDragAndDrop, ElementDropIntoCode, ElementsMime } from './dragAndDrop';
import { objectUri, parseObjectUri, XppCodeFileSystem, XppScheme } from './codeView';
import { FormDesigner } from './designer';
import { registerTools } from './d365Tools';
import { XppDebugger } from './debug';
import { DesignerNodeContext, DesignerServices, ElementDesigner } from './elementDesigner';
import { LabelEditor } from './labelEditor';
import { initEnvironmentSelection, onDidChangeEnvironment, readActiveEnvironment } from './environment';
import { EnvironmentStatus, selectEnvironment } from './environmentPicker';
import { registerGoTo } from './goTo';
import { registerHistory } from './history';
import { registerMcpProvider } from './mcpProvider';
import { openObject, XppDocumentSelector, XppNavigation } from './navigation';
import { PacCommands } from './pac';
import { createProject, findActiveProject, ProjectTreeProvider, readProject, selectProject, syncWorkspaceFolders } from './project';
import { XppServerClient } from './serverClient';
import { connectEnvironment } from './setup';
import { registerTests } from './tests';
import { checkEnvironmentConnection } from './ude';
import { CodeIntelligence } from './codeIntel/providers';
import { registerProductivity } from './productivity';
import { registerSecurity } from './security';

export function activate(context: vscode.ExtensionContext): void {
    const log = vscode.window.createOutputChannel('X++');
    const buildOutput = vscode.window.createOutputChannel('X++ Build');
    initEnvironmentSelection(context);
    const server = new XppServerClient(context, log, findActiveProject);
    // Language features run in a server process of their own, so index builds never block the designers.
    const languageServer = new XppServerClient(context, log, findActiveProject);
    const builder = new XppBuilder(buildOutput);
    const projectTree = new ProjectTreeProvider();
    const aotTree = new AotTreeProvider();
    const codeFs = new XppCodeFileSystem(server);
    const navigation = new XppNavigation(server);
    const pac = new PacCommands();
    const environment = new EnvironmentStatus();
    const dragAndDrop = new ElementDragAndDrop(server, () => projectTree.refresh());
    const designers: DesignerServices = { context, server, drags: dragAndDrop };
    context.subscriptions.push(log, buildOutput, server, languageServer, builder, environment);

    registerMcpProvider(context, log);

    context.subscriptions.push(
        vscode.workspace.registerFileSystemProvider(XppScheme, codeFs, { isCaseSensitive: false }),
        projectTree.view = vscode.window.createTreeView<Parameters<typeof projectTree.getTreeItem>[0]>('xpp.project', { treeDataProvider: projectTree, dragAndDropController: dragAndDrop, canSelectMany: true }),
        aotTree.view = vscode.window.createTreeView<Parameters<typeof aotTree.getTreeItem>[0]>('xpp.aot', { treeDataProvider: aotTree, showCollapseAll: true, dragAndDropController: dragAndDrop, canSelectMany: true }),
        vscode.languages.registerDocumentDropEditProvider(XppDocumentSelector, new ElementDropIntoCode(), { dropMimeTypes: [ElementsMime, 'text/uri-list'] }),
        vscode.languages.registerDefinitionProvider(XppDocumentSelector, navigation),
        vscode.languages.registerReferenceProvider(XppDocumentSelector, navigation),
        vscode.languages.registerHoverProvider(XppDocumentSelector, navigation),
        vscode.languages.registerDocumentLinkProvider(XppDocumentSelector, new XppReferenceLinks()),
        vscode.workspace.onDidSaveTextDocument(doc => {
            if (doc.fileName.endsWith('.rnrproj')) {
                projectTree.refresh();
            }
        }),
        vscode.workspace.onDidChangeConfiguration(e => {
            if (e.affectsConfiguration('xpp.project')) {
                server.restart();
                languageServer.restart();
                projectTree.refresh();
                // U7: the workspace follows the selected or created project (project folder + model folder).
                void syncWorkspaceFolders().catch(err => log.appendLine('Workspace folders: ' + (err as Error).message));
            }
        }),
        onDidChangeEnvironment(() => {
            server.restart();
            languageServer.restart();
            aotTree.refresh();
            projectTree.refresh();
        }),
        projectTree,
    );

    const command = (id: string, handler: (...args: any[]) => unknown) =>
        context.subscriptions.push(vscode.commands.registerCommand(id, async (...args: any[]) => {
            try {
                return await handler(...args);
            } catch (err) {
                vscode.window.showErrorMessage((err as Error).message);
            }
        }));

    command('xpp.showEnvironment', async () => {
        log.appendLine(JSON.stringify(await readActiveEnvironment(), null, 2));
        log.show(true);
    });
    command('xpp.selectEnvironment', () => selectEnvironment());
    command('xpp.checkEnvironment', () => checkEnvironmentConnection(log));
    command('xpp.connectEnvironment', async () => {
        if (await connectEnvironment(context, log)) {
            server.restart();
            vscode.window.showInformationMessage('Environment connected. Reload the window so the X++ tools use the new configuration.', 'Reload')
                .then(choice => choice === 'Reload' ? vscode.commands.executeCommand('workbench.action.reloadWindow') : undefined);
        }
    });
    command('xpp.openObject', () => openObject(server));
    command('xpp.openFormDesigner', async (arg?: string | ElementRef) => {
        // Tree context menus pass the clicked node; the layout view opens the base form of an extension.
        const fromNode = typeof arg === 'string' ? arg : elementOf(arg)?.objectName;
        const name = fromNode?.split('.')[0] ?? await vscode.window.showInputBox({ title: 'Form name', placeHolder: 'CustTable' });
        if (name) {
            FormDesigner.open(context, server, name);
        }
    });
    command('xpp.refreshProject', () => projectTree.refresh());
    command('xpp.refreshAot', () => aotTree.refresh());
    command('xpp.openDesigner', async (arg?: string | ElementRef | vscode.Uri, name?: string, labelId?: string) => {
        const element = elementFromArg(arg, name);
        if (element?.objectName && element.objectType === 'AxLabelFile') {
            // U8: a label file opens on its labels; its properties are one click away.
            LabelEditor.open(context, server, element.objectName,
                () => ElementDesigner.open(designers, element.objectType, element.objectName), labelId);
        } else if (element?.objectName) {
            ElementDesigner.open(designers, element.objectType, element.objectName);
        }
    });
    command('xpp.openDesignerToSide', async (arg?: ElementRef) => {
        const element = elementOf(arg);
        if (element?.objectType === 'AxClass') {
            await vscode.window.showTextDocument(objectUri(element.objectType, element.objectName), { viewColumn: vscode.ViewColumn.Beside });
        } else if (element) {
            ElementDesigner.open(designers, element.objectType, element.objectName, vscode.ViewColumn.Beside);
        }
    });
    command('xpp.revealInAot', async (arg?: ElementRef | vscode.Uri) => {
        const element = elementFromArg(arg);
        if (element && !await aotTree.reveal(element.objectType, element.objectName)) {
            vscode.window.showInformationMessage(`${element.objectName} is not in the Application Explorer.`);
        }
    });
    command('xpp.viewCode', async (arg?: ElementRef) => {
        const element = elementOf(arg);
        if (element) {
            await vscode.commands.executeCommand('vscode.open', objectUri(element.objectType, element.objectName));
        }
    });
    command('xpp.aot.modelView', () => aotTree.setModelView(true));
    command('xpp.aot.classicView', () => aotTree.setModelView(false));
    command('xpp.aot.filter', async (value?: unknown) => {
        const filter = typeof value === 'string' ? value
            : await vscode.window.showInputBox({ title: 'Filter Application Explorer', prompt: 'Part of an element name', value: aotTree.filter });
        if (filter !== undefined) {
            aotTree.setFilter(filter);
        }
    });
    command('xpp.aot.clearFilter', () => aotTree.setFilter(''));
    new XppDebugger(server, log).register(context);
    const codeIntel = new CodeIntelligence(languageServer, server, navigation, log);
    context.subscriptions.push(codeIntel);
    codeIntel.register(command);
    registerTools(context, server, () => {
        aotTree.refresh();
        projectTree.refresh();
    }, command, [environment, codeIntel]);
    const productivity = {
        server, languageServer, log, codeIntel,
        refreshCode: (uri: vscode.Uri) => codeFs.refresh(uri),
        refreshTrees: () => {
            aotTree.refresh();
            projectTree.refresh();
        },
    };
    registerProductivity(context, command, productivity);
    registerSecurity(context, command, productivity);
    command('xpp.designer.new', (node: DesignerNodeContext) => ElementDesigner.runAction('new', node));
    command('xpp.designer.newMethod', (node: DesignerNodeContext) => ElementDesigner.runAction('newMethod', node));
    command('xpp.designer.override', (node: DesignerNodeContext) => ElementDesigner.runAction('override', node));
    command('xpp.designer.delete', (node: DesignerNodeContext) => ElementDesigner.runAction('delete', node));
    command('xpp.designer.openMethod', (node: DesignerNodeContext) => ElementDesigner.runAction('openMethod', node));
    command('xpp.designer.copyEventHandler', (node: DesignerNodeContext) => ElementDesigner.runAction('copyEventHandler', node));
    command('xpp.designer.findEventHandlers', (node: DesignerNodeContext) => ElementDesigner.runAction('findEventHandlers', node));
    command('xpp.designer.rename', (node: DesignerNodeContext) => ElementDesigner.runAction('rename', node));
    command('xpp.designer.duplicate', (node: DesignerNodeContext) => ElementDesigner.runAction('duplicate', node));
    command('xpp.designer.moveUp', (node: DesignerNodeContext) => ElementDesigner.runAction('moveUp', node));
    command('xpp.designer.moveDown', (node: DesignerNodeContext) => ElementDesigner.runAction('moveDown', node));
    command('xpp.designer.addElement', (node: DesignerNodeContext) => ElementDesigner.runAction('addElement', node));
    registerGoTo(command, server);
    registerHistory(context, server, command);
    context.subscriptions.push(ElementDesigner.onDidChange.event(e => codeFs.refresh(objectUri(e.objectType, e.objectName))));
    void vscode.commands.executeCommand('setContext', 'xpp.aot.modelView', false);
    command('xpp.selectProject', () => selectProject());
    command('xpp.createProject', () => createProject());
    command('xpp.build', async () => {
        const project = await readProject();
        await vscode.window.withProgress(
            { location: vscode.ProgressLocation.Notification, title: `Building ${project?.model ?? 'model'}…`, cancellable: true },
            async (_progress, token) => {
                const ok = await builder.build(project?.model, token);
                vscode.window.setStatusBarMessage(ok ? '$(check) X++ build succeeded' : '$(error) X++ build failed', 10000);
            });
    });
    command('xpp.buildProject', async () => {
        const project = await readProject();
        if (!project?.model) {
            throw new Error('Select a D365FO project first.');
        }
        return vscode.window.withProgress(
            { location: vscode.ProgressLocation.Notification, title: `Building project ${project.name}…`, cancellable: true },
            async (_progress, token) => {
                const ok = await builder.build(project.model, token, project.items);
                vscode.window.setStatusBarMessage(ok ? '$(check) X++ project build succeeded' : '$(error) X++ project build failed', 10000);
                return ok;
            });
    });
    registerTests(context, server);
    followAgentProjectSelection(context);
    command('xpp.pac.init', () => pac.initSolution());
    command('xpp.pac.signIn', () => pac.signIn());
    command('xpp.pac.deploy', () => pac.deploy());
    command('xpp.pac.dbSync', () => pac.syncDatabase());
}

export function deactivate(): void { }

/** The MCP server writes the project an agent selected (xpp_select_project); the views follow it through xpp.project. */
function followAgentProjectSelection(context: vscode.ExtensionContext): void {
    const stateFile = path.join(process.env.LOCALAPPDATA ?? os.homedir(), 'XppVsCode', 'activeProject.txt');
    let lastWrite = 0;
    const written = async () => (await fs.stat(stateFile).catch(() => undefined))?.mtimeMs ?? 0;
    const check = async () => {
        const mtime = await written();
        if (mtime <= lastWrite) {
            return;
        }
        lastWrite = mtime;
        const selected = (await fs.readFile(stateFile, 'utf8').catch(() => '')).trim();
        const config = vscode.workspace.getConfiguration('xpp');
        if (selected && (config.get<string>('project') ?? '').toLowerCase() !== selected.toLowerCase()) {
            await config.update('project', selected, vscode.ConfigurationTarget.Global);
        }
    };
    // Only selections written after startup count; a file left by an earlier session must not override the user's project.
    void written().then(initial => {
        lastWrite = initial;
        const timer = setInterval(() => void check(), 3000);
        context.subscriptions.push({ dispose: () => clearInterval(timer) });
    });
}

/** Tree nodes passed to context-menu commands: Application Explorer elements or project items. */
type ElementRef = { objectType?: string; entry?: { name: string }; item?: { objectType: string; objectName: string } };

function elementOf(node: ElementRef | undefined): { objectType: string; objectName: string } | undefined {
    if (node?.item) {
        return { objectType: node.item.objectType, objectName: node.item.objectName };
    }
    if (node?.objectType && node.entry) {
        return { objectType: node.objectType, objectName: node.entry.name };
    }
    return undefined;
}

/** A command's element: (type, name) strings, a tree node, or a code view document (editor context menu). */
function elementFromArg(arg: string | ElementRef | vscode.Uri | undefined, name?: string): { objectType: string; objectName: string } | undefined {
    if (typeof arg === 'string') {
        return { objectType: arg, objectName: name ?? '' };
    }
    if (arg instanceof vscode.Uri) {
        if (arg.scheme !== XppScheme) {
            return undefined;
        }
        const { objectType, objectName } = parseObjectUri(arg);
        return { objectType, objectName };
    }
    return elementOf(arg);
}
