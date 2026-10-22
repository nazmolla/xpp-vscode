import * as vscode from 'vscode';
import { CodeTypes } from '../aot';
import { methodUri, objectUri, parseObjectUri, XppScheme } from '../codeView';
import { ToolNode, ToolsContribution } from '../d365Tools';
import { currentObject, declaredType, documentText, Symbol, XppDocumentSelector, XppNavigation } from '../navigation';
import { XppServerClient } from '../serverClient';
import { compilerLineToDocumentLine, methodSpans } from '../xppSource';
import { formatXpp, reindent } from './formatter';
import { affects, enabled, Features, setEnabled } from './settings';

/** An element (or member) returned by the language service. */
interface LangElement {
    kind: string;
    path: string;
    objectType?: string;
    objectName?: string;
    memberPath?: string[];
    member?: string;
    detail?: string;
    line?: number;
    column?: number;
}

interface Member {
    kind: 'method' | 'field' | 'enumValue';
    name: string;
    owner: string;
    static: boolean;
    type?: string;
    signature?: string;
    doc?: string;
    access?: string;
    extension?: boolean;
}

const KeywordList = ('abstract anytype as asc at avg boolean break breakpoint by case catch changecompany class client container continue count '
    + 'crosscompany date default delegate delete_from desc display div do edit else element enum exists extends false final finally firstfast '
    + 'firstonly firstonly1 firstonly10 firstonly100 firstonly1000 flush for forceliterals forcenestedloop forceplaceholders forceselectorder '
    + 'forupdate from generateonly group guid hint if implements in index insert_recordset int int64 interface internal is join like maxof minof '
    + 'mod new next nofetch notexists null optimisticlock order outer pessimisticlock print private protected public real repeatableread retry '
    + 'return reverse select server static str sum super switch this throw true try ttsabort ttsbegin ttscommit unchecked update_recordset using '
    + 'utcdatetime validtimestate var void while').split(' ');

const NonTypeWords = new Set(KeywordList.map(k => k.toLowerCase()));

/**
 * Code intelligence for X++ documents: outline, implementations (overrides, Chain of Command, event handlers,
 * subclasses), call and type hierarchy, code lens, completion, signature help, hover details, workspace symbols,
 * live syntax check, formatting, semantic highlighting and compile on save. Each feature is switched by a setting
 * (xpp.codeIntelligence.*), also shown under Code Intelligence in the Dynamics 365 view.
 */
export class CodeIntelligence implements vscode.Disposable, ToolsContribution {
    private readonly disposables: vscode.Disposable[] = [];
    private readonly changed = new vscode.EventEmitter<void>();
    readonly onDidChange = this.changed.event;
    private readonly lensChanged = new vscode.EventEmitter<void>();
    private readonly tokensChanged = new vscode.EventEmitter<void>();
    private readonly syntax = vscode.languages.createDiagnosticCollection('xpp-syntax');
    private readonly memberCache = new Map<string, Promise<Member[]>>();
    private readonly syntaxCache = new Map<string, any[]>();
    private readonly syntaxTimers = new Map<string, NodeJS.Timeout>();
    private indexStatus = 'not started';
    private polling?: NodeJS.Timeout;
    private building = false;

    constructor(
        private readonly lang: XppServerClient,
        private readonly bridge: XppServerClient,
        private readonly nav: XppNavigation,
        private readonly log: vscode.OutputChannel,
    ) { }

    dispose(): void {
        clearInterval(this.polling);
        this.disposables.forEach(d => d.dispose());
        this.syntax.dispose();
    }

    register(command: (id: string, handler: (...args: any[]) => unknown) => void): void {
        const sel = XppDocumentSelector;
        const legend = new vscode.SemanticTokensLegend(['class', 'struct', 'enum', 'type', 'method', 'property', 'enumMember']);
        this.disposables.push(
            this.syntax,
            vscode.languages.registerDocumentSymbolProvider(sel, { provideDocumentSymbols: d => this.symbols(d) }),
            vscode.languages.registerImplementationProvider(sel, { provideImplementation: (d, p) => this.implementations(d, p) }),
            vscode.languages.registerCallHierarchyProvider(sel, this.callHierarchy()),
            vscode.languages.registerTypeHierarchyProvider(sel, this.typeHierarchy()),
            vscode.languages.registerCodeLensProvider(sel, { onDidChangeCodeLenses: this.lensChanged.event, provideCodeLenses: d => this.codeLenses(d) }),
            vscode.languages.registerCompletionItemProvider(sel, { provideCompletionItems: (d, p) => this.completion(d, p) }, '.', ':', '@'),
            vscode.languages.registerSignatureHelpProvider(sel, { provideSignatureHelp: (d, p) => this.signatureHelp(d, p) }, '(', ','),
            vscode.languages.registerHoverProvider(sel, { provideHover: (d, p) => this.hover(d, p) }),
            vscode.languages.registerWorkspaceSymbolProvider({
                provideWorkspaceSymbols: q => this.workspaceSymbols(q),
                resolveWorkspaceSymbol: s => this.resolveWorkspaceSymbol(s),
            }),
            vscode.languages.registerDocumentFormattingEditProvider(sel, { provideDocumentFormattingEdits: d => this.format(d) }),
            vscode.languages.registerDocumentRangeFormattingEditProvider(sel, { provideDocumentRangeFormattingEdits: (d, r) => this.formatRange(d, r) }),
            vscode.languages.registerDocumentSemanticTokensProvider(sel, {
                onDidChangeSemanticTokens: this.tokensChanged.event,
                provideDocumentSemanticTokens: d => this.semanticTokens(d, legend),
            }, legend),
            vscode.workspace.onDidOpenTextDocument(d => this.scheduleSyntax(d)),
            vscode.workspace.onDidChangeTextDocument(e => this.scheduleSyntax(e.document)),
            vscode.workspace.onDidCloseTextDocument(d => this.syntax.delete(d.uri)),
            vscode.workspace.onDidSaveTextDocument(d => this.onSave(d)),
            vscode.workspace.onDidChangeConfiguration(e => {
                if (!affects(e)) {
                    return;
                }
                if (affects(e, 'syntaxCheck') && !enabled('syntaxCheck')) {
                    this.syntax.clear();
                }
                if (affects(e, 'eventHandlerIndex') && enabled('eventHandlerIndex')) {
                    void this.ensureIndex(false);
                }
                this.lensChanged.fire();
                this.tokensChanged.fire();
                this.changed.fire();
            }),
        );
        for (const d of vscode.workspace.textDocuments) {
            this.scheduleSyntax(d);
        }

        command('xpp.codeIntel.toggle', (key: string) => setEnabled(key, !enabled(key)));
        command('xpp.codeIntel.rebuildIndex', () => this.ensureIndex(true));
        command('xpp.codeIntel.showExtensions', (arg?: { objectType?: string; objectName?: string; entry?: { name: string }; item?: { objectType: string; objectName: string } }) =>
            this.showExtensions(arg));
        command('xpp.codeIntel.peek', (uri: vscode.Uri, line: number, character: number, kinds: string[]) => this.peek(uri, line, character, kinds));
        command('xpp.codeIntel.goto', (target: LangElement) => this.open(target));
        command('xpp.codeIntel.eventHandlers', (query: { path: string; event?: string; includeChildren?: boolean }, title?: string) =>
            this.showEventHandlers(query, title ?? query.path));

        // The index is built in the language server's background as soon as an X++ document is open.
        if (vscode.workspace.textDocuments.some(d => d.languageId === 'xpp')) {
            void this.ensureIndex(false);
        }
        this.disposables.push(vscode.workspace.onDidOpenTextDocument(d => {
            if (d.languageId === 'xpp' && this.indexStatus === 'not started') {
                void this.ensureIndex(false);
            }
        }));
    }

