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
const PdbSourceRoots = ['e:\\source\\', 'e:\\\\source\\', 'E:\\source\\'];

interface InfologEntry {
    kind: string;
    message: string;
    time: Date;
}

interface PendingAdapter {
    proxy: string;
    host: string;
    token: string;
}

/**
 * Debugging a unified developer environment, the way Visual Studio does it: the FinOps tools' DebuggerProxy
 * (--VSVersion vsdbg) is the debug adapter, tunnelling to vsdbg next to AXHost.exe in the cloud.
 */
export class XppDebugger implements vscode.DebugConfigurationProvider, vscode.DebugAdapterDescriptorFactory {
    private pending: PendingAdapter | undefined;
    readonly infolog = new InfologProvider();

    constructor(private readonly server: XppServerClient, private readonly log: vscode.OutputChannel, private readonly deployState: DeployState) { }

    register(context: vscode.ExtensionContext): void {
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
            const packages = await this.writeDebugSources();
            await this.mirrorBreakpoints();

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
                justMyCode: config.justMyCode ?? true,
                sourceFileMap: { ...sourceFileMap, ...(config.sourceFileMap ?? {}) },
                symbolOptions: config.symbolOptions ?? {
                    moduleFilter: {
                        mode: 'loadOnlyIncluded',
                        includedModules: packages.flatMap(p => [`Dynamics.AX.${p}.dll`, `Dynamics.AX.${p}.*`]),
                        includeSymbolsNextToModules: false,
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
        const packages = await vscode.window.withProgress({ location: vscode.ProgressLocation.Notification, title: 'Preparing X++ debugging: writing debug sources…' },
            () => this.writeDebugSources());
        await this.mirrorBreakpoints();
        const sourceFileMap: Record<string, string> = {};
        for (const root of PdbSourceRoots) {
            sourceFileMap[root] = debugSourceRoot() + path.sep;
        }
        await vscode.debug.startDebugging(undefined, {
            type: 'clr',
            request: 'attach',
            name: `Attach to the local AOS (${picked.name} ${picked.pid})`,
            processId: picked.pid,
            justMyCode: config.justMyCode ?? true,
            requireExactSource: false,
            sourceFileMap: { ...sourceFileMap, ...(config.sourceFileMap ?? {}) },
            symbolOptions: {
                searchPaths: [...new Set(models.map(m => path.join(path.dirname(m.folder), 'bin')))],
                searchMicrosoftSymbolServer: false,
                moduleFilter: {
                    mode: 'loadOnlyIncluded',
                    includedModules: packages.flatMap(p => [`Dynamics.AX.${p}.dll`, `Dynamics.AX.${p}.*`]),
                    includeSymbolsNextToModules: true,
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
        ], this.log));
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
    private async writeDebugSources(): Promise<string[]> {
        const root = debugSourceRoot();
        const models = (await listModels()).filter(m => m.isCustom);
        const packages = [...new Set(models.map(m => m.packageName))];
        for (const model of models) {
            const dir = path.join(root, model.packageName.toLowerCase());
            await fs.mkdir(dir, { recursive: true });
            for (const objectType of DebugSourceTypes) {
                const files = await fs.readdir(path.join(model.folder, objectType)).catch(() => [] as string[]);
                for (const file of files.filter(f => f.toLowerCase().endsWith('.xml'))) {
                    const objectName = file.slice(0, -4);
                    const result = await this.server.bridge('read_object', { objectType, objectName }).catch(() => undefined);
                    if (result?.success) {
                        await fs.writeFile(debugSourceFile(model.packageName, objectType, objectName), compilerView(result.declaration ?? '', result.methods ?? []), 'utf8');
                    }
                }
            }
        }
        return packages;
    }

    /** Breakpoints set in the X++ code view are added at the matching line of the debug source file. */
    private async mirrorBreakpoints(): Promise<void> {
        const models = (await listModels()).filter(m => m.isCustom);
        const added: vscode.SourceBreakpoint[] = [];
        for (const bp of vscode.debug.breakpoints) {
            if (!(bp instanceof vscode.SourceBreakpoint) || bp.location.uri.scheme !== XppScheme) {
                continue;
            }
            const { objectType, objectName, methodPath } = parseObjectUri(bp.location.uri);
            if (methodPath) {
                continue;
            }
            const owner = await this.server.bridge('read_object', { objectType, objectName });
            const pkg = models.find(m => m.name.toLowerCase() === String(owner.modelName ?? '').toLowerCase())?.packageName;
            if (!pkg) {
                continue;
            }
            const document = await vscode.workspace.openTextDocument(objectUri(objectType, objectName));
            const line = documentLineToCompilerLine(document.getText(), bp.location.range.start.line) - 1;
            const target = vscode.Uri.file(debugSourceFile(pkg, objectType, objectName));
            const exists = vscode.debug.breakpoints.some(b => b instanceof vscode.SourceBreakpoint
                && b.location.uri.fsPath.toLowerCase() === target.fsPath.toLowerCase() && b.location.range.start.line === line);
            if (!exists) {
                added.push(new vscode.SourceBreakpoint(new vscode.Location(target, new vscode.Position(line, 0)), bp.enabled, bp.condition, bp.hitCondition, bp.logMessage));
            }
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

    constructor(proxy: string, args: string[], private readonly log: vscode.OutputChannel) {
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
                this.sent.fire(JSON.parse(body));
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

function debugSourceRoot(): string {
    return path.join(os.tmpdir(), 'XppDebugSource');
}

function debugSourceFile(packageName: string, objectType: string, objectName: string): string {
    return path.join(debugSourceRoot(), packageName.toLowerCase(), `${objectType.toLowerCase()}_${objectName.toLowerCase()}.xpp`);
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
