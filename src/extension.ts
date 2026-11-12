import { promises as fs } from 'fs';
import * as os from 'os';
import * as path from 'path';
import * as vscode from 'vscode';
import { AotTreeProvider } from './aot';
import { XppBuilder } from './build';
import { DeployState } from './deployState';
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
import { registerGoTo, registerOpenElementTool } from './goTo';
import { registerHistory } from './history';
import { registerMcpProvider } from './mcpProvider';
import { openObject, XppDocumentSelector, XppNavigation } from './navigation';
import { PacCommands } from './pac';
import { createProject, findActiveProject, ProjectTreeProvider, ProjectWorkspaces, readProject } from './project';
import { XppServerClient } from './serverClient';
import { ServerStatus } from './serverStatus';
import { ProjectStatus } from './projectStatus';
import { TaskType, XppTaskProvider } from './tasks';
import { registerTestExplorer } from './testExplorer';
import { registerPerformance } from './performance';
import { registerUpgradeReadiness } from './upgrade';
import { registerTelemetryQueries } from './telemetry';
import { ActivityView, showChanges } from './activity';
import { createTeamRules } from './teamRules';
import { ChangeDecorations, changedElements } from './changes';
import { reviewChanges } from './review';
import { registerChatParticipant } from './chat';
import { preDeployChecklist } from './checklist';
import { newSysOperationJob } from './scaffolding/batch';
import { newReport, newService, newWorkflow } from './scaffolding/wizards';
import { newElement } from './scaffolding/newElement';
import { addNumberSequence, newBusinessEvent, newFeature } from './scaffolding/lifecycleWizards';
import { findUnusedLabels, translateMissingLabels } from './labels';
import { registerTraceAnalyzer } from './traceAnalyzer';
import { XppRename } from './rename';
import { elementOf as treeElementOf, pickElement } from './d365Tools';
import { clearSetupStatus, connectEnvironment } from './setup';
import { cleanUpOldVersions, VersionGuard } from './versionGuard';
import { openTeamRepository } from './teamRepository';
import { verifyInRunningApp } from './verify';
import { AgentSession } from './agentSession';
import { registerIntegration } from './integration';
import { recordError, reportProblem } from './reportProblem';
import { registerTests } from './tests';
import { checkEnvironmentConnection } from './ude';
import { CodeIntelligence } from './codeIntel/providers';
import { registerProductivity } from './productivity';
import { registerSecurity } from './security';
import { registerDatabase } from './database';
import { goToFinding, ResultsView } from './results';
import { registerPanelSerializer } from './webviewState';