    // ── Dynamics 365 view: Code Intelligence node ──

    nodes(): ToolNode[] {
        return [{
            label: 'Code Intelligence', icon: 'lightbulb', description: this.building ? 'indexing…' : undefined,
            dynamicChildren: () => [
                ...Features.map(f => ({
                    label: f.label, icon: f.slow ? 'watch' : 'symbol-event',
                    description: f.slow ? 'slow' : undefined,
                    tooltip: f.description + (f.slow ? '\n\nSlow: costs noticeable time or resources.' : ''),
                    checked: enabled(f.key),
                    onToggle: (checked: boolean) => void setEnabled(f.key, checked),
                })),
                {
                    label: 'Rebuild Extension Index', icon: 'refresh', command: 'xpp.codeIntel.rebuildIndex',
                    description: this.indexStatus, tooltip: 'Chain of Command classes, wrapped methods and event handlers. ' + this.indexStatus,
                },
                { label: 'Show Extensions and Handlers…', icon: 'extensions', command: 'xpp.codeIntel.showExtensions' },
            ],
        }];
    }

    /** Handlers of an event (designer Events node), from the extension index. */
    private async showEventHandlers(query: { path: string; event?: string; includeChildren?: boolean }, title: string): Promise<void> {
        const result = await vscode.window.withProgress({ location: vscode.ProgressLocation.Window, title: 'Finding event handlers?' },
            () => this.lang.lang('event_handlers', { ...query, includeChildren: !!query.includeChildren, ...this.scanEvents() }));
        if (!result.success) {
            throw new Error(result.message);
        }
        const items: (LangElement & { event?: string; detail?: string })[] = result.items ?? [];
        if (items.length === 0) {
            const note = !result.indexReady ? ' The extension index is still building; try again in a moment.'
                : !enabled('eventHandlerIndex') && !/\/Methods\//.test(query.path) ? ' Form and data event handlers are found only when "Form and data event handlers" is on (Code intelligence).' : '';
            vscode.window.showInformationMessage(`No event handlers for ${title}.${note}`);
            return;
        }
        const picked = await vscode.window.showQuickPick(items.map(item => ({
            label: `${item.objectName}.${item.member ?? ''}`,
            description: item.event,
            detail: item.detail,
            item,
        })), { title: `${items.length} event handler(s) for ${title}`, matchOnDescription: true, matchOnDetail: true });
        if (picked) {
            await this.open(picked.item);
        }
    }

    /** Starts (or forces) the extension index build and follows its status. */
    async ensureIndex(force: boolean): Promise<void> {
        try {
            const result = await this.lang.lang(force ? 'refresh_index' : 'status', { scanEventHandlers: enabled('eventHandlerIndex'), force });
            this.setStatus(result.status);
            clearInterval(this.polling);
            this.polling = setInterval(async () => {
                try {
                    const s = await this.lang.lang('status', { scanEventHandlers: enabled('eventHandlerIndex') });
                    this.setStatus(s.status);
                    if (/^(ready|failed)/.test(s.status) && !/^(reading|scanning)/.test(s.status)) {
                        clearInterval(this.polling);
                        this.lensChanged.fire();
                    }
                } catch {
                    clearInterval(this.polling);
                }
            }, 3000);
        } catch (err) {
            this.setStatus('unavailable: ' + (err as Error).message);
        }
    }

    private setStatus(status: string): void {
        const building = !/^(ready|failed|unavailable|not built)/.test(status);
        if (status !== this.indexStatus || building !== this.building) {
            this.indexStatus = status;
            this.building = building;
            this.changed.fire();
        }
    }

    private scanEvents(): { scanEventHandlers: boolean } {
        return { scanEventHandlers: enabled('eventHandlerIndex') };
    }

    // ── Helpers ──

    private element(document: vscode.TextDocument): (Symbol & { methodPath?: string[] }) | undefined {
        if (document.uri.scheme !== XppScheme) {
            return undefined;
        }
        try {
            return parseObjectUri(document.uri);
        } catch {
            return undefined;
        }
    }

    /** Where an element or member is in code; undefined for elements without code (designer only). */
    private async locate(e: LangElement): Promise<vscode.Location | undefined> {
        if (!e.objectType || !e.objectName || !CodeTypes.has(e.objectType)) {
            return undefined;
        }
        const path = e.memberPath ?? [];
        if (path.length > 2 && path[path.length - 2] === 'Methods') {
            return new vscode.Location(methodUri(e.objectType, e.objectName, path), new vscode.Position(0, 0));
        }
        if (e.member) {
            return (await this.nav.definition({ objectType: e.objectType, objectName: e.objectName, member: e.member })).location;
        }
        return new vscode.Location(objectUri(e.objectType, e.objectName), new vscode.Position(0, 0));
    }

