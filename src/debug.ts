import { ChildProcessWithoutNullStreams, execFile, spawn } from 'child_process';
import { existsSync, promises as fs } from 'fs';
import * as net from 'net';
import * as os from 'os';
import * as path from 'path';
import * as vscode from 'vscode';
import { listModels } from './aot';
import { objectUri, parseObjectUri, XppScheme } from './codeView';
import { dynamics365Folder, readActiveEnvironment, selectedEnvironmentUrl } from './environment';
import { XppServerClient } from './serverClient';
import { DeployState } from './deployState';
import { getDataverseToken, readUdeEnvironment, resolveDataverseUrl } from './ude';
import { compilerView, documentLineToCompilerLine } from './xppSource';

export const DebugType = 'xpp';

/** Element types the compiler emits as their own source file in the debug symbols. */
const DebugSourceTypes = ['AxClass', 'AxTable', 'AxForm', 'AxView', 'AxDataEntityView', 'AxQuery', 'AxMap'];

// Compiled packages record their sources as e:\source\<package>\ax<type>_<name>.xpp (see the .pdb next to the dll).
// Standard packages name them xppSource://Source/<Model>\Ax<Type>_<Name>.xpp: those are sent as is (ProxyDebugAdapter).
const PdbSourceRoots = ['e:\\source\\', 'e:\\\\source\\', 'E:\\source\\'];
/** workspaceState: the debug source breakpoints that are copies of code view breakpoints. */
const CopiesKey = 'xpp.debug.breakpointCopies';

interface InfologEntry {
    kind: string;
    message: string;
    time: Date;
}

interface PendingAdapter {
    proxy: string;
    host: string;
    token: string;
    /** Debug source file (lower case) -> document name in the symbols, for standard elements with breakpoints. */
    documents?: Map<string, string>;
}

/**
 * Debugging a unified developer environment, the way Visual Studio does it: the FinOps tools' DebuggerProxy
 * (--VSVersion vsdbg) is the debug adapter, tunnelling to vsdbg next to AXHost.exe in the cloud.
 */
export class XppDebugger implements vscode.DebugConfigurationProvider, vscode.DebugAdapterDescriptorFactory {
    private pending: PendingAdapter | undefined;
    /** Your elements' debug source files (lower case) -> their document names in the symbols. */
    private readonly ownDocuments = new Map<string, string>();
    /** Code view breakpoint (breakpointKey) -> its copy on the debug source file, which is what the debugger binds. */
    private readonly breakpointLinks = new Map<string, string>();
    /** Standard elements prepared for the running session; breakpoints added meanwhile are copied for them too. */
    private sessionStandard: StandardBreakpointElement[] = [];
    /** Copy passes run one after another, so a change made during a pass is handled by the next one. */
    private mirrorQueue: Promise<void> = Promise.resolve();
    /** Remembers which debug source breakpoints are copies (breakpointKey); others were set there by you. */
    private state: vscode.Memento | undefined;
    readonly infolog = new InfologProvider();

    constructor(private readonly server: XppServerClient, private readonly log: vscode.OutputChannel, private readonly deployState: DeployState) { }

    register(context: vscode.ExtensionContext): void {
        this.state = context.workspaceState;
        context.subscriptions.push(
            vscode.debug.registerDebugConfigurationProvider(DebugType, this),
            vscode.debug.registerDebugAdapterDescriptorFactory(DebugType, this),
            vscode.debug.registerDebugAdapterTrackerFactory(DebugType, { createDebugAdapterTracker: () => this.tracker() }),
            // The local AOS is debugged with the C# extension's clr debugger; its output carries the same Infolog messages.
            vscode.debug.registerDebugAdapterTrackerFactory('clr', { createDebugAdapterTracker: () => this.tracker() }),
            this.infolog.view = vscode.window.createTreeView('xpp.infolog', { treeDataProvider: this.infolog }),
            vscode.commands.registerCommand('xpp.debug.start', () => vscode.debug.startDebugging(undefined, {
                type: DebugType, request: 'attach', name: 'Attach to Dynamics 365 (unified developer environment)',
            })),
            vscode.commands.registerCommand('xpp.infolog.clear', () => this.infolog.clear()),
            vscode.commands.registerCommand('xpp.infolog.filter', () => this.infolog.pickFilter()),
            vscode.commands.registerCommand('xpp.infolog.copy', () => this.infolog.copy()),
            // A breakpoint set in the code view during a session gets its copy on the debug source right away.
            vscode.debug.onDidChangeBreakpoints(event => {
                const codeView = (b: vscode.Breakpoint) => b instanceof vscode.SourceBreakpoint && b.location.uri.scheme === XppScheme;
                if (vscode.debug.activeDebugSession?.type === DebugType && [...event.added, ...event.changed, ...event.removed].some(codeView)) {
                    this.mirrorBreakpoints(this.sessionStandard).catch(error => this.log.appendLine(`Breakpoint copy failed: ${(error as Error).message}`));
                }
            }),
        );
    }

    provideDebugConfigurations(): vscode.DebugConfiguration[] {
        return [{ type: DebugType, request: 'attach', name: 'Attach to Dynamics 365 (unified developer environment)' }];
    }

