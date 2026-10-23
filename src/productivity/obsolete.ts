import * as vscode from 'vscode';
import { blankStringsAndComments } from '../codeIntel/providers';
import { affects, enabled } from '../codeIntel/settings';
import { currentObject, declaredType } from '../navigation';
import { ProductivityContext } from './common';

interface ObsoleteItem {
    path: string;
    objectType: string;
    objectName: string;
    member?: string;
    message: string;
    isError: boolean;
}

interface ObsoleteType {
    /** Set when the whole class or table is obsolete. */
    whole?: ObsoleteItem;
    members: Map<string, ObsoleteItem>;
}

const Key = 'obsoleteWarnings';

/**
 * Obsolete API warnings: calls to methods (Type::method(, variable.method() and uses of classes and tables that are
 * marked [SysObsolete], from the language service's list (loaded once, reloaded with Rebuild Extension Index).
 */
export class ObsoleteWarnings implements vscode.Disposable {
    private readonly diagnostics = vscode.languages.createDiagnosticCollection('xpp-obsolete');
    private readonly timers = new Map<string, NodeJS.Timeout>();
    private readonly disposables: vscode.Disposable[] = [];
    private types: Map<string, ObsoleteType> | undefined;
    private loading: Promise<Map<string, ObsoleteType>> | undefined;

    constructor(private readonly ctx: ProductivityContext) {
        this.disposables.push(
            this.diagnostics,
            vscode.workspace.onDidOpenTextDocument(d => this.schedule(d)),
            vscode.workspace.onDidChangeTextDocument(e => this.schedule(e.document)),
            vscode.workspace.onDidCloseTextDocument(d => {
                clearTimeout(this.timers.get(d.uri.toString()));
                this.diagnostics.delete(d.uri);
            }),
            vscode.workspace.onDidChangeConfiguration(e => {
                if (affects(e, Key)) {
                    this.diagnostics.clear();
                    this.scheduleAll();
                }
            }),
            ctx.codeIntel.onDidRebuildIndex(() => {
                this.types = undefined;
                this.loading = undefined;
                this.scheduleAll();
            }),
        );
        this.scheduleAll();
    }

    dispose(): void {
        this.timers.forEach(t => clearTimeout(t));
        this.disposables.forEach(d => d.dispose());
    }

    private scheduleAll(): void {
        for (const d of vscode.workspace.textDocuments) {
            this.schedule(d);
        }
    }

    private schedule(document: vscode.TextDocument): void {
        if (document.languageId !== 'xpp' || !enabled(Key)) {
            return;
        }
        const key = document.uri.toString();
        clearTimeout(this.timers.get(key));
        this.timers.set(key, setTimeout(() => {
            this.timers.delete(key);
            void this.check(document).catch(err => this.ctx.log.appendLine('Obsolete API check: ' + (err as Error).message));
        }, 600));
    }

    private load(): Promise<Map<string, ObsoleteType>> {
        if (this.types) {
            return Promise.resolve(this.types);
        }
        return this.loading ??= (async () => {
            const types = new Map<string, ObsoleteType>();
            try {
                const result = await this.ctx.languageServer.lang('obsolete', {});
                if (result?.success) {
                    for (const item of (result.items ?? []) as ObsoleteItem[]) {
                        if (!item.objectName) {
                            continue;
                        }
                        const entry = types.get(item.objectName.toLowerCase()) ?? { members: new Map<string, ObsoleteItem>() };
                        const member = item.member ?? /\/Methods\/([^/]+)$/.exec(item.path ?? '')?.[1];
                        const depth = (item.path ?? '').split('/').filter(p => p).length;
                        if (member) {
                            entry.members.set(member.toLowerCase(), item);
                        } else if (depth <= 2) {
                            entry.whole = item;
                        } else {
                            continue;
                        }
                        types.set(item.objectName.toLowerCase(), entry);
                    }
                } else {
                    this.ctx.log.appendLine('Obsolete API list unavailable: ' + (result?.message ?? 'no result'));
                }
            } catch (err) {
                this.ctx.log.appendLine('Obsolete API list unavailable: ' + (err as Error).message);
            }
            this.types = types;
            return types;
        })();
    }

    private async check(document: vscode.TextDocument): Promise<void> {
        if (document.isClosed || !enabled(Key)) {
            return;
        }
        const types = await this.load();
        if (types.size === 0 || document.isClosed) {
            this.diagnostics.delete(document.uri);
            return;
        }
        const own = currentObject(document)?.objectName.toLowerCase();
        const obsoleteMembers = new Set<string>();
        types.forEach(t => t.members.forEach((_item, name) => obsoleteMembers.add(name)));
        const lines = document.getText().split(/\r?\n/);
        const found: vscode.Diagnostic[] = [];
        const report = (line: number, start: number, length: number, name: string, item: ObsoleteItem) => {
            const diagnostic = new vscode.Diagnostic(new vscode.Range(line, start, line, start + length),
                `${name} is obsolete: ${item.message || 'marked [SysObsolete].'}`,
                item.isError ? vscode.DiagnosticSeverity.Error : vscode.DiagnosticSeverity.Warning);
            diagnostic.source = 'X++ obsolete';
            diagnostic.tags = [vscode.DiagnosticTag.Deprecated];
            found.push(diagnostic);
        };
        let inComment = false;
        for (let l = 0; l < lines.length; l++) {
            const code = blankStringsAndComments(lines[l], inComment);
            inComment = code.inComment;
            const text = code.text;
            const flagged = new Set<number>();

            // Type::method(
            for (const m of text.matchAll(/\b([A-Za-z_]\w*)\s*::\s*([A-Za-z_]\w*)\s*\(/g)) {
                const type = types.get(m[1].toLowerCase());
                const item = type?.members.get(m[2].toLowerCase());
                if (item && m[1].toLowerCase() !== own) {
                    const start = m.index! + m[0].lastIndexOf(m[2]);
                    report(l, start, m[2].length, `${item.objectName}::${m[2]}`, item);
                    flagged.add(m.index!);
                }
            }

            // variable.method( where the variable is declared with an exact type that has an obsolete method
            for (const m of text.matchAll(/\b([A-Za-z_]\w*)\s*\.\s*([A-Za-z_]\w*)\s*\(/g)) {
                const variable = m[1];
                if (/^(this|super|element)$/i.test(variable) || !obsoleteMembers.has(m[2].toLowerCase())) {
                    continue;
                }
                const type = declaredType(document, variable, new vscode.Position(l, m.index!));
                const entry = type ? types.get(type.toLowerCase()) : undefined;
                const item = entry?.members.get(m[2].toLowerCase());
                if (item && type!.toLowerCase() !== own) {
                    const start = m.index! + m[0].lastIndexOf(m[2]);
                    report(l, start, m[2].length, `${item.objectName}.${m[2]}`, item);
                }
            }

            // The name of an obsolete class or table
            for (const m of text.matchAll(/[A-Za-z_]\w*/g)) {
                const word = m[0].toLowerCase();
                const item = types.get(word)?.whole;
                if (!item || word === own || flagged.has(m.index!)) {
                    continue;
                }
                const before = text.slice(0, m.index);
                if (/[.]\s*$/.test(before) || /::\s*$/.test(before)) {
                    continue;
                }
                report(l, m.index!, m[0].length, item.objectName, item);
            }
        }
        if (!document.isClosed && enabled(Key)) {
            this.diagnostics.set(document.uri, found);
        }
    }
}
