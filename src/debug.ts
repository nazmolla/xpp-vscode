import { existsSync, promises as fs } from 'fs';
import * as os from 'os';
import * as path from 'path';
import * as vscode from 'vscode';
import { listModels } from './aot';
import { objectUri, parseObjectUri, XppScheme } from './codeView';
import { dynamics365Folder, readActiveEnvironment, selectedEnvironmentUrl } from './environment';
import { XppServerClient } from './serverClient';
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

    constructor(private readonly server: XppServerClient, private readonly log: vscode.OutputChannel) { }

    register(context: vscode.ExtensionContext): void {
        context.subscriptions.push(
            vscode.debug.registerDebugConfigurationProvider(DebugType, this),
            vscode.debug.registerDebugAdapterDescriptorFactory(DebugType, this),
            vscode.debug.registerDebugAdapterTrackerFactory(DebugType, { createDebugAdapterTracker: () => this.tracker() }),
            vscode.window.registerTreeDataProvider('xpp.infolog', this.infolog),
            vscode.commands.registerCommand('xpp.debug.start', () => vscode.debug.startDebugging(undefined, {
                type: DebugType, request: 'attach', name: 'Attach to Dynamics 365 (unified developer environment)',
            })),
            vscode.commands.registerCommand('xpp.infolog.clear', () => this.infolog.clear()),
        );
    }

    provideDebugConfigurations(): vscode.DebugConfiguration[] {
        return [{ type: DebugType, request: 'attach', name: 'Attach to Dynamics 365 (unified developer environment)' }];
    }

    async resolveDebugConfiguration(_folder: vscode.WorkspaceFolder | undefined, config: vscode.DebugConfiguration): Promise<vscode.DebugConfiguration | undefined> {
        if (!config.type) {
            config = this.provideDebugConfigurations()[0];
        }
        return vscode.window.withProgress({ location: vscode.ProgressLocation.Notification, title: 'Preparing X++ debugging…' }, async progress => {
            progress.report({ message: 'signing in' });
            const dataverseSetting = selectedEnvironmentUrl();
            if (!dataverseSetting) {
                vscode.window.showErrorMessage('Select the environment first: X++: Select Environment.');
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

    createDebugAdapterDescriptor(): vscode.DebugAdapterDescriptor {
        const pending = this.pending;
        this.pending = undefined;
        if (!pending) {
            throw new Error('Start X++ debugging from the Run and Debug view or X++: Start Debugging.');
        }
        // The same arguments Visual Studio passes in its VSDbgLaunch.json.
        return new vscode.DebugAdapterExecutable(pending.proxy, [
            '--remoteProxyHost', pending.host, '--VSVersion', 'vsdbg', '--accessToken', pending.token,
        ]);
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
        throw new Error('Microsoft.Dynamics.AX.DebuggerProxy.exe was not found. Run X++: Connect to Environment, or set xpp.debuggerProxyPath.');
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

/** The Infolog window: messages raised by the code being debugged. */
export class InfologProvider implements vscode.TreeDataProvider<InfologEntry> {
    private readonly changed = new vscode.EventEmitter<void>();
    readonly onDidChangeTreeData = this.changed.event;
    private readonly entries: InfologEntry[] = [];

    add(entry: InfologEntry): void {
        this.entries.unshift(entry);
        this.changed.fire();
    }

    clear(): void {
        this.entries.length = 0;
        this.changed.fire();
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
        return this.entries;
    }
}