    private async open(e: LangElement): Promise<void> {
        const location = await this.locate(e);
        if (location) {
            await vscode.window.showTextDocument(location.uri, { selection: location.range });
        } else if (e.objectType && e.objectName) {
            await vscode.commands.executeCommand('xpp.openDesigner', e.objectType, e.objectName);
        }
    }

    private static label(e: LangElement): string {
        return e.member ? `${e.objectName}.${e.member}` : (e.memberPath?.length ? `${e.objectName}/${e.memberPath.join('/')}` : e.objectName ?? e.path);
    }

    /** The method enclosing a position in a code view document. */
    private enclosingMethod(document: vscode.TextDocument, position: vscode.Position): string | undefined {
        const spans = methodSpans(document.getText());
        let current: string | undefined;
        for (const s of spans) {
            if (s.startLine <= position.line) {
                current = s.name;
            }
        }
        return current && current !== 'classDeclaration' ? current : undefined;
    }

    // ── Outline ──

    private symbols(document: vscode.TextDocument): vscode.DocumentSymbol[] {
        if (!enabled('outline')) {
            return [];
        }
        const text = document.getText();
        const lines = text.split(/\r?\n/);
        const spans = methodSpans(text);
        const element = this.element(document);
        const end = (i: number) => (i + 1 < spans.length ? spans[i + 1].startLine - 1 : lines.length - 1);
        // Only real methods: the spans also contain macro lines (#define...) of the declaration.
        const methods = spans.filter(s => s.name !== 'classDeclaration' && /^[A-Za-z_]\w*$/.test(s.name)).map(s => {
            const i = spans.indexOf(s);
            const last = Math.min(lines.length - 1, Math.max(s.startLine, end(i)));
            let line = s.startLine;
            while (line < last && !new RegExp(`\\b${s.name}\\s*\\(`).test(lines[line])) {
                line++;
            }
            const col = Math.max(0, lines[line].search(new RegExp(`\\b${s.name}\\b`)));
            const signature = lines[line].trim();
            const range = new vscode.Range(s.startLine, 0, last, lines[last].length);
            // The selection must lie inside the range (VS Code rejects the whole outline otherwise).
            const selection = range.intersection(new vscode.Range(line, col, line, col + s.name.length)) ?? new vscode.Range(s.startLine, 0, s.startLine, 0);
            return new vscode.DocumentSymbol(s.name, signature.replace(new RegExp(`\\b${s.name}\\b.*`), '').trim() || 'method',
                vscode.SymbolKind.Method, range, selection);
        });
        if (!element || element.methodPath) {
            return methods;
        }
        // Members declared in the class declaration (before the first method).
        const declEnd = spans.find(s => s.name !== 'classDeclaration')?.startLine ?? lines.length;
        const fields: vscode.DocumentSymbol[] = [];
        for (let l = 0; l < declEnd; l++) {
            const m = /^\s*(?:(?:public|private|protected|internal|static|const|readonly)\s+)*([A-Za-z_]\w*(?:\[\s*\d*\s*\])?)\s+([A-Za-z_]\w*)\s*(?:=[^;]*)?;/.exec(lines[l]);
            if (m && !NonTypeWords.has(m[1].toLowerCase()) || (m && /^(int|int64|str|real|boolean|date|utcdatetime|container|guid|anytype)$/i.test(m[1]))) {
                const col = lines[l].indexOf(m[2]);
                const r = new vscode.Range(l, col, l, col + m[2].length);
                fields.push(new vscode.DocumentSymbol(m[2], m[1], vscode.SymbolKind.Field, new vscode.Range(l, 0, l, lines[l].length), r));
            }
        }
        const root = new vscode.DocumentSymbol(element.objectName, element.objectType.replace(/^Ax/, ''),
            element.objectType === 'AxTable' ? vscode.SymbolKind.Struct : vscode.SymbolKind.Class,
            new vscode.Range(0, 0, lines.length - 1, lines[lines.length - 1].length), new vscode.Range(0, 0, 0, lines[0].length));
        root.children = [...fields, ...methods];
        return [root];
    }

    // ── Implementations ──

    private async implementationItems(symbol: { objectType: string; objectName: string; member?: string }): Promise<LangElement[]> {
        const result = await this.lang.lang('implementations', { ...symbol, ...this.scanEvents() });
        if (!result?.success) {
            throw new Error(result?.message ?? 'Implementations are not available.');
        }
        if (!result.indexReady) {
            vscode.window.setStatusBarMessage('$(sync~spin) Extension index still building; Chain of Command and handlers may be missing', 6000);
        }
        return result.items as LangElement[];
    }

    private async targetAt(document: vscode.TextDocument, position: vscode.Position): Promise<Symbol | undefined> {
        const symbol = await this.nav.resolve(document, position);
        if (symbol) {
            if (symbol.member) {
                const found = await this.nav.definition(symbol);
                return { objectType: found.owner.objectType, objectName: found.owner.objectName, member: symbol.member };
            }
            return symbol;
        }
        const element = this.element(document);
        if (!element || element.methodPath) {
            return undefined;
        }
        const method = this.enclosingMethod(document, position);
        // On the declaration line: the element itself; inside a method: that method.
        return method && document.lineAt(position.line).text.includes(method)
            ? { objectType: element.objectType, objectName: element.objectName, member: method }
            : { objectType: element.objectType, objectName: element.objectName };
    }

    private async implementations(document: vscode.TextDocument, position: vscode.Position): Promise<vscode.Location[]> {
        if (!enabled('navigation')) {
            return [];
        }
        const target = await this.targetAt(document, position);
        if (!target) {
            return [];
        }
        const items = await this.implementationItems(target);
        const locations = await Promise.all(items.map(i => this.locate(i)));
        const withoutCode = items.filter((_, i) => !locations[i]);
        if (withoutCode.length > 0) {
            vscode.window.setStatusBarMessage(`${withoutCode.length} more without code (metadata extensions); see Show Extensions and Handlers`, 6000);
        }
        return locations.filter((l): l is vscode.Location => !!l);
    }