    async resolveDebugConfiguration(_folder: vscode.WorkspaceFolder | undefined, config: vscode.DebugConfiguration): Promise<vscode.DebugConfiguration | undefined> {
        if (!config.type) {
            config = this.provideDebugConfigurations()[0];
        }
        const environment = await readActiveEnvironment().catch(() => undefined);
        if (environment?.local) {
            await this.attachLocal(config);
            return undefined;
        }
        if (!await this.deployState.confirmCurrent('The debugger')) {
            return undefined;
        }
        return vscode.window.withProgress({ location: vscode.ProgressLocation.Notification, title: 'Preparing X++ debugging…' }, async progress => {
            progress.report({ message: 'signing in' });
            const dataverseSetting = selectedEnvironmentUrl();
            if (!dataverseSetting) {
                vscode.window.showErrorMessage('Select the environment first: F&O Studio: Select Environment.');
                return undefined;
            }
            const dataverseUrl = await resolveDataverseUrl(dataverseSetting);
            const token = await getDataverseToken(dataverseUrl);
            const env = await readUdeEnvironment(dataverseUrl, token);
            if (!env.finOpsUrl) {
                throw new Error('The environment did not return its finance and operations URL.');
            }
            const proxy = await findDebuggerProxy(env.applicationVersion);
            this.pending = { proxy, host: new URL(env.finOpsUrl).host, token };

            progress.report({ message: 'writing debug sources' });
            const standard = await this.standardBreakpointElements();
            const packages = await this.writeDebugSources(standard);
            this.sessionStandard = standard;
            await this.mirrorBreakpoints(standard);
            this.pending.documents = new Map([...this.ownDocuments, ...standard.filter(e => e.document)
                .map(e => [debugSourceFile(e.sourceFolder, e.objectType, e.objectName).toLowerCase(), e.document!] as [string, string])]);
            if (standard.length) {
                this.log.appendLine(`Debugging standard code too: ${standard.map(e => `${e.objectName} (${e.module ?? `all of ${e.packageName}`}${e.document ? `, ${e.document}` : ''})`).join(', ')}.`);
            }

            const sourceRoot = debugSourceRoot();
            const sourceFileMap: Record<string, string> = {};
            for (const root of PdbSourceRoots) {
                sourceFileMap[root] = sourceRoot + path.sep;
            }
            return {
                ...config,
                processName: config.processName ?? 'AXHost.exe',
                console: 'internalConsole',
                requireExactSource: false,
                // Microsoft's packages are optimized "non-user" code: Just My Code would skip their breakpoints.
                justMyCode: config.justMyCode ?? standard.length === 0,
                // X++ throws and catches exceptions all the time; without Just My Code each one would be printed.
                logging: config.logging ?? { exceptions: false },
                sourceFileMap: { ...sourceFileMap, ...(config.sourceFileMap ?? {}) },
                symbolOptions: config.symbolOptions ?? {
                    // Your modules and the module holding each standard breakpoint first; any other module's symbols
                    // load when it appears in a call stack or you step into it.
                    moduleFilter: {
                        mode: 'loadOnlyIncluded',
                        includedModules: XppDebugger.includedModules(packages, standard),
                        includeSymbolsNextToModules: false,
                        includeSymbolsOnDemand: true,
                    },
                },
            };
        });
    }

    /**
     * Cloud-hosted and VHD environments run the AOS on this machine (w3wp.exe in IIS, Batch.exe for batch): their .NET
     * Framework process is attached with the C# extension's vsdbg ("clr"), with the symbols in each package's bin folder.
     * Local builds record the same e:\source\ paths as the cloud's, so the debug sources are written the same way.
     */
    private async attachLocal(config: vscode.DebugConfiguration): Promise<void> {
        if (!vscode.extensions.getExtension('ms-dotnettools.csharp')) {
            const choice = await vscode.window.showErrorMessage('Debugging the local AOS uses the C# extension\'s .NET Framework debugger. Install the C# extension (ms-dotnettools.csharp) and try again.', 'Install');
            if (choice) {
                await vscode.commands.executeCommand('workbench.extensions.installExtension', 'ms-dotnettools.csharp');
            }
            return;
        }
        const processes = await localAosProcesses();
        if (!processes.length) {
            vscode.window.showErrorMessage('No AOS process is running (w3wp.exe or Batch.exe). Open the web client once to start it, then try again.');
            return;
        }
        const picked = processes.length === 1 && processes[0].name.toLowerCase() === 'w3wp.exe' ? processes[0]
            : (await vscode.window.showQuickPick(processes.map(p => ({ label: p.name, description: `process ${p.pid}`, process: p })),
                { title: 'Attach the X++ debugger to' }))?.process;
        if (!picked) {
            return;
        }
        const models = (await listModels()).filter(m => m.isCustom);
        const standard = await this.standardBreakpointElements();
        const packages = await vscode.window.withProgress({ location: vscode.ProgressLocation.Notification, title: 'Preparing X++ debugging: writing debug sources…' },
            () => this.writeDebugSources(standard));
        await this.mirrorBreakpoints(standard);
        const sourceFileMap: Record<string, string> = {};
        for (const root of PdbSourceRoots) {
            sourceFileMap[root] = debugSourceRoot() + path.sep;
        }
        const env = await readActiveEnvironment();
        await vscode.debug.startDebugging(undefined, {
            type: 'clr',
            request: 'attach',
            name: `Attach to the local AOS (${picked.name} ${picked.pid})`,
            processId: picked.pid,
            justMyCode: config.justMyCode ?? standard.length === 0,
            logging: config.logging ?? { exceptions: false },
            requireExactSource: false,
            sourceFileMap: { ...sourceFileMap, ...(config.sourceFileMap ?? {}) },
            symbolOptions: {
                searchPaths: [...new Set([
                    ...models.map(m => path.join(path.dirname(m.folder), 'bin')),
                    ...standard.map(e => path.join(env.frameworkDirectory, e.packageName, 'bin')),
                ])],
                searchMicrosoftSymbolServer: false,
                moduleFilter: {
                    mode: 'loadOnlyIncluded',
                    includedModules: XppDebugger.includedModules(packages, standard),
                    includeSymbolsNextToModules: true,
                    includeSymbolsOnDemand: true,
                },
            },
        });
    }

