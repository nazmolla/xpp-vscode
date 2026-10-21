import { spawn } from 'child_process';
import { existsSync, promises as fs } from 'fs';
import * as path from 'path';
import * as vscode from 'vscode';
import { objectUri } from './codeView';
import { CompilerDiagnostic, diagnosticTarget, parseDiagnostics } from './diagnostics';
import { readActiveEnvironment, XppEnvironment } from './environment';
import { readProject } from './project';
import { compilerLineToDocumentLine } from './xppSource';

/** Builds a model the way Visual Studio does: labels with LabelC, then X++ with xppc. */
function inScope(d: CompilerDiagnostic, scope: Set<string>): boolean {
    const target = diagnosticTarget(d.path);
    return !target || scope.has(`${target.objectType}/${target.objectName}`.toLowerCase());
}

export class XppBuilder {
    private readonly diagnostics = vscode.languages.createDiagnosticCollection('xpp');

    constructor(private readonly output: vscode.OutputChannel) { }

    dispose(): void {
        this.diagnostics.dispose();
    }

    /** projectItems limits the reported diagnostics to those objects; xppc always compiles the whole package. */
    async build(modelName: string | undefined, token?: vscode.CancellationToken, projectItems?: { objectType: string; objectName: string }[]): Promise<boolean> {
        const env = await readActiveEnvironment();
        const model = modelName ?? await this.pickModel(env);
        if (!model) {
            return false;
        }
        const packageFolder = path.join(env.modelStoreFolder, model);
        if (!existsSync(packageFolder)) {
            throw new Error(`Model '${model}' is not in your custom metadata folder (${env.modelStoreFolder}). Only your own models can be built.`);
        }

        const logDir = path.join(packageFolder, 'BuildLogs');
        await fs.mkdir(logDir, { recursive: true });
        this.output.show(true);
        this.output.appendLine(`=== Build ${model} (${new Date().toLocaleTimeString()}) ===`);

        const bin = env.binDirectory;
        const labelFiles = await this.labelFileIds(packageFolder, model);
        if (labelFiles.length > 0) {
            const ok = await this.run(path.join(bin, 'LabelC.exe'), [
                `-metadata=${env.modelStoreFolder}`,
                `-output=${path.join(packageFolder, 'Resources')}`,
                `-modelmodule=${model}`,
                `-names=${labelFiles.join(',')}`,
            ], token);
            if (!ok) {
                this.output.appendLine('Label compilation failed.');
                return false;
            }
        }

        const xmlLog = path.join(logDir, 'Dynamics.AX.' + model + '.xppc.xml');
        const args = [
            `-metadata=${env.modelStoreFolder}`,
            `-compilermetadata=${env.runtimePackagesDirectory ?? env.frameworkDirectory}`,
            `-modelmodule=${model}`,
            `-output=${path.join(packageFolder, 'bin')}`,
            `-xmllog=${xmlLog}`,
            `-log=${path.join(logDir, 'Dynamics.AX.' + model + '.xppc.log')}`,
            `-appBase=${bin}`,
            `-refPath=${bin}`,
            ...env.referencePackagesPaths.map(p => `-referenceFolder=${p}`),
            `-referenceFolder=${env.modelStoreFolder}`,
        ];
        const config = vscode.workspace.getConfiguration('xpp');
        if (config.get<boolean>('build.crossReference', true) && env.crossReferencesDbServerName && env.crossReferencesDatabaseName) {
            args.push('-xref', `-xrefSqlServer=${env.crossReferencesDbServerName}`, `-xrefDbName=${env.crossReferencesDatabaseName}`);
        }
        if (config.get<boolean>('build.runAppCheckerRules', false)) {
            args.push('-RunAppcheckerRules');
        }

        await fs.rm(xmlLog, { force: true });
        const assembly = path.join(packageFolder, 'bin', `Dynamics.AX.${model}.dll`);
        const assemblyBefore = existsSync(assembly) ? (await fs.stat(assembly)).mtimeMs : 0;
        const compiled = await this.run(path.join(bin, 'xppc.exe'), args, token);
        const scope = projectItems ? new Set(projectItems.map(i => `${i.objectType}/${i.objectName}`.toLowerCase())) : undefined;
        const all = existsSync(xmlLog) ? await this.publish(xmlLog, scope) : [];
        const found = all.filter(d => !scope || inScope(d, scope));
        // Fatal (compiler crash) is as blocking as Error; a crash may also write no diagnostics at all.
        const isBlocking = (d: CompilerDiagnostic) => ['error', 'fatal'].includes(d.severity.toLowerCase());
        const errors = found.filter(isBlocking).length;
        const assemblyWritten = existsSync(assembly) && (await fs.stat(assembly)).mtimeMs > assemblyBefore;
        const ok = compiled && !all.some(isBlocking) && assemblyWritten;
        const reason = !compiled ? ' (xppc exited with an error)' : !assemblyWritten && errors === 0 ? ' (no assembly was produced)' : '';
        this.output.appendLine(`=== ${model}${scope ? ' (project items)' : ''}: ${ok ? 'BUILD SUCCEEDED' : 'BUILD FAILED' + reason} - ${errors} error(s), ${found.length - errors} warning(s) ===`);
        return ok;
    }

