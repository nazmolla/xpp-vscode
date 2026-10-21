import { ChildProcessWithoutNullStreams, spawn } from 'child_process';
import * as vscode from 'vscode';
import { readActiveEnvironment } from './environment';

interface Pending {
    resolve: (value: any) => void;
    reject: (reason: Error) => void;
    timer: NodeJS.Timeout;
}

const RequestTimeoutMs = 180_000;

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

    constructor(
        private readonly context: vscode.ExtensionContext,
        private readonly log: vscode.OutputChannel,
        private readonly projectFile: () => Promise<string | undefined>,
    ) { }

    /** Calls a metadata bridge action and returns its JSON result. */
    async bridge<T = any>(action: string, body: object = {}): Promise<T> {
        await this.ensureStarted();
        const raw = await this.request('xpp/bridge', { action, body });
        const result = typeof raw === 'string' ? JSON.parse(raw) : raw;
        return result as T;
    }

    /** Calls an MCP tool and returns its text output. */
    async tool(name: string, args: object): Promise<{ text: string; isError: boolean }> {
        await this.ensureStarted();
        const result = await this.request('tools/call', { name, arguments: args });
        return { text: result?.content?.[0]?.text ?? '', isError: !!result?.isError };
    }

    /** Restarts the server so it picks up a changed configuration or project. */
    restart(): void {
        this.stop();
    }

    dispose(): void {
        this.stop();
    }

    private stop(): void {
        this.process?.kill();
        this.process = undefined;
        this.starting = undefined;
        for (const [, p] of this.pending) {
            clearTimeout(p.timer);
            p.reject(new Error('X++ server stopped.'));
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
        const env = await readActiveEnvironment();
        const args = ['--config', env.configFile];
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
            }
        });

        await this.request('initialize', { protocolVersion: '2025-06-18', clientInfo: { name: 'xpp-vscode', version: '0' } });
    }

    private request(method: string, params: object): Promise<any> {
        const child = this.process;
        if (!child) {
            return Promise.reject(new Error('X++ server is not running.'));
        }
        const id = this.nextId++;
        return new Promise((resolve, reject) => {
            const timer = setTimeout(() => {
                this.pending.delete(id);
                reject(new Error(`X++ server did not answer ${method} within ${RequestTimeoutMs / 1000}s.`));
            }, RequestTimeoutMs);
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
            let message: any;
            try {
                message = JSON.parse(line);
            } catch {
                this.log.appendLine(`Unreadable server output: ${line.slice(0, 200)}`);
                continue;
            }
            const pending = this.pending.get(message.id);
            if (!pending) {
                continue;
            }
            this.pending.delete(message.id);
            clearTimeout(pending.timer);
            if (message.error) {
                pending.reject(new Error(message.error.message));
            } else {
                pending.resolve(message.result);
            }
        }
    }
}