    async createDebugAdapterDescriptor(): Promise<vscode.DebugAdapterDescriptor> {
        const pending = this.pending;
        this.pending = undefined;
        if (!pending) {
            throw new Error('Start X++ debugging from the Run and Debug view or F&O Studio: Launch Debugger.');
        }
        // The same arguments Visual Studio passes in its VSDbgLaunch.json; a free port so a Visual Studio session or a
        // leftover proxy on the default 5119 does not stop the proxy.
        const port = await freePort();
        return new vscode.DebugAdapterInlineImplementation(new ProxyDebugAdapter(pending.proxy, [
            '--port', String(port), '--remoteProxyHost', pending.host, '--VSVersion', 'vsdbg', '--accessToken', pending.token,
        ], this.log, pending.documents, this.breakpointLinks));
    }

    /** Infolog messages arrive in the debugger output as <InfologMessage> XML, as in Visual Studio's Infolog window. */
    private tracker(): vscode.DebugAdapterTracker {
        return {
            onDidSendMessage: (message: any) => {
                if (message?.type === 'event' && message.event === 'output' && typeof message.body?.output === 'string') {
                    for (const entry of parseInfolog(message.body.output)) {
                        this.infolog.add(entry);
                        this.log.appendLine(`[Infolog ${entry.kind}] ${entry.message}`);
                    }
                }
            },
            onError: error => this.log.appendLine(`Debugger error: ${error.message}`),
        };
    }

    /** Writes each element of your packages as the compiler numbered it, under the paths the debug symbols use. */
    private async writeDebugSources(standard: StandardBreakpointElement[] = []): Promise<string[]> {
        const root = debugSourceRoot();
        const models = (await listModels()).filter(m => m.isCustom);
        const packages = [...new Set(models.map(m => m.packageName))];
        this.ownDocuments.clear();
        for (const model of models) {
            const dir = path.join(root, model.packageName.toLowerCase());
            await fs.mkdir(dir, { recursive: true });
            for (const objectType of DebugSourceTypes) {
                const files = await fs.readdir(path.join(model.folder, objectType)).catch(() => [] as string[]);
                for (const file of files.filter(f => f.toLowerCase().endsWith('.xml'))) {
                    const objectName = file.slice(0, -4);
                    const text = await this.debugSourceText(objectType, objectName);
                    if (text !== undefined) {
                        const local = debugSourceFile(model.packageName, objectType, objectName);
                        await fs.writeFile(local, text, 'utf8');
                        // The symbols name it xppSource://Source/<model folder>\<AxType>_<Name>.xpp (e.g. testcust\AxClass_X.xpp).
                        this.ownDocuments.set(local.toLowerCase(), `xppSource://Source/${path.basename(model.folder)}\\${objectType}_${objectName}.xpp`);
                    }
                }
            }
        }
        // Standard elements: only those with a breakpoint (a standard package has tens of thousands of elements).
        for (const element of standard) {
            const text = await this.debugSourceText(element.objectType, element.objectName);
            if (text !== undefined) {
                await fs.mkdir(path.join(root, element.sourceFolder.toLowerCase()), { recursive: true });
                await fs.writeFile(debugSourceFile(element.sourceFolder, element.objectType, element.objectName), text, 'utf8');
            }
        }
        return packages;
    }

    /**
     * The element's source as the compiler numbered it (the line numbers in its symbols). For forms that is the whole
     * form, data source and control classes included, which the code view doesn't show.
     */
    private async debugSourceText(objectType: string, objectName: string): Promise<string | undefined> {
        const complete = await this.server.bridge('complete_source', { objectType, objectName }).catch(() => undefined);
        if (complete?.success && typeof complete.text === 'string') {
            return complete.text;
        }
        const result = await this.server.bridge('read_object', { objectType, objectName }).catch(() => undefined);
        return result?.success ? compilerView(result.declaration ?? '', result.methods ?? []) : undefined;
    }

    /** The modules whose symbols are loaded: your packages, and for standard breakpoints only the module holding the element. */
    private static includedModules(packages: string[], standard: StandardBreakpointElement[]): string[] {
        return [...new Set([
            ...packages.flatMap(p => [`Dynamics.AX.${p}.dll`, `Dynamics.AX.${p}.*`]),
            ...standard.flatMap(e => e.module ? [`${e.module}.*`, `${e.module}.dll`, `Dynamics.AX.${e.packageName}.dll`] : [`Dynamics.AX.${e.packageName}.*`]),
        ])];
    }