export function activate(context: vscode.ExtensionContext): void {
    const log = vscode.window.createOutputChannel('X++');
    const buildOutput = vscode.window.createOutputChannel('X++ Build');
    initEnvironmentSelection(context);
    const server = new XppServerClient(context, log, findActiveProject);
    // Language features run in a server process of their own, so index builds never block the designers.
    const languageServer = new XppServerClient(context, log, findActiveProject);
    const builder = new XppBuilder(buildOutput);
    const deployState = new DeployState(context, builder);
    const projectTree = new ProjectTreeProvider();
    const aotTree = new AotTreeProvider();
    const codeFs = new XppCodeFileSystem(server);
    const navigation = new XppNavigation(server);
    const pac = new PacCommands();
    const versionGuard = new VersionGuard();
    const environment = new EnvironmentStatus(versionGuard);
    const dragAndDrop = new ElementDragAndDrop(server, () => projectTree.refresh());
    const designers: DesignerServices = { context, server, drags: dragAndDrop };
    const projects = new ProjectWorkspaces(context);
    registerPanelSerializer<{ objectType: string; objectName: string }>(context, 'xppElementDesigner', (panel, state) => ElementDesigner.revive(designers, panel, state));
    registerPanelSerializer<{ formName: string }>(context, 'xppFormDesigner', (panel, state) => FormDesigner.revive(context, server, panel, state));
    registerPanelSerializer<{ labelFile: string }>(context, 'xppLabelEditor', (panel, state) =>
        LabelEditor.revive(context, server, panel, state, () => ElementDesigner.open(designers, 'AxLabelFile', state.labelFile)));
    void projects.migrateUserSetting().catch(err => log.appendLine('Project setting: ' + (err as Error).message));
    context.subscriptions.push(log, buildOutput, server, languageServer, builder, deployState, versionGuard, environment, new ServerStatus([server, languageServer]),
        new ProjectStatus(builder, deployState), vscode.tasks.registerTaskProvider(TaskType, new XppTaskProvider(builder)));

    registerMcpProvider(context, log);

    context.subscriptions.push(
        vscode.workspace.registerFileSystemProvider(XppScheme, codeFs, { isCaseSensitive: false }),
        projectTree.view = vscode.window.createTreeView<Parameters<typeof projectTree.getTreeItem>[0]>('xpp.project', { treeDataProvider: projectTree, dragAndDropController: dragAndDrop, canSelectMany: true }),
        aotTree.view = vscode.window.createTreeView<Parameters<typeof aotTree.getTreeItem>[0]>('xpp.aot', { treeDataProvider: aotTree, showCollapseAll: true, dragAndDropController: dragAndDrop, canSelectMany: true }),
        vscode.languages.registerDocumentDropEditProvider(XppDocumentSelector, new ElementDropIntoCode(), { dropMimeTypes: [ElementsMime, 'text/uri-list'] }),
        vscode.languages.registerDefinitionProvider(XppDocumentSelector, navigation),
        vscode.languages.registerReferenceProvider(XppDocumentSelector, navigation),
        vscode.languages.registerHoverProvider(XppDocumentSelector, navigation),
        vscode.languages.registerRenameProvider(XppDocumentSelector, new XppRename(navigation, server)),
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
            }
        }),
        onDidChangeEnvironment(() => {
            server.restart();
            languageServer.restart();
            aotTree.refresh();
            projectTree.refresh();
            // Open tabs show the new environment's metadata without a window reload.
            ElementDesigner.refreshAll();
            for (const doc of vscode.workspace.textDocuments.filter(d => d.uri.scheme === XppScheme && !d.isDirty)) {
                codeFs.refresh(doc.uri);
            }
        }),
        projectTree,
    );

    const command = (id: string, handler: (...args: any[]) => unknown) =>
        context.subscriptions.push(vscode.commands.registerCommand(id, async (...args: any[]) => {
            countUsage(context, id);
            try {
                return await handler(...args);
            } catch (err) {
                const error = err instanceof Error ? err : new Error(String(err));
                log.appendLine(`[${id}] ${error.stack ?? error.message}`);
                recordError(id, error);
                const message = error.message || 'The command failed.';
                vscode.window.showErrorMessage(message, 'Show Log', 'Report Problem').then(choice => {
                    if (choice === 'Show Log') {
                        log.show(true);
                    } else if (choice === 'Report Problem') {
                        void reportProblem(context, UsageKey, id);
                    }
                });
            }
        }));

    command('xpp.showEnvironment', async () => {
        log.appendLine(JSON.stringify(await readActiveEnvironment(), null, 2));
        log.show(true);
    });
    command('xpp.selectEnvironment', () => selectEnvironment());
    command('xpp.environment.menu', () => environment.menu());
    command('xpp.reportProblem', () => reportProblem(context, UsageKey));
    command('xpp.restartServer', () => vscode.window.withProgress({ location: vscode.ProgressLocation.Window, title: 'Restarting the X++ server' }, async () => {
        server.restart();
        languageServer.restart();
        await Promise.all([server.bridge('get_environment'), languageServer.bridge('get_environment')]);
    }));
    command('xpp.checkEnvironment', () => checkEnvironmentConnection(log));
    command('xpp.environment.clearSetup', () => clearSetupStatus());
    command('xpp.cleanUpOldVersions', () => cleanUpOldVersions());
    command('xpp.checkVersion', async () => {
        const drift = await versionGuard.check();
        if (!drift) {
            vscode.window.showInformationMessage('The metadata on this machine matches the environment\'s application version (or the environment could not be reached without signing in).');
        }
    });
    command('xpp.connectEnvironment', async (dataverseUrl?: string) => {
        if (await connectEnvironment(context, log, typeof dataverseUrl === 'string' ? dataverseUrl : undefined)) {
            vscode.window.showInformationMessage('Environment connected. The Application Explorer, designers and code views now use it.');
        }
    });
    command('xpp.openObject', () => openObject(server, context));
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
    new XppDebugger(server, log, deployState).register(context);
    const codeIntel = new CodeIntelligence(languageServer, server, navigation, log);
    context.subscriptions.push(codeIntel);
    codeIntel.register(command);
    const dataTools = registerDatabase(context, command, server);
    registerTools(context, server, () => {
        aotTree.refresh();
        projectTree.refresh();
    }, command, [environment, dataTools, codeIntel]);
    const productivity = {
        server, languageServer, log, codeIntel,
        refreshCode: (uri: vscode.Uri) => codeFs.refresh(uri),
        refreshTrees: () => {
            aotTree.refresh();
            projectTree.refresh();
        },
    };
    registerProductivity(context, command, productivity);
    const agentSession = new AgentSession(server, productivity.refreshTrees);
    context.subscriptions.push(agentSession);
    agentSession.register(command);
    registerSecurity(context, command, productivity);
    registerPerformance(context, command, server);
    const results = new ResultsView();
    results.register(context, command);
    command('xpp.results.goTo', (location: Parameters<typeof goToFinding>[0]) => goToFinding(location));
    command('xpp.results.goToElement', async (objectName: string, method?: string, line?: number) => {
        const found = await server.bridge('find_object', { objectName, exactMatch: true, maxResults: 5 });
        const match = (found.matches ?? [])[0];
        if (!match) {
            throw new Error(`${objectName} was not found.`);
        }
        await goToFinding({ objectType: match.objectType, objectName: match.objectName, method, line: method ? line : undefined });
    });
    registerUpgradeReadiness(command, server, results);
    registerIntegration(command, server);
    registerTelemetryQueries(command);
    const activity = new ActivityView();
    context.subscriptions.push(activity, vscode.window.registerTreeDataProvider('xpp.activity', activity));
    command('xpp.activity.showChanges', (entry: Parameters<typeof showChanges>[0]) => showChanges(entry));
    command('xpp.activity.clear', () => activity.clear());
    command('xpp.createTeamRules', () => createTeamRules());
    const changeDecorations = new ChangeDecorations();
    context.subscriptions.push(changeDecorations);
    command('xpp.reviewChanges', (base?: unknown) => reviewChanges(server, builder, results, typeof base === 'string' ? base : undefined));
    registerChatParticipant(context);
    command('xpp.preDeployChecklist', () => preDeployChecklist(server, builder, results));
    command('xpp.newSysOperationJob', () => newSysOperationJob(server).then(() => projectTree.refresh()));
    command('xpp.newReport', () => newReport(server).then(() => projectTree.refresh()));
    command('xpp.newWorkflow', () => newWorkflow(server).then(() => projectTree.refresh()));
    command('xpp.newService', () => newService(server).then(() => projectTree.refresh()));
    command('xpp.newBusinessEvent', () => newBusinessEvent(server).then(() => projectTree.refresh()));
    command('xpp.addNumberSequence', () => addNumberSequence(server).then(() => projectTree.refresh()));
    command('xpp.newFeature', () => newFeature(server).then(() => projectTree.refresh()));
    command('xpp.showFeatureUsage', async (arg?: { item?: { objectType: string; objectName: string }; objectType?: string; entry?: { name: string } }) => {
        const element = treeElementOf(arg) ?? await pickElement(server, 'Show feature usage: feature class', ['AxClass']);
        if (!element) {
            return;
        }
        const result = await vscode.window.withProgress({ location: { viewId: 'xpp.results' }, title: `Finding where ${element.objectName} is checked…` },
            () => server.bridge('where_used', { objectType: 'AxClass', objectName: element.objectName, maxResults: '1000' }));
        if (!result.success) {
            throw new Error(result.message);
        }
        const refs = (result.references ?? []) as { objectType?: string; objectName: string; member?: string; line?: number; kind?: string; module?: string }[];
        const findings = refs.filter(r => r.objectName !== element.objectName).map(r => ({
            label: `${r.objectName}${r.member ? '.' + r.member : ''}`, description: `line ${r.line ?? '?'} · ${r.module ?? ''}`, severity: 'info' as const,
            command: { command: 'xpp.results.goToElement', title: 'Open', arguments: [r.objectName, r.member, r.line] },
        }));
        await results.show({
            title: `Feature usage: ${element.objectName}`, time: new Date(), rerun: { command: 'xpp.showFeatureUsage', args: [{ item: element }] },
            summary: `${findings.length} place(s) check or use ${element.objectName}`,
            markdown: [`# Where ${element.objectName} is used`, '', ...findings.map(f => `- ${f.label} (${f.description})`)].join('\n'),
            sections: [{ title: 'Code gated by the feature', severity: findings.length ? 'info' : 'warning', description: findings.length ? undefined : 'not used: the feature gates nothing (or the cross references are not built)', findings }],
        });
    });
    // A project folder node pre-selects its element type.
    command('xpp.newElement', (arg?: string | { kind?: string; items?: { objectType: string }[] }) =>
        newElement(context, server, productivity.refreshTrees, typeof arg === 'string' ? arg : arg?.items?.[0]?.objectType));
    command('xpp.translateMissingLabels', () => translateMissingLabels(server));
    command('xpp.findUnusedLabels', () => findUnusedLabels(server));
    registerTraceAnalyzer(context, command, server);
    command('xpp.syncTables', async (arg?: ElementRef, selected?: ElementRef[]) => {
        const picked = (selected?.length ? selected : arg ? [arg] : []).map(a => treeElementOf(a)).filter(e => e?.objectType === 'AxTable');
        const tables = picked.length ? picked.map(e => e!.objectName) : [(await pickElement(server, 'Synchronize table', ['AxTable']))?.objectName].filter((t): t is string => !!t);
        if (!tables.length) {
            return;
        }
        const result = await vscode.window.withProgress({ location: vscode.ProgressLocation.Notification, title: `Synchronizing ${tables.join(', ')}…` },
            () => server.bridge('sync_tables', { tables }));
        (result.success ? vscode.window.showInformationMessage : vscode.window.showErrorMessage)(String(result.message ?? '').split('\n')[0]);
        log.appendLine(String(result.message ?? ''));
    });
    command('xpp.showUsageStatistics', async () => {
        const counts = context.globalState.get<Record<string, number>>(UsageKey, {});
        const rows = Object.entries(counts).sort((a, b) => b[1] - a[1]);
        const content = ['# F&O Studio usage on this machine', '', vscode.workspace.getConfiguration('xpp').get<boolean>('usageStatistics')
            ? 'Counted locally; nothing is sent anywhere.' : 'Counting is off (setting `xpp.usageStatistics`).', '',
        '| Command | Runs |', '|---|---|', ...rows.map(([id, n]) => `| ${id} | ${n} |`)].join('\n');
        await vscode.window.showTextDocument(await vscode.workspace.openTextDocument({ language: 'markdown', content }), { preview: true });
    });
    command('xpp.showChangedElements', async () => {
        const base = vscode.workspace.getConfiguration('xpp').get<string>('git.baseBranch') || undefined;
        const changes = await changedElements(base);
        if (!changes.length) {
            vscode.window.showInformationMessage('No changed elements in your models (they must be in a Git repository).');
            return;
        }
        const pick = await vscode.window.showQuickPick(changes.map(c => ({
            label: c.objectName, description: `${c.objectType.replace(/^Ax/, '')} · ${c.status}${c.uncommitted ? '' : ' (branch)'}`, detail: c.model, change: c,
        })), { title: `Changed elements${base ? ` (since ${base}, plus uncommitted)` : ''}`, matchOnDescription: true });
        if (pick && pick.change.status !== 'D') {
            await vscode.commands.executeCommand('xpp.openDesigner', pick.change.objectType, pick.change.objectName);
        }
    });
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
    command('xpp.designer.viewCode', () => ElementDesigner.titleAction('viewCode'));
    command('xpp.designer.formLayout', () => ElementDesigner.titleAction('formLayout'));
    command('xpp.designer.refresh', () => ElementDesigner.titleAction('refresh'));
    command('xpp.designer.createExtension', () => ElementDesigner.titleAction('createExtension'));
    registerGoTo(command, server);
    registerOpenElementTool(context, server);
    registerHistory(context, server, command);
    context.subscriptions.push(ElementDesigner.onDidChange.event(e => codeFs.refresh(objectUri(e.objectType, e.objectName))));
    void vscode.commands.executeCommand('setContext', 'xpp.aot.modelView', false);
    command('xpp.selectProject', () => projects.select());
    command('xpp.openRecentProject', () => projects.openRecent());
    command('xpp.openProject', (file?: string) => file ? projects.open(file) : projects.select());
    command('xpp.closeProject', () => projects.close());
    command('xpp.openTeamRepository', () => openTeamRepository(builder, file => projects.open(file)));
    command('xpp.verifyInRunningApp', () => verifyInRunningApp(server, deployState));
    command('xpp.createProject', async () => {
        const file = await createProject();
        if (file) {
            await projects.open(file);
        }
    });
    const runBuild = async (title: string, model: string | undefined, items?: { objectType: string; objectName: string }[]) => {
        await saveCodeViews();
        const before = builder.lastResult;
        const ok = await vscode.window.withProgress({ location: vscode.ProgressLocation.Notification, title, cancellable: true },
            (_progress, token) => builder.build(model, token, items));
        const result = builder.lastResult !== before ? builder.lastResult : undefined;
        if (ok) {
            vscode.window.setStatusBarMessage(`$(check) Build succeeded${result?.warnings ? `: ${result.warnings} warning(s)` : ''}`, 10000);
        } else if (result) {
            const choice = await vscode.window.showErrorMessage(`Build failed: ${result.errors} error(s), ${result.warnings} warning(s) in ${result.model}.`, 'Show Problems', 'Show Output');
            if (choice === 'Show Problems') {
                await vscode.commands.executeCommand('workbench.actions.view.problems');
            } else if (choice === 'Show Output') {
                builder.showOutput();
            }
        }
        return ok;
    };
    command('xpp.build', async () => {
        const project = await readProject();
        return runBuild(`Building ${project?.model ?? 'model'}…`, project?.model);
    });
    // Ctrl+Shift+B and the project status bar item: the project when one is open, otherwise a model.
    command('xpp.buildProject', async () => {
        const project = await readProject();
        if (!project?.model) {
            return runBuild('Building model…', undefined);
        }
        return runBuild(`Building project ${project.name}…`, project.model, project.items);
    });
    registerTests(context, server, deployState);
    command('xpp.bestPractice', async () => {
        const project = await readProject();
        if (!project?.model) {
            throw new Error('Open a D365FO project first.');
        }
        const result = await vscode.window.withProgress(
            { location: vscode.ProgressLocation.Notification, title: `Best practice checks: ${project.name}…`, cancellable: true },
            (_progress, token) => builder.bestPractice(project.model!, token, project.items));
        if (result) {
            vscode.window.setStatusBarMessage(`$(${result.errors ? 'error' : 'check'}) Best practice: ${result.errors} error(s), ${result.warnings} warning(s)`, 10000);
        }
    });
    registerTestExplorer(context, server);
    followAgentProjectSelection(context, projects);
    command('xpp.pac.init', () => pac.initSolution());
    command('xpp.pac.signIn', () => pac.signIn());
    command('xpp.pac.deploy', async () => {
        const models = await pac.deploy();
        if (models?.length) {
            await deployState.deployed(models);
        }
    });
    command('xpp.pac.dbSync', () => pac.syncDatabase());
}