    private async pickModel(env: XppEnvironment): Promise<string | undefined> {
        const project = await readProject();
        if (project?.model) {
            return project.model;
        }
        const entries = await fs.readdir(env.modelStoreFolder, { withFileTypes: true });
        const models = entries.filter(e => e.isDirectory()).map(e => e.name);
        return vscode.window.showQuickPick(models, { title: 'Model to build' });
    }

    private async labelFileIds(packageFolder: string, model: string): Promise<string[]> {
        const folder = path.join(packageFolder, model, 'AxLabelFile');
        if (!existsSync(folder)) {
            return [];
        }
        const files = await fs.readdir(folder);
        // Descriptors are <Id>_<lang>.xml; LabelC wants the ids.
        return [...new Set(files.filter(f => f.endsWith('.xml')).map(f => f.replace(/_[^_]+\.xml$/, '')))];
    }

    private run(exe: string, args: string[], token?: vscode.CancellationToken): Promise<boolean> {
        this.output.appendLine(`> ${path.basename(exe)} ${args.join(' ')}`);
        return new Promise(resolve => {
            const child = spawn(exe, args, { cwd: path.dirname(exe), windowsHide: true });
            const cancel = token?.onCancellationRequested(() => child.kill());
            child.stdout.on('data', d => this.output.append(d.toString()));
            child.stderr.on('data', d => this.output.append(d.toString()));
            child.on('error', err => {
                this.output.appendLine(`Could not start ${exe}: ${err.message}`);
                cancel?.dispose();
                resolve(false);
            });
            child.on('exit', code => {
                cancel?.dispose();
                resolve(code === 0);
            });
        });
    }

    /** Shows compiler diagnostics in the Problems view, on the object's code view where possible. */
    private async publish(xmlLog: string, scope?: Set<string>): Promise<CompilerDiagnostic[]> {
        const xml = await fs.readFile(xmlLog, 'utf8');
        const found = parseDiagnostics(xml);
        const byUri = new Map<string, { uri: vscode.Uri; items: vscode.Diagnostic[] }>();

        for (const d of found) {
            const target = diagnosticTarget(d.path);
            if (!target || (scope && !inScope(d, scope) && d.severity.toLowerCase() !== 'fatal')) {
                continue;
            }
            const uri = objectUri(target.objectType, target.objectName);
            let line = Math.max(0, (d.line ?? 1) - 1);
            if (target.isCode && d.line !== undefined) {
                line = await this.documentLine(uri, d.line);
            }
            const endLine = d.endLine !== undefined ? line + (d.endLine - (d.line ?? d.endLine)) : line;
            const range = new vscode.Range(line, Math.max(0, (d.column ?? 1) - 1), endLine, Math.max(0, (d.endColumn ?? 1000) - 1));
            const severity = ['error', 'fatal'].includes(d.severity.toLowerCase()) ? vscode.DiagnosticSeverity.Error
                : d.severity.toLowerCase() === 'warning' ? vscode.DiagnosticSeverity.Warning
                    : vscode.DiagnosticSeverity.Information;
            const diagnostic = new vscode.Diagnostic(range, d.line === undefined ? `${d.message} (${d.path})` : d.message, severity);
            diagnostic.code = d.moniker;
            diagnostic.source = d.type;
            const entry = byUri.get(uri.toString()) ?? { uri, items: [] };
            entry.items.push(diagnostic);
            byUri.set(uri.toString(), entry);
        }

        this.diagnostics.clear();
        for (const { uri, items } of byUri.values()) {
            this.diagnostics.set(uri, items);
        }
        for (const d of found) {
            this.output.appendLine(`${d.severity}: ${d.path}${d.line ? `(${d.line},${d.column})` : ''}: ${d.message}`);
        }
        return found;
    }

    private async documentLine(uri: vscode.Uri, compilerLine: number): Promise<number> {
        try {
            const text = Buffer.from(await vscode.workspace.fs.readFile(uri)).toString('utf8');
            return compilerLineToDocumentLine(text, compilerLine);
        } catch {
            return Math.max(0, compilerLine - 1);
        }
    }
}