    /** Standard (not your own) elements that have a breakpoint in the X++ code view. */
    private async standardBreakpointElements(): Promise<StandardBreakpointElement[]> {
        const models = await listModels();
        const found = new Map<string, StandardBreakpointElement>();
        for (const bp of vscode.debug.breakpoints) {
            if (!(bp instanceof vscode.SourceBreakpoint) || bp.location.uri.scheme !== XppScheme) {
                continue;
            }
            const { objectType, objectName, methodPath } = parseObjectUri(bp.location.uri);
            if (methodPath || !DebugSourceTypes.includes(objectType) || found.has(`${objectType}/${objectName}`.toLowerCase())) {
                continue;
            }
            const owner = await this.server.bridge('read_object', { objectType, objectName }).catch(() => undefined);
            const model = models.find(m => m.name.toLowerCase() === String(owner?.modelName ?? '').toLowerCase());
            if (model && !model.isCustom) {
                const env = await readActiveEnvironment();
                const sourceFolder = path.basename(model.folder);
                const symbols = await moduleOfElement(path.join(env.frameworkDirectory, model.packageName, 'bin'), objectType, objectName, sourceFolder);
                found.set(`${objectType}/${objectName}`.toLowerCase(), { objectType, objectName, packageName: model.packageName, sourceFolder, module: symbols?.module, document: symbols?.document });
            }
        }
        return [...found.values()];
    }

    /** Breakpoints set in the X++ code view are added at the matching line of the debug source file. */
    private mirrorBreakpoints(standard: StandardBreakpointElement[] = []): Promise<void> {
        const pass = this.mirrorQueue.then(() => this.copyBreakpoints(standard));
        this.mirrorQueue = pass.catch(() => undefined);
        return pass;
    }

    private async copyBreakpoints(standard: StandardBreakpointElement[]): Promise<void> {
        const models = (await listModels()).filter(m => m.isCustom);
        const added: vscode.SourceBreakpoint[] = [];
        const removed: vscode.Breakpoint[] = [];
        const wanted = new Set<string>();
        const root = debugSourceRoot().toLowerCase();
        const onDebugSource = (b: vscode.Breakpoint): b is vscode.SourceBreakpoint => b instanceof vscode.SourceBreakpoint
            && b.location.uri.scheme === 'file' && b.location.uri.fsPath.toLowerCase().startsWith(root);
        const keyOf = (b: vscode.SourceBreakpoint) => breakpointKey(b.location.uri.fsPath, b.location.range.start.line + 1);
        // Before copies were remembered, every breakpoint on a debug source was one.
        const remembered = this.state?.get<string[]>(CopiesKey);
        const copies = new Set(remembered ?? vscode.debug.breakpoints.filter(onDebugSource).map(keyOf));
        this.breakpointLinks.clear();
        for (const bp of vscode.debug.breakpoints) {
            if (!(bp instanceof vscode.SourceBreakpoint) || bp.location.uri.scheme !== XppScheme) {
                continue;
            }
            const { objectType, objectName, methodPath } = parseObjectUri(bp.location.uri);
            if (methodPath) {
                continue;
            }
            const std = standard.find(e => e.objectType === objectType && e.objectName.toLowerCase() === objectName.toLowerCase());
            let folder = std?.sourceFolder;
            if (!folder) {
                const owner = await this.server.bridge('read_object', { objectType, objectName });
                folder = models.find(m => m.name.toLowerCase() === String(owner.modelName ?? '').toLowerCase())?.packageName;
            }
            if (!folder) {
                continue;
            }
            const document = await vscode.workspace.openTextDocument(objectUri(objectType, objectName));
            const target = vscode.Uri.file(debugSourceFile(folder, objectType, objectName));
            const debugText = await fs.readFile(target.fsPath, 'utf8').catch(() => undefined);
            const line = (debugText !== undefined ? matchingLine(document.getText(), debugText, bp.location.range.start.line) : undefined)
                ?? documentLineToCompilerLine(document.getText(), bp.location.range.start.line) - 1;
            const copyKey = breakpointKey(target.fsPath, line + 1);
            wanted.add(copyKey);
            this.breakpointLinks.set(breakpointKey(bp.location.uri.toString(), bp.location.range.start.line + 1), copyKey);
            const existing = vscode.debug.breakpoints.filter(onDebugSource).find(b => keyOf(b) === copyKey);
            // Disabling the code view breakpoint or changing its condition changes the copy, which is what stops.
            const same = existing && existing.enabled === bp.enabled && existing.condition === bp.condition
                && existing.hitCondition === bp.hitCondition && existing.logMessage === bp.logMessage;
            if (!same) {
                if (existing) {
                    removed.push(existing);
                }
                added.push(new vscode.SourceBreakpoint(new vscode.Location(target, new vscode.Position(line, 0)), bp.enabled, bp.condition, bp.hitCondition, bp.logMessage));
            }
        }
        // Copies whose code view breakpoint moved or was removed would stop at the wrong place; breakpoints you set on a
        // debug source yourself stay.
        removed.push(...vscode.debug.breakpoints.filter(onDebugSource).filter(b => copies.has(keyOf(b)) && !wanted.has(keyOf(b))));
        await this.state?.update(CopiesKey, [...wanted]);
        if (removed.length > 0) {
            vscode.debug.removeBreakpoints(removed);
        }
        if (added.length > 0) {
            vscode.debug.addBreakpoints(added);
        }
    }
}