    /** Quick pick of everything that extends an element: CoC classes, handlers, subclasses, metadata extensions. */
    private async showExtensions(arg?: { objectType?: string; objectName?: string; entry?: { name: string }; item?: { objectType: string; objectName: string } }): Promise<void> {
        let element: { objectType: string; objectName: string } | undefined =
            arg?.item ? arg.item : arg?.objectType && arg.entry ? { objectType: arg.objectType, objectName: arg.entry.name }
                : arg?.objectType && arg.objectName ? { objectType: arg.objectType, objectName: arg.objectName } : undefined;
        if (!element && vscode.window.activeTextEditor) {
            element = this.element(vscode.window.activeTextEditor.document);
        }
        if (!element) {
            const name = await vscode.window.showInputBox({ title: 'Show extensions of', placeHolder: 'SalesTable, SalesFormLetter, CustTable…' });
            if (!name) {
                return;
            }
            const type = await this.nav.findType(name);
            if (!type) {
                throw new Error(`No class, table, form or other element named ${name}.`);
            }
            element = type;
        }
        const items = await vscode.window.withProgress({ location: vscode.ProgressLocation.Window, title: `Extensions of ${element.objectName}` },
            () => this.implementationItems({ objectType: element!.objectType, objectName: element!.objectName }));
        if (items.length === 0) {
            vscode.window.showInformationMessage(`Nothing extends ${element.objectName}.`);
            return;
        }
        const order = ['Chain of Command class', 'Event handler', 'Metadata extension', 'Subclass'];
        items.sort((a, b) => order.indexOf(a.kind) - order.indexOf(b.kind) || CodeIntelligence.label(a).localeCompare(CodeIntelligence.label(b)));
        const picks: (vscode.QuickPickItem & { e?: LangElement })[] = [];
        let lastKind = '';
        for (const e of items) {
            if (e.kind !== lastKind) {
                picks.push({ label: e.kind, kind: vscode.QuickPickItemKind.Separator });
                lastKind = e.kind;
            }
            picks.push({ label: CodeIntelligence.label(e), description: e.detail, detail: e.objectType?.replace(/^Ax/, ''), e });
        }
        const picked = await vscode.window.showQuickPick(picks, { title: `${items.length} extension(s) of ${element.objectName}`, matchOnDescription: true });
        if (picked?.e) {
            await this.open(picked.e);
        }
    }

    private async peek(uri: vscode.Uri, line: number, character: number, kinds: string[]): Promise<void> {
        const document = await vscode.workspace.openTextDocument(uri);
        const position = new vscode.Position(line, character);
        let locations: vscode.Location[];
        if (kinds.includes('references')) {
            locations = await this.nav.provideReferences(document, position, { includeDeclaration: false });
        } else {
            const target = await this.targetAt(document, position);
            const items = target ? (await this.implementationItems(target)).filter(i => kinds.includes(i.kind)) : [];
            locations = (await Promise.all(items.map(i => this.locate(i)))).filter((l): l is vscode.Location => !!l);
        }
        await vscode.commands.executeCommand('editor.action.peekLocations', uri, position, locations, 'peek');
    }

    // ── Call hierarchy ──

    private callHierarchy(): vscode.CallHierarchyProvider {
        const itemFor = async (e: LangElement): Promise<vscode.CallHierarchyItem | undefined> => {
            const location = await this.locate(e);
            if (!location) {
                return undefined;
            }
            const item = new vscode.CallHierarchyItem(vscode.SymbolKind.Method, e.member ?? CodeIntelligence.label(e), e.objectName ?? '',
                location.uri, location.range, location.range);
            (item as any).xpp = e;
            return item;
        };
        return {
            prepareCallHierarchy: async (document, position) => {
                if (!enabled('navigation')) {
                    return undefined;
                }
                const target = await this.targetAt(document, position);
                const element = this.element(document);
                const member = target?.member ?? (element ? this.enclosingMethod(document, position) : undefined);
                const owner = target?.member ? target : element;
                if (!owner || !member) {
                    return undefined;
                }
                return itemFor({ kind: 'Method', path: '', objectType: owner.objectType, objectName: owner.objectName, member, memberPath: ['Methods', member] });
            },
            provideCallHierarchyIncomingCalls: async item => {
                const e: LangElement = (item as any).xpp;
                const result = await this.lang.lang('calls', { objectType: e.objectType, objectName: e.objectName, member: e.member, direction: 'in', max: '200' });
                const byCaller = new Map<string, LangElement[]>();
                for (const c of (result.calls ?? []) as LangElement[]) {
                    byCaller.set(c.path, [...(byCaller.get(c.path) ?? []), c]);
                }
                const calls: vscode.CallHierarchyIncomingCall[] = [];
                for (const group of byCaller.values()) {
                    const from = await itemFor(group[0]);
                    if (!from) {
                        continue;
                    }
                    const text = await documentText(from.uri);
                    const ranges = group.map(c => {
                        const line = text && c.line ? compilerLineToDocumentLine(text, c.line) : from.range.start.line;
                        return new vscode.Range(line, 0, line, 0);
                    });
                    calls.push(new vscode.CallHierarchyIncomingCall(from, ranges));
                }
                return calls;
            },
            provideCallHierarchyOutgoingCalls: async item => {
                const e: LangElement = (item as any).xpp;
                const result = await this.lang.lang('calls', { objectType: e.objectType, objectName: e.objectName, member: e.member, direction: 'out', max: '200' });
                const ownText = await documentText(item.uri);
                const byTarget = new Map<string, LangElement[]>();
                for (const c of (result.calls ?? []) as LangElement[]) {
                    if (c.objectType) {
                        byTarget.set(c.path, [...(byTarget.get(c.path) ?? []), c]);
                    }
                }
                const calls: vscode.CallHierarchyOutgoingCall[] = [];
                for (const group of [...byTarget.values()].slice(0, 100)) {
                    const to = await itemFor(group[0]);
                    if (!to) {
                        continue;
                    }
                    const ranges = group.map(c => {
                        const line = ownText && c.line ? compilerLineToDocumentLine(ownText, c.line) : item.range.start.line;
                        return new vscode.Range(line, Math.max(0, (c.column ?? 1) - 1), line, Math.max(0, (c.column ?? 1) - 1));
                    });
                    calls.push(new vscode.CallHierarchyOutgoingCall(to, ranges));
                }
                return calls;
            },
        };
    }

