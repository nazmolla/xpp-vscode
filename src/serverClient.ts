import { ChildProcessWithoutNullStreams, spawn } from 'child_process';
import * as vscode from 'vscode';
import { BridgeAction, BridgeResult } from './bridgeContract';
import { readActiveEnvironment } from './environment';

interface Pending {
    resolve: (value: unknown) => void;
    reject: (reason: Error) => void;
    timer: NodeJS.Timeout;
}

const RequestTimeoutMs = 180_000;

export type ServerState = { kind: 'starting' } | { kind: 'ready' } | { kind: 'failed'; message: string };

/**
 * The extension's own connection to XppVsCode.Server (separate from the MCP server Copilot uses),
 * for the code view, designer and explorer. Speaks newline-delimited JSON-RPC.
 */
export class XppServerClient implements vscode.Disposable {
    private process: ChildProcessWithoutNullStreams | undefined;
    private starting: Promise<void> | undefined;
    private nextId = 1;
    private buffer = '';
    private readonly pending = new Map<number, Pending>();
    private readonly stateChanged = new vscode.EventEmitter<ServerState>();
    /** Starting, ready, or stopped unexpectedly (a restart the extension asked for is not a failure). */
    readonly onDidChangeState = this.stateChanged.event;

    constructor(
        private readonly context: vscode.ExtensionContext,
        private readonly log: vscode.OutputChannel,
        private readonly projectFile: () => Promise<string | undefined>,
    ) { }

    /** Calls a metadata bridge action and returns its JSON result. */
    async bridge<T = BridgeResult>(action: BridgeAction, body: object = {}): Promise<T> {
        await this.ensureStarted();
        const raw = await this.request('xpp/bridge', { action, body });
        const result = typeof raw === 'string' ? JSON.parse(raw) : raw;
        return result as T;
    }

    /** Calls a language-service action (xpp/lang) and returns its JSON result. */
    async lang<T = BridgeResult>(action: string, body: object = {}): Promise<T> {
        await this.ensureStarted();
        const raw = await this.request('xpp/lang', { action, body });
        return (typeof raw === 'string' ? JSON.parse(raw) : raw) as T;
    }

    /** Calls a business data action (xpp/data); the body carries the database connection. */
    async data<T = BridgeResult>(action: string, body: object, timeoutMs = RequestTimeoutMs): Promise<T> {
        await this.ensureStarted();
        const raw = await this.request('xpp/data', { action, body }, timeoutMs);
        return (typeof raw === 'string' ? JSON.parse(raw) : raw) as T;
    }

    /** Analyzes a performance trace (.etl) on the server (xpp/trace); outside the metadata lock. */
    async trace<T = BridgeResult>(body: { path: string; minRequestMs?: number; minNodeMs?: number }, timeoutMs = 600_000): Promise<T> {
        await this.ensureStarted();
        const raw = await this.request('xpp/trace', { action: 'analyze', body }, timeoutMs);
        return (typeof raw === 'string' ? JSON.parse(raw) : raw) as T;
    }

    /** Calls an MCP tool and returns its text output. */
    async tool(name: string, args: object): Promise<{ text: string; isError: boolean }> {
        await this.ensureStarted();
        const result = await this.request('tools/call', { name, arguments: args }) as { content?: { text?: string }[]; isError?: boolean } | undefined;
        return { text: result?.content?.[0]?.text ?? '', isError: !!result?.isError };
    }

    /** Restarts the server so it picks up a changed configuration or project. */
    restart(): void {
        this.stop();
    }

    dispose(): void {
        this.stop();
        this.stateChanged.dispose();
    }

    private stop(): void {
        this.process?.kill();
        this.process = undefined;
        this.starting = undefined;
        for (const [, p] of this.pending) {
            clearTimeout(p.timer);
            p.reject(new Error('The X++ server stopped. Run "F&O Studio: Restart X++ Server" and see the X++ output for the cause.'));
        }
        this.pending.clear();
    }

    private ensureStarted(): Promise<void> {
        if (this.process && !this.process.killed) {
            return Promise.resolve();
        }
        return this.starting ??= this.start().finally(() => { this.starting = undefined; });
    }

    private async start(): Promise<void> {
        this.stateChanged.fire({ kind: 'starting' });
        try {
            await this.launch();
            this.stateChanged.fire({ kind: 'ready' });
        } catch (err) {
            this.stateChanged.fire({ kind: 'failed', message: (err as Error).message });
            throw err;
        }
    }

    private async launch(): Promise<void> {
        const env = await readActiveEnvironment();
        const args = ['--config', env.configFile, '--allow-data'];
        const project = await this.projectFile();
        if (project) {
            args.push('--project', project);
        }
        const exe = this.context.asAbsolutePath('server/bin/XppVsCode.Server.exe');
        const child = spawn(exe, args, { windowsHide: true });
        this.process = child;
        this.buffer = '';

        child.stdout.setEncoding('utf8');
        child.stdout.on('data', (chunk: string) => this.onData(chunk));
        child.stderr.on('data', (chunk: Buffer) => this.log.append(chunk.toString()));
        child.on('exit', code => {
            this.log.appendLine(`X++ server exited (${code}).`);
            if (this.process === child) {
                this.stop();
                this.stateChanged.fire({ kind: 'failed', message: `The X++ server stopped (exit code ${code}).` });
            }
        });
        child.on('error', err => {
            this.log.appendLine(`X++ server could not run: ${err.message}`);
            if (this.process === child) {
                this.stop();
                this.stateChanged.fire({ kind: 'failed', message: `The X++ server could not run: ${err.message}` });
            }
        });

        await this.request('initialize', { protocolVersion: '2025-06-18', clientInfo: { name: 'xpp-vscode', version: '0' } });
    }

    private request(method: string, params: object, timeoutMs = RequestTimeoutMs): Promise<unknown> {
        const child = this.process;
        if (!child) {
            return Promise.reject(new Error('The X++ server is not running. Check the environment with "F&O Studio: Check Environment Connection", then restart the server.'));
        }
        const id = this.nextId++;
        return new Promise((resolve, reject) => {
            const timer = setTimeout(() => {
                this.pending.delete(id);
                reject(new Error(`X++ server did not answer ${method} within ${timeoutMs / 1000}s.`));
            }, timeoutMs);
            this.pending.set(id, { resolve, reject, timer });
            child.stdin.write(JSON.stringify({ jsonrpc: '2.0', id, method, params }) + '\n');
        });
    }

    private onData(chunk: string): void {
        this.buffer += chunk;
        let newline: number;
        while ((newline = this.buffer.indexOf('\n')) >= 0) {
            const line = this.buffer.slice(0, newline).trim();
            this.buffer = this.buffer.slice(newline + 1);
            if (!line) {
                continue;
            }
            let message: { id?: number; result?: unknown; error?: { message: string } };
            try {
                message = JSON.parse(line);
            } catch {
                this.log.appendLine(`Unreadable server output: ${line.slice(0, 200)}`);
                continue;
            }
            const pending = message.id === undefined ? undefined : this.pending.get(message.id);
            if (!pending) {
                continue;
            }
            this.pending.delete(message.id!);
            clearTimeout(pending.timer);
            if (message.error) {
                pending.reject(new Error(message.error.message));
            } else {
                pending.resolve(message.result);
            }
        }
    }
}