/**
 * Runs DebuggerProxy as the debug adapter over stdio, like DebugAdapterExecutable, but keeps its stderr and exit code:
 * when the proxy stops, VS Code otherwise only reports "read error".
 */
class ProxyDebugAdapter implements vscode.DebugAdapter {
    private readonly sent = new vscode.EventEmitter<vscode.DebugProtocolMessage>();
    readonly onDidSendMessage = this.sent.event;
    private readonly process: ChildProcessWithoutNullStreams;
    private buffer = Buffer.alloc(0);
    private stderr = '';
    private disposed = false;
    private disconnecting = false;
    private readonly toDocument: Map<string, string>;
    private readonly toLocal: Map<string, string>;
    /** Ids given to VS Code for code view breakpoints (breakpointKey). */
    private readonly codeViewIds = new Map<string, number>();
    private nextCodeViewId = 1_000_000;
    /** State of the breakpoints on debug source files (breakpointKey), as vsdbg reports it. */
    private readonly copyState = new Map<string, { verified: boolean; message?: string }>();
    private readonly copyRequests = new Map<number, string[]>();
    private readonly copyOfId = new Map<number, string>();

    /**
     * Visual Studio registers xppSource: as a debuggable protocol and sets breakpoints on the symbols' own document names
     * (xppSource://Source/<Model>\AxForm_PurchTable.xpp); a local path mapped with sourceFileMap does not bind there.
     * Breakpoints on our debug source files are sent under that name, and the name is turned back into the file.
     */
    private rewritePaths(value: unknown, map: (p: string) => string | undefined): void {
        if (!value || typeof value !== 'object') {
            return;
        }
        if (Array.isArray(value)) {
            value.forEach(v => this.rewritePaths(v, map));
            return;
        }
        const record = value as Record<string, unknown>;
        if (typeof record.path === 'string') {
            const mapped = map(record.path);
            if (mapped) {
                record.path = mapped;
            }
        }
        for (const v of Object.values(record)) {
            this.rewritePaths(v, map);
        }
    }