    // ── Type hierarchy ──

    private typeHierarchy(): vscode.TypeHierarchyProvider {
        const itemFor = (objectType: string, objectName: string) => {
            const uri = objectUri(objectType, objectName);
            const item = new vscode.TypeHierarchyItem(vscode.SymbolKind.Class, objectName, objectType.replace(/^Ax/, ''), uri,
                new vscode.Range(0, 0, 0, 0), new vscode.Range(0, 0, 0, 0));
            (item as any).xpp = { objectType, objectName };
            return item;
        };
        return {
            prepareTypeHierarchy: async (document, position) => {
                if (!enabled('navigation')) {
                    return undefined;
                }
                const symbol = await this.nav.resolve(document, position);
                const target = symbol && !symbol.member ? symbol : this.element(document);
                return target?.objectType === 'AxClass' ? itemFor(target.objectType, target.objectName) : undefined;
            },
            provideTypeHierarchySupertypes: async item => {
                const base = await this.nav.baseOf((item as any).xpp);
                return base ? [itemFor(base.objectType, base.objectName)] : [];
            },
            provideTypeHierarchySubtypes: async item => {
                const x = (item as any).xpp;
                const result = await this.lang.lang('subtypes', { objectType: x.objectType, objectName: x.objectName });
                return ((result.subtypes ?? []) as LangElement[]).filter(s => s.objectType && s.objectName).map(s => itemFor(s.objectType!, s.objectName!));
            },
        };
    }

    // ── Code lens ──

    private async codeLenses(document: vscode.TextDocument): Promise<vscode.CodeLens[]> {
        const element = this.element(document);
        if (!enabled('codeLens') || !element || element.methodPath) {
            return [];
        }
        const result = await this.lang.lang('code_lens', { objectType: element.objectType, objectName: element.objectName, ...this.scanEvents() }).catch(() => undefined);
        if (!result?.success) {
            return [];
        }
        const text = document.getText();
        const lines = text.split(/\r?\n/);
        const lenses: vscode.CodeLens[] = [];
        const plural = (n: number, word: string) => `${n} ${n === 1 ? word : /(s|sh|ch|x)$/.test(word) ? word + 'es' : word + 's'}`;
        const at = (line: number, title: string, command: string, args: unknown[]) =>
            lenses.push(new vscode.CodeLens(new vscode.Range(line, 0, line, 0), { title, command, arguments: args }));

        // Element level, on the first line.
        const header: string[] = [];
        if (result.extensionClasses) { header.push(plural(result.extensionClasses, 'Chain of Command class')); }
        if (result.objectHandlers) { header.push(plural(result.objectHandlers, 'event handler')); }
        if (result.subclasses) { header.push(plural(result.subclasses, 'subclass')); }
        if (header.length) {
            at(0, header.join(' · '), 'xpp.codeIntel.showExtensions', [{ objectType: element.objectType, objectName: element.objectName }]);
        }
        if (result.extends) {
            at(0, `extends ${CodeIntelligence.label(result.extends)} (Chain of Command)`, 'xpp.codeIntel.goto', [result.extends]);
        }

        for (const span of methodSpans(text).filter(s => /^[A-Za-z_]\w*$/.test(s.name))) {
            let line = span.startLine;
            while (line < lines.length - 1 && !new RegExp(`\\b${span.name}\\s*\\(`).test(lines[line])) {
                line++;
            }
            const col = Math.max(0, lines[line].search(new RegExp(`\\b${span.name}\\b`)));
            const m = result.methods?.[span.name];
            const args = (kinds: string[]) => [document.uri, line, col, kinds];
            if (m) {
                at(line, plural(m.references, 'reference'), 'xpp.codeIntel.peek', args(['references']));
                if (m.chainOfCommand) { at(line, `${m.chainOfCommand} Chain of Command`, 'xpp.codeIntel.peek', args(['Chain of Command'])); }
                if (m.overrides) { at(line, plural(m.overrides, 'override'), 'xpp.codeIntel.peek', args(['Override'])); }
                if (m.handlers) { at(line, plural(m.handlers, 'handler'), 'xpp.codeIntel.peek', args(['Event handler'])); }
            }
            const wraps = result.wraps?.[span.name];
            if (wraps) { at(line, `wraps ${CodeIntelligence.label(wraps)}`, 'xpp.codeIntel.goto', [wraps]); }
            const handles = result.handles?.[span.name];
            if (handles) { at(line, `${handles.detail ?? 'handles ' + CodeIntelligence.label(handles)}`, 'xpp.codeIntel.goto', [handles]); }
        }
        if (!result.indexReady) {
            at(0, '$(sync~spin) extension index building…', 'xpp.codeIntel.rebuildIndex', []);
        }
        return lenses;
    }

    // ── Completion and signature help ──

    private members(objectType: string, objectName: string): Promise<Member[]> {
        const key = `${objectType}/${objectName}`.toLowerCase();
        let cached = this.memberCache.get(key);
        if (!cached) {
            cached = this.lang.lang('members', { objectType, objectName, ...this.scanEvents() })
                .then(r => (r?.success ? r.members as Member[] : []))
                .catch(() => []);
            this.memberCache.set(key, cached);
            setTimeout(() => this.memberCache.delete(key), 60_000);
        }
        return cached;
    }

    /** The type whose members follow `qualifier.` or `qualifier::`. */
    private async ownerOf(document: vscode.TextDocument, position: vscode.Position, qualifier: string, called: boolean):
        Promise<{ objectType: string; objectName: string } | undefined> {
        const current = currentObject(document);
        if (/^(this|element)$/i.test(qualifier)) {
            return current;
        }
        if (/^super$/i.test(qualifier)) {
            return current ? this.nav.baseOf(current) : undefined;
        }
        if (called) {
            return undefined;
        }
        const declared = declaredType(document, qualifier, position);
        return this.nav.findType(declared ?? qualifier);
    }