export function deactivate(): void { }

/** Unsaved code views would otherwise be compiled from the metadata on disk. */
async function saveCodeViews(): Promise<void> {
    if (!vscode.workspace.getConfiguration('xpp').get<boolean>('build.saveBeforeBuild', true)) {
        return;
    }
    await Promise.all(vscode.workspace.textDocuments.filter(d => d.isDirty && d.uri.scheme === XppScheme).map(d => d.save()));
}

const UsageKey = 'xpp.usage';

/** Opt-in, local-only command counts (xpp.usageStatistics); they never leave the machine. */
function countUsage(context: vscode.ExtensionContext, id: string): void {
    if (vscode.workspace.getConfiguration('xpp').get<boolean>('usageStatistics')) {
        const counts = context.globalState.get<Record<string, number>>(UsageKey, {});
        void context.globalState.update(UsageKey, { ...counts, [id]: (counts[id] ?? 0) + 1 });
    }
}

/** The MCP server writes the project an agent selected (xpp_select_project); the user decides whether the window opens it. */
function followAgentProjectSelection(context: vscode.ExtensionContext, projects: ProjectWorkspaces): void {
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
        if (selected && (await findActiveProject() ?? '').toLowerCase() !== selected.toLowerCase()) {
            // Opening a project reloads the window, which would interrupt the agent: ask.
            const choice = await vscode.window.showInformationMessage(`The agent selected project ${path.basename(selected, '.rnrproj')}.`, 'Open Project');
            if (choice) {
                await projects.open(selected);
            }
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