    constructor(proxy: string, args: string[], private readonly log: vscode.OutputChannel, documents: Map<string, string> = new Map(),
        private readonly links: Map<string, string> = new Map()) {
        // Local debug source file (lower case) -> the document name in the symbols, and back.
        this.toDocument = documents;
        // vsdbg answers with the document name either as is or under its local cache, as Visual Studio sees it
        // (C:\RemoteDebugAndTest\VSDbg\xppSource:\Source\<Model>\<AxType>_<Name>.xpp; see VSProjectNode.IsDocumentInProject).
        this.toLocal = new Map([...documents].flatMap(([local, doc]) => [
            [doc.toLowerCase(), local],
            [doc.replace(/^xppSource:\/\/Source\//i, 'xppsource:\\source\\').toLowerCase(), local],
        ] as [string, string][]));
        this.process = spawn(proxy, args, { cwd: path.dirname(proxy), windowsHide: true });
        this.process.stdout.on('data', (chunk: Buffer) => this.read(chunk));
        this.process.stderr.on('data', (chunk: Buffer) => {
            const text = chunk.toString('utf8');
            this.stderr = (this.stderr + text).slice(-4000);
            this.log.append(`[DebuggerProxy] ${text}`);
        });
        this.process.stdin.on('error', () => undefined);
        this.process.on('error', error => this.stopped(`DebuggerProxy could not start: ${error.message}`));
        this.process.on('exit', (code, signal) => this.stopped(`DebuggerProxy exited with ${code !== null ? `code ${code}` : `signal ${signal}`}.`));
    }

    private localFor(p: string): string | undefined {
        const lower = p.toLowerCase().replace(/\//g, '\\');
        const at = lower.indexOf('xppsource:');
        return this.toLocal.get(p.toLowerCase()) ?? (at >= 0 ? this.toLocal.get(lower.substring(at)) : undefined);
    }

    private codeViewBreakpoint(key: string, line: number): DapBreakpoint {
        let id = this.codeViewIds.get(key);
        if (id === undefined) {
            id = this.nextCodeViewId++;
            this.codeViewIds.set(key, id);
        }
        const copy = this.links.get(key);
        const state = copy ? this.copyState.get(copy) : undefined;
        return {
            id, line, verified: !!state?.verified,
            message: state?.verified ? undefined : copy ? state?.message ?? 'Waiting for the symbols to load.'
                : 'This element was not prepared for debugging when the session started; restart the debugger.',
        };
    }

    /** A copy's state changed: its code view breakpoint shows the same state. */
    private copyChanged(copy: string, breakpoint: DapBreakpoint): void {
        const verified = !!breakpoint.verified;
        const before = this.copyState.get(copy);
        this.copyState.set(copy, { verified, message: breakpoint.message });
        if (before?.verified !== verified) {
            this.log.appendLine(`Breakpoint ${copy.replace(/^.*\\/, '').replace('|', ':')}: ${verified ? 'bound' : breakpoint.message ?? 'not bound'}`);
        }
        for (const [key, target] of this.links) {
            const id = this.codeViewIds.get(key);
            if (target === copy && id !== undefined) {
                this.sent.fire({
                    type: 'event', seq: 0, event: 'breakpoint',
                    body: { reason: 'changed', breakpoint: this.codeViewBreakpoint(key, Number(key.slice(key.lastIndexOf('|') + 1))) },
                } as vscode.DebugProtocolMessage);
            }
        }
    }

    handleMessage(message: vscode.DebugProtocolMessage): void {
        const command = (message as { command?: string }).command;
        if (command === 'disconnect' || command === 'terminate') {
            this.disconnecting = true;
        }
        // vsdbg rejects initialize with VS Code's debug type ('xpp'); 'clr' (.NET Framework, AXHost) as in VSDbgLaunch.json.
        if (command === 'initialize') {
            const request = message as { arguments?: Record<string, unknown> };
            message = { ...request, arguments: { ...request.arguments, adapterID: 'clr' } } as vscode.DebugProtocolMessage;
        }
        if (command === 'setBreakpoints') {
            const request = message as { seq: number; arguments?: { source?: { path?: string }; breakpoints?: { line: number }[] } };
            const source = request.arguments?.source?.path ?? '';
            const lines = (request.arguments?.breakpoints ?? []).map(b => b.line);
            // The code view (xpp:) is not a document vsdbg knows: its breakpoints are bound through their copies on the
            // debug source files, so they are answered here with the state of their copy.
            if (source.toLowerCase().startsWith(`${XppScheme}:`)) {
                this.sent.fire({
                    type: 'response', seq: 0, request_seq: request.seq, success: true, command: 'setBreakpoints',
                    body: { breakpoints: lines.map(line => this.codeViewBreakpoint(breakpointKey(source, line), line)) },
                } as vscode.DebugProtocolMessage);
                return;
            }
            this.copyRequests.set(request.seq, lines.map(line => breakpointKey(source, line)));
        }
        if (this.toDocument.size && (command === 'setBreakpoints' || command === 'breakpointLocations' || command === 'source')) {
            message = JSON.parse(JSON.stringify(message));
            this.rewritePaths((message as { arguments?: unknown }).arguments, p => this.toDocument.get(p.toLowerCase()));
        }
        if (this.process.stdin.writable) {
            const json = JSON.stringify(message);
            this.process.stdin.write(`Content-Length: ${Buffer.byteLength(json, 'utf8')}\r\n\r\n${json}`, 'utf8');
        }
    }

    dispose(): void {
        this.disposed = true;
        if (this.process.exitCode === null) {
            this.process.kill();
        }
        this.sent.dispose();
    }

    /** Splits the stdout stream into DAP messages (Content-Length header, blank line, JSON body). */
    private read(chunk: Buffer): void {
        this.buffer = Buffer.concat([this.buffer, chunk]);
        for (;;) {
            const headerEnd = this.buffer.indexOf('\r\n\r\n');
            if (headerEnd < 0) {
                return;
            }
            const length = /Content-Length:\s*(\d+)/i.exec(this.buffer.subarray(0, headerEnd).toString('ascii'));
            if (!length) {
                this.buffer = this.buffer.subarray(headerEnd + 4);
                continue;
            }
            const start = headerEnd + 4;
            const end = start + Number(length[1]);
            if (this.buffer.length < end) {
                return;
            }
            const body = this.buffer.subarray(start, end).toString('utf8');
            this.buffer = this.buffer.subarray(end);
            try {
                const parsed = JSON.parse(body);
                if (parsed.type === 'response' && parsed.command === 'setBreakpoints' && this.copyRequests.has(parsed.request_seq)) {
                    const keys = this.copyRequests.get(parsed.request_seq)!;
                    this.copyRequests.delete(parsed.request_seq);
                    (parsed.body?.breakpoints ?? []).forEach((b: DapBreakpoint, i: number) => {
                        if (keys[i]) {
                            if (typeof b.id === 'number') {
                                this.copyOfId.set(b.id, keys[i]);
                            }
                            this.copyChanged(keys[i], b);
                        }
                    });
                }
                if (parsed.type === 'event' && parsed.event === 'breakpoint' && typeof parsed.body?.breakpoint?.id === 'number') {
                    const key = this.copyOfId.get(parsed.body.breakpoint.id);
                    if (key) {
                        this.copyChanged(key, parsed.body.breakpoint);
                    }
                }
                if (this.toLocal.size && /xppSource/i.test(body)) {
                    this.rewritePaths(parsed.body, p => this.localFor(p));
                }
                this.sent.fire(parsed);
            } catch (error) {
                this.log.appendLine(`[DebuggerProxy] Unreadable message: ${(error as Error).message}`);
            }
        }
    }

    private stopped(reason: string): void {
        if (this.disposed) {
            return;
        }
        this.disposed = true;
        if (this.disconnecting) {
            this.sent.fire({ type: 'event', seq: 0, event: 'terminated', body: {} } as vscode.DebugProtocolMessage);
            return;
        }
        const hint = 'Check that the sign-in is current (F&O Studio: Connect to Environment), that the environment allows debugging, and that no firewall or proxy blocks wss://<environment>/___sidecar.';
        // stderr already went to the output channel as it arrived.
        this.log.appendLine(`${reason}\n${hint}`);
        const detail = this.stderr.trim();
        const message = `${reason}${detail ? `\n${detail}` : ''}\n${hint}`;
        this.sent.fire({ type: 'event', seq: 0, event: 'output', body: { category: 'stderr', output: `${message}\n` } } as vscode.DebugProtocolMessage);
        this.sent.fire({ type: 'event', seq: 0, event: 'terminated', body: {} } as vscode.DebugProtocolMessage);
        vscode.window.showErrorMessage(`X++ debugging stopped: ${reason} See the X++ output for details.`);
    }
}

/** The local AOS processes: IIS worker processes (w3wp.exe) and the batch server (Batch.exe). */
function localAosProcesses(): Promise<{ name: string; pid: number }[]> {
    return new Promise(resolve => execFile('tasklist', ['/FO', 'CSV', '/NH'], { windowsHide: true, timeout: 30_000 }, (err, stdout) => {
        if (err) {
            resolve([]);
            return;
        }
        resolve(stdout.split(/\r?\n/).map(line => /^"([^"]+)","(\d+)"/.exec(line)).filter((m): m is RegExpExecArray => !!m)
            .filter(m => /^(w3wp|batch)\.exe$/i.test(m[1])).map(m => ({ name: m[1], pid: Number(m[2]) })));
    }));
}

function freePort(): Promise<number> {
    return new Promise((resolve, reject) => {
        const server = net.createServer();
        server.unref();
        server.on('error', reject);
        server.listen(0, '127.0.0.1', () => {
            const port = (server.address() as net.AddressInfo).port;
            server.close(() => resolve(port));
        });
    });
}

/**
 * The line of the debug source that holds the code view's line: the same non-blank text, at the same occurrence
 * (the code view adds blank lines and its own layout around the same method bodies). Undefined when not found.
 */
export function matchingLine(codeView: string, debugSource: string, codeLine: number): number | undefined {
    const norm = (s: string) => s.trim().replace(/\s+/g, ' ');
    const code = codeView.replace(/\r\n/g, '\n').split('\n').map(norm);
    const debug = debugSource.replace(/\r\n/g, '\n').split('\n').map(norm);
    const wanted = code[codeLine];
    if (!wanted) {
        return undefined;
    }
    let occurrence = 0;
    for (let i = 0; i < codeLine; i++) {
        if (code[i] === wanted) {
            occurrence++;
        }
    }
    for (let i = 0; i < debug.length; i++) {
        if (debug[i] === wanted && occurrence-- === 0) {
            return i;
        }
    }
    return undefined;
}

function debugSourceRoot(): string {
    return path.join(os.tmpdir(), 'XppDebugSource');
}

/** Debug source files are the compiler's view of an element (forms with their data source and control classes), not code to check. */
export function isDebugSourceFile(uri: vscode.Uri): boolean {
    return uri.scheme === 'file' && uri.fsPath.toLowerCase().startsWith(debugSourceRoot().toLowerCase() + path.sep);
}

interface DapBreakpoint {
    id?: number;
    verified?: boolean;
    line?: number;
    message?: string;
}

/** A breakpoint's identity in the code view and on the debug source files: the source (path or URI) and its 1-based line. */
function breakpointKey(source: string, line: number): string {
    let decoded = source;
    try {
        decoded = decodeURIComponent(source);
    } catch {
        // Not URI-encoded.
    }
    return `${decoded.toLowerCase()}|${line}`;
}

/** folder: the package for your own packages; for Microsoft's packages the model folder their symbols record (e.g. Foundation). */
function debugSourceFile(folder: string, objectType: string, objectName: string): string {
    return path.join(debugSourceRoot(), folder.toLowerCase(), `${objectType.toLowerCase()}_${objectName.toLowerCase()}.xpp`);
}

/** A standard element with a breakpoint: its package is loaded with symbols, its source written where its symbols point. */
interface StandardBreakpointElement {
    objectType: string;
    objectName: string;
    packageName: string;
    sourceFolder: string;
    /** The package's module (e.g. Dynamics.AX.ApplicationSuite.Forms.16) whose symbols hold the element. */
    module?: string;
    /** The element's document name in those symbols, e.g. xppSource://Source/Foundation\AxForm_PurchTable.xpp. */
    document?: string;
}

const moduleCache = new Map<string, { module: string; document: string } | undefined>();

/**
 * A standard package is split into hundreds of modules; loading the symbols of all of them takes minutes. The one whose
 * symbols name the element's source file (<AxType>_<Name>.xpp) is enough for its breakpoints.
 */
async function moduleOfElement(binFolder: string, objectType: string, objectName: string, sourceFolder: string): Promise<{ module: string; document: string } | undefined> {
    const key = `${binFolder}|${objectType}|${objectName}`.toLowerCase();
    if (moduleCache.has(key)) {
        return moduleCache.get(key);
    }
    const file = `${objectType}_${objectName}.xpp`;
    const full = `${sourceFolder}\\${file}`;
    let found: { module: string; document: string } | undefined;
    for (const pdb of (await fs.readdir(binFolder).catch(() => [] as string[])).filter(f => f.toLowerCase().endsWith('.pdb'))) {
        const text = (await fs.readFile(path.join(binFolder, pdb)).catch(() => Buffer.alloc(0))).toString('latin1');
        // Portable PDBs store the name in parts, each after a length byte that can look like a letter: match the whole
        // "<model folder>\<AxType>_<Name>.xpp" (else the file name after a separator) and take its exact case from the PDB.
        const name = exactName(text, full) ?? exactName(text, file, '\\');
        if (name) {
            found = { module: pdb.slice(0, -4), document: `xppSource://Source/${name.includes('\\') ? name : `${sourceFolder}\\${name}`}` };
            break;
        }
    }
    moduleCache.set(key, found);
    return found;
}

/** `name` as written in `text` (case-insensitive match); when `after` is given, only where it follows that character. */
function exactName(text: string, name: string, after?: string): string | undefined {
    const lower = text.toLowerCase();
    const wanted = name.toLowerCase();
    for (let at = lower.indexOf(wanted); at >= 0; at = lower.indexOf(wanted, at + 1)) {
        if (after === undefined || (at > 0 && text[at - 1] === after)) {
            return text.substring(at, at + name.length);
        }
    }
    return undefined;
}

async function findDebuggerProxy(applicationVersion?: string): Promise<string> {
    const configured = vscode.workspace.getConfiguration('xpp').get<string>('debuggerProxyPath');
    const candidates: string[] = configured ? [configured] : [];
    const env = await readActiveEnvironment().catch(() => undefined);
    const dynamics365 = dynamics365Folder();
    for (const version of [applicationVersion, env ? path.basename(path.dirname(env.frameworkDirectory)) : undefined]) {
        if (version) {
            candidates.push(path.join(dynamics365, version, 'FinOpsTools', 'DebuggerProxy', 'Microsoft.Dynamics.AX.DebuggerProxy.exe'));
        }
    }
    const found = candidates.find(c => existsSync(c));
    if (!found) {
        throw new Error('Microsoft.Dynamics.AX.DebuggerProxy.exe was not found. Run F&O Studio: Connect to Environment, or set xpp.debuggerProxyPath.');
    }
    return found;
}

export function parseInfolog(output: string): { kind: string; message: string; time: Date }[] {
    const entries: InfologEntry[] = [];
    for (const match of output.matchAll(/<InfologMessage xmlns[\s\S]*?<\/InfologMessage>/g)) {
        const xml = match[0];
        const kind = /<Kind>([^<]*)<\/Kind>/.exec(xml)?.[1] ?? 'Info';
        const message = /<Message>([\s\S]*?)<\/Message>/.exec(xml)?.[1] ?? '';
        entries.push({ kind, message: decodeXml(message), time: new Date() });
    }
    return entries;
}

function decodeXml(text: string): string {
    return text.replace(/&lt;/g, '<').replace(/&gt;/g, '>').replace(/&quot;/g, '"').replace(/&apos;/g, '\'').replace(/&amp;/g, '&');
}

/** The Infolog window: messages raised by the code being debugged, filterable by severity and text. */
export class InfologProvider implements vscode.TreeDataProvider<InfologEntry> {
    private readonly changed = new vscode.EventEmitter<void>();
    readonly onDidChangeTreeData = this.changed.event;
    private readonly entries: InfologEntry[] = [];
    private kinds = new Set(['Error', 'Warning', 'Info']);
    private text = '';
    view: vscode.TreeView<InfologEntry> | undefined;

    add(entry: InfologEntry): void {
        const first = this.entries.length === 0;
        this.entries.unshift(entry);
        this.changed.fire();
        if (first && this.view && !this.view.visible) {
            this.view.reveal(entry, { focus: false, select: false }).then(undefined, () => undefined);
        }
    }

    clear(): void {
        this.entries.length = 0;
        this.changed.fire();
    }

    async pickFilter(): Promise<void> {
        const picked = await vscode.window.showQuickPick(['Error', 'Warning', 'Info'].map(k => ({ label: k, picked: this.kinds.has(k) })),
            { title: 'Infolog: show', canPickMany: true });
        if (!picked) {
            return;
        }
        const text = await vscode.window.showInputBox({ title: 'Infolog: text filter', prompt: 'Only messages containing this text (empty: all)', value: this.text });
        if (text === undefined) {
            return;
        }
        this.kinds = new Set(picked.map(p => p.label));
        this.text = text.trim().toLowerCase();
        this.changed.fire();
    }

    async copy(): Promise<void> {
        await vscode.env.clipboard.writeText(this.visible().map(e => `${e.time.toLocaleTimeString()} ${e.kind}: ${e.message}`).join('\n'));
        vscode.window.setStatusBarMessage('Infolog copied', 3000);
    }

    private visible(): InfologEntry[] {
        const kind = (k: string) => k === 'Error' || k === 'Warning' ? k : 'Info';
        return this.entries.filter(e => this.kinds.has(kind(e.kind)) && (!this.text || e.message.toLowerCase().includes(this.text)));
    }

    getTreeItem(entry: InfologEntry): vscode.TreeItem {
        const item = new vscode.TreeItem(entry.message, vscode.TreeItemCollapsibleState.None);
        item.description = entry.time.toLocaleTimeString();
        item.tooltip = `${entry.kind}: ${entry.message}`;
        item.iconPath = new vscode.ThemeIcon(entry.kind === 'Error' ? 'error' : entry.kind === 'Warning' ? 'warning' : 'info');
        item.contextValue = 'xppInfologEntry';
        return item;
    }

    getChildren(): InfologEntry[] {
        return this.visible();
    }

    getParent(): undefined {
        return undefined;
    }
}