    private async completion(document: vscode.TextDocument, position: vscode.Position): Promise<vscode.CompletionItem[] | vscode.CompletionList | undefined> {
        if (!enabled('completion')) {
            return undefined;
        }
        const before = document.lineAt(position.line).text.slice(0, position.character);

        // Label references inside strings: "@...
        const label = /(["'])@([\w:]*)$/.exec(before);
        if (label) {
            const result = await this.lang.lang('labels', { search: label[2], max: '100' });
            const start = position.translate(0, -(label[2].length + 1));
            return new vscode.CompletionList(((result.labels ?? []) as { reference: string; text: string; comment: string }[]).map(l => {
                const item = new vscode.CompletionItem({ label: l.reference, description: l.text }, vscode.CompletionItemKind.Text);
                item.range = new vscode.Range(start, position);
                item.filterText = l.reference + ' ' + l.text;
                item.documentation = l.comment || undefined;
                return item;
            }), true);
        }

        const member = /([A-Za-z_]\w*)\s*(\(\s*\))?\s*(\.|::)\s*(\w*)$/.exec(before);
        if (member) {
            const isStatic = member[3] === '::';
            const owner = await this.ownerOf(document, position, member[1], !!member[2]);
            if (!owner) {
                return undefined;
            }
            const current = currentObject(document);
            const own = current && current.objectName.toLowerCase() === owner.objectName.toLowerCase();
            const list = (await this.members(owner.objectType, owner.objectName)).filter(m =>
                (isStatic ? m.static || m.kind === 'enumValue' : !m.static && m.kind !== 'enumValue')
                && (own || m.access !== 'private'));
            return list.map((m, i) => {
                const kind = m.kind === 'method' ? vscode.CompletionItemKind.Method
                    : m.kind === 'enumValue' ? vscode.CompletionItemKind.EnumMember : vscode.CompletionItemKind.Field;
                const item = new vscode.CompletionItem({ label: m.name, detail: m.kind === 'method' ? '()' : undefined, description: m.owner + (m.extension ? ' (extension)' : '') }, kind);
                item.detail = m.signature ?? (m.type ? `${m.type} ${m.name}` : undefined);
                item.documentation = m.doc;
                item.sortText = String(i).padStart(4, '0');
                if (m.kind === 'method') {
                    item.insertText = new vscode.SnippetString(`${m.name}($0)`);
                    item.command = { command: 'editor.action.triggerParameterHints', title: 'Parameter hints' };
                }
                return item;
            });
        }

        const word = /[A-Za-z_]\w*$/.exec(before);
        if (!word) {
            return undefined;
        }
        return KeywordList.map(k => new vscode.CompletionItem(k, vscode.CompletionItemKind.Keyword));
    }

    private async signatureHelp(document: vscode.TextDocument, position: vscode.Position): Promise<vscode.SignatureHelp | undefined> {
        if (!enabled('completion')) {
            return undefined;
        }
        // Find the unclosed "(" of the call the cursor is in (looking back over at most a few lines).
        const startLine = Math.max(0, position.line - 10);
        const text = document.getText(new vscode.Range(startLine, 0, position.line, position.character));
        let depth = 0;
        let commas = 0;
        let open = -1;
        let quote = '';
        for (let i = text.length - 1; i >= 0; i--) {
            const c = text[i];
            if (quote) {
                if (c === quote) { quote = ''; }
                continue;
            }
            if (c === '"' || c === '\'') { quote = c; continue; }
            if (c === ')' || c === ']') { depth++; }
            else if (c === '[') { depth--; }
            else if (c === '(') {
                if (depth === 0) { open = i; break; }
                depth--;
            } else if (c === ',' && depth === 0) { commas++; }
            else if ((c === ';' || c === '{' || c === '}') && depth === 0) { return undefined; }
        }
        if (open < 0) {
            return undefined;
        }
        const head = /([A-Za-z_]\w*)\s*$/.exec(text.slice(0, open));
        if (!head) {
            return undefined;
        }
        const offset = document.offsetAt(new vscode.Position(startLine, 0)) + head.index;
        const symbol = await this.nav.resolve(document, document.positionAt(offset));
        let signature: string | undefined;
        if (symbol?.member) {
            // The member list has the whole signature (also over several lines, and for kernel and extension methods).
            signature = (await this.members(symbol.objectType, symbol.objectName)).find(m => m.name.toLowerCase() === symbol.member!.toLowerCase())?.signature
                ?? (await this.nav.definition(symbol)).signature;
        }
        if (!signature) {
            return undefined;
        }
        const params = /\(([\s\S]*)\)/.exec(signature)?.[1] ?? '';
        const parts = splitParameters(params);
        const info = new vscode.SignatureInformation(signature);
        let at = signature.indexOf('(') + 1;
        info.parameters = parts.map(p => {
            const start = signature!.indexOf(p, at);
            at = start + p.length;
            return new vscode.ParameterInformation([start, start + p.length]);
        });
        const help = new vscode.SignatureHelp();
        help.signatures = [info];
        help.activeSignature = 0;
        help.activeParameter = Math.min(commas, Math.max(0, parts.length - 1));
        return help;
    }

    // ── Hover details ──

    private async hover(document: vscode.TextDocument, position: vscode.Position): Promise<vscode.Hover | undefined> {
        if (!enabled('hover')) {
            return undefined;
        }
        const labelRange = document.getWordRangeAtPosition(position, /@[A-Za-z_]\w*:[A-Za-z_]\w*|@[A-Z]{2,}\d+/);
        if (labelRange) {
            const reference = document.getText(labelRange);
            const colon = reference.indexOf(':');
            const fileId = colon > 0 ? reference.slice(1, colon) : reference.replace(/^@([A-Z]+)\d+$/, '$1');
            const labelId = colon > 0 ? reference.slice(colon + 1) : reference;
            const result = await this.bridge.bridge('read_label', { labelFileId: fileId, labelId }).catch(() => undefined);
            const text = result?.labels?.[0]?.text;
            return text ? new vscode.Hover(new vscode.MarkdownString().appendMarkdown(`**${reference}**: `).appendText(text), labelRange) : undefined;
        }
        const symbol = await this.nav.resolve(document, position);
        if (!symbol || symbol.member || !/^(AxEdt|AxEnum)$/.test(symbol.objectType)) {
            return undefined;
        }
        const read = await this.bridge.bridge('read_object', { objectType: symbol.objectType, objectName: symbol.objectName }).catch(() => undefined);
        if (!read?.success) {
            return undefined;
        }
        const props = read.properties ?? {};
        const md = new vscode.MarkdownString();
        if (symbol.objectType === 'AxEdt') {
            const parts = [props.EdtType && `type ${props.EdtType}`, props.Extends && `extends ${props.Extends}`, props.StringSize && `size ${props.StringSize}`,
                props.EnumType && `enum ${props.EnumType}`, props.ReferenceTable && `table ${props.ReferenceTable}`].filter(Boolean);
            md.appendMarkdown(parts.join(' · ') || 'EDT');
        } else {
            const values = (read.enumValues ?? []) as { name: string; value?: number }[];
            md.appendMarkdown(`${values.length} value(s): ` + values.slice(0, 20).map(v => `\`${v.name}\``).join(', ') + (values.length > 20 ? ', …' : ''));
        }
        if (props.Label) {
            md.appendMarkdown(`\n\nLabel: ${props.Label}`);
        }
        return new vscode.Hover(md, document.getWordRangeAtPosition(position));
    }

    // ── Workspace symbols ──

    private async workspaceSymbols(query: string): Promise<vscode.SymbolInformation[]> {
        if (!enabled('workspaceSymbols') || query.trim().length < 2) {
            return [];
        }
        const result = await this.lang.lang('symbols', { query: query.trim(), max: '100' }).catch(() => undefined);
        return ((result?.symbols ?? []) as LangElement[])
            .filter(s => s.objectType && s.objectName && CodeTypes.has(s.objectType))
            .map(s => {
                const info = new vscode.SymbolInformation(s.member ?? s.objectName!, s.member ? vscode.SymbolKind.Method : vscode.SymbolKind.Class,
                    s.member ? s.objectName! : s.objectType!.replace(/^Ax/, ''), new vscode.Location(objectUri(s.objectType!, s.objectName!), new vscode.Position(0, 0)));
                (info as any).xpp = s;
                return info;
            });
    }

    private async resolveWorkspaceSymbol(symbol: vscode.SymbolInformation): Promise<vscode.SymbolInformation> {
        const e: LangElement | undefined = (symbol as any).xpp;
        const location = e?.member ? await this.locate(e) : undefined;
        if (location) {
            symbol.location = location;
        }
        return symbol;
    }

    // ── Live syntax check ──

    private scheduleSyntax(document: vscode.TextDocument): void {
        if (document.languageId !== 'xpp' || !enabled('syntaxCheck')) {
            return;
        }
        const key = document.uri.toString();
        clearTimeout(this.syntaxTimers.get(key));
        this.syntaxTimers.set(key, setTimeout(() => void this.checkSyntax(document).catch(err => this.log.appendLine('Syntax check: ' + (err as Error).message)), 600));
    }

    private async checkSyntax(document: vscode.TextDocument): Promise<void> {
        if (document.isClosed) {
            return;
        }
        const element = this.element(document);
        const lines = document.getText().split(/\r?\n/);
        const blocks: { kind: 'method' | 'declaration'; start: number; text: string }[] = [];
        const spans = methodSpans(document.getText());
        // A class layout (declaration, methods inside its braces) only when the text starts with a class or interface.
        const declarationFirst = /^\s*(?:\[[^\]]*\]\s*)*(?:(?:public|internal|final|abstract|static)\s+)*(?:class|interface)\b/
            .test(lines.filter(l => !/^\s*(\/\/|$)/.test(l)).join('\n'));
        if (element?.methodPath || spans.length === 0 || (!declarationFirst && spans[0].name !== 'classDeclaration' && spans.length <= 1)) {
            blocks.push({ kind: 'method', start: 0, text: lines.join('\n') });
        } else if (!declarationFirst && spans[0].name !== 'classDeclaration') {
            spans.forEach((s, i) => blocks.push({
                kind: 'method', start: s.startLine,
                text: lines.slice(s.startLine, i + 1 < spans.length ? spans[i + 1].startLine : lines.length).join('\n'),
            }));
        } else if (spans[0].name === 'classDeclaration') {
            // One block per method, the first being the class declaration.
            spans.forEach((s, i) => blocks.push({
                kind: s.name === 'classDeclaration' ? 'declaration' : 'method', start: s.startLine,
                text: lines.slice(s.startLine, i + 1 < spans.length ? spans[i + 1].startLine : lines.length).join('\n'),
            }));
        } else {
            // Class layout: the declaration, the methods inside its braces, then the closing brace.
            let closing = lines.length - 1;
            while (closing > 0 && lines[closing].trim() === '') { closing--; }
            blocks.push({ kind: 'declaration', start: 0, text: lines.slice(0, spans[0].startLine).join('\n') + '\n}' });
            spans.forEach((s, i) => blocks.push({
                kind: 'method', start: s.startLine,
                text: lines.slice(s.startLine, i + 1 < spans.length ? spans[i + 1].startLine : closing).join('\n'),
            }));
        }
        const diagnostics: vscode.Diagnostic[] = [];
        await Promise.all(blocks.map(async b => {
            const cacheKey = b.kind + '\u0000' + b.text;
            let found = this.syntaxCache.get(cacheKey);
            if (!found) {
                const result = await this.lang.lang('syntax', { kind: b.kind, source: b.text, elementType: element?.objectType?.replace(/^Ax/, '') ?? 'Class', elementName: element?.objectName ?? 'Code' });
                found = result?.success ? result.diagnostics : [];
                this.syntaxCache.set(cacheKey, found!);
                if (this.syntaxCache.size > 2000) {
                    this.syntaxCache.clear();
                }
            }
            for (const d of found!) {
                const line = Math.min(lines.length - 1, b.start + Math.max(0, d.line - 1));
                const endLine = Math.min(lines.length - 1, b.start + Math.max(0, (d.endLine || d.line) - 1));
                const range = new vscode.Range(line, Math.max(0, d.column - 1), endLine, Math.max(d.column, (d.endColumn || d.column) - 1));
                const diagnostic = new vscode.Diagnostic(range, d.message, /Warning/.test(d.severity) ? vscode.DiagnosticSeverity.Warning : vscode.DiagnosticSeverity.Error);
                diagnostic.source = 'X++ syntax';
                diagnostic.code = d.code;
                diagnostics.push(diagnostic);
            }
        }));
        if (!document.isClosed && enabled('syntaxCheck')) {
            this.syntax.set(document.uri, diagnostics);
        }
    }

    // ── Compile on save ──

    private compiling = false;

    private async onSave(document: vscode.TextDocument): Promise<void> {
        this.memberCache.clear();
        this.lensChanged.fire();
        if (document.languageId !== 'xpp' || !enabled('diagnosticsOnSave') || this.compiling) {
            return;
        }
        this.compiling = true;
        try {
            await vscode.commands.executeCommand('xpp.buildProject');
        } finally {
            this.compiling = false;
        }
    }

    // ── Formatting ──

    private format(document: vscode.TextDocument): vscode.TextEdit[] {
        if (!enabled('formatting')) {
            return [];
        }
        const text = document.getText();
        const formatted = formatXpp(text, { allman: true });
        if (formatted === text) {
            return [];
        }
        return [vscode.TextEdit.replace(new vscode.Range(0, 0, document.lineCount, 0), formatted)];
    }

    private formatRange(document: vscode.TextDocument, range: vscode.Range): vscode.TextEdit[] {
        if (!enabled('formatting')) {
            return [];
        }
        // Reindent the whole document (the indentation of a line depends on everything above it), keep only the range.
        const lines = document.getText().split(/\r?\n/);
        const indented = reindent(lines);
        const edits: vscode.TextEdit[] = [];
        for (let l = range.start.line; l <= range.end.line && l < lines.length; l++) {
            if (indented[l] !== lines[l]) {
                edits.push(vscode.TextEdit.replace(document.lineAt(l).range, indented[l]));
            }
        }
        return edits;
    }

    // ── Semantic highlighting ──

    private async semanticTokens(document: vscode.TextDocument, legend: vscode.SemanticTokensLegend): Promise<vscode.SemanticTokens | undefined> {
        if (!enabled('semanticHighlighting')) {
            return undefined;
        }
        const builder = new vscode.SemanticTokensBuilder(legend);
        const lines = document.getText().split(/\r?\n/);
        const typeOf = new Map<string, string | undefined>();
        const pending: { line: number; col: number; len: number; word: string }[] = [];
        let inComment = false;
        const tokens: { line: number; col: number; len: number; type: string }[] = [];
        for (let l = 0; l < lines.length; l++) {
            const code = blankStringsAndComments(lines[l], inComment);
            inComment = code.inComment;
            const re = /[A-Za-z_]\w*/g;
            for (let m = re.exec(code.text); m; m = re.exec(code.text)) {
                const word = m[0];
                if (NonTypeWords.has(word.toLowerCase())) {
                    continue;
                }
                const before = code.text.slice(0, m.index);
                const after = code.text.slice(m.index + word.length);
                if (/::\s*$/.test(before)) {
                    tokens.push({ line: l, col: m.index, len: word.length, type: /^\s*\(/.test(after) ? 'method' : 'enumMember' });
                } else if (/\.\s*$/.test(before)) {
                    tokens.push({ line: l, col: m.index, len: word.length, type: /^\s*\(/.test(after) ? 'method' : 'property' });
                } else if (/^\s*\(/.test(after) && !/\bnew\s+$/.test(before)) {
                    tokens.push({ line: l, col: m.index, len: word.length, type: 'method' });
                } else if (/^[A-Z]/.test(word) && word.length >= 3) {
                    pending.push({ line: l, col: m.index, len: word.length, word });
                }
            }
        }
        // Look up each distinct candidate type name once (bounded).
        const distinct = [...new Set(pending.map(p => p.word.toLowerCase()))].slice(0, 200);
        await Promise.all(distinct.map(async w => {
            const found = await this.nav.findType(w).catch(() => undefined);
            typeOf.set(w, found?.objectType);
        }));
        for (const p of pending) {
            const t = typeOf.get(p.word.toLowerCase());
            const type = t === 'AxClass' ? 'class' : t === 'AxEnum' ? 'enum' : t === 'AxEdt' ? 'type'
                : t && /^(AxTable|AxView|AxDataEntityView|AxMap)$/.test(t) ? 'struct' : undefined;
            if (type) {
                tokens.push({ line: p.line, col: p.col, len: p.len, type });
            }
        }
        tokens.sort((a, b) => a.line - b.line || a.col - b.col);
        for (const t of tokens) {
            builder.push(t.line, t.col, t.len, legend.tokenTypes.indexOf(t.type), 0);
        }
        return builder.build();
    }
}

/** Splits a parameter list on top-level commas. */
function splitParameters(params: string): string[] {
    const parts: string[] = [];
    let depth = 0;
    let current = '';
    for (const c of params) {
        if (c === '(' || c === '[') { depth++; }
        if (c === ')' || c === ']') { depth--; }
        if (c === ',' && depth === 0) {
            parts.push(current.trim());
            current = '';
        } else {
            current += c;
        }
    }
    if (current.trim()) {
        parts.push(current.trim());
    }
    return parts;
}

/** A line with strings and comments replaced by spaces (positions kept). */
function blankStringsAndComments(line: string, inComment: boolean): { text: string; inComment: boolean } {
    let out = '';
    let quote = '';
    for (let i = 0; i < line.length; i++) {
        const c = line[i];
        if (inComment) {
            if (c === '*' && line[i + 1] === '/') { inComment = false; out += '  '; i++; continue; }
            out += ' ';
            continue;
        }
        if (quote) {
            if (c === '\\') { out += '  '; i++; continue; }
            if (c === quote) { quote = ''; }
            out += ' ';
            continue;
        }
        if (c === '/' && line[i + 1] === '/') { out += ' '.repeat(line.length - i); break; }
        if (c === '/' && line[i + 1] === '*') { inComment = true; out += '  '; i++; continue; }
        if (c === '"' || c === '\'') { quote = c; out += ' '; continue; }
        out += c;
    }
    return { text: out, inComment };
}
