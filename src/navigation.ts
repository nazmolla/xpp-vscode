import * as vscode from 'vscode';
import { CodeTypes, elementOpenCommand, iconFor, typeName } from './aot';
import { methodUri, objectUri, parseObjectUri, XppScheme } from './codeView';
import { XppServerClient } from './serverClient';
import { compilerLineToDocumentLine, methodSpans } from './xppSource';

interface Match {
    objectType: string;
    objectName: string;
    modelName: string;
    isCustom: boolean;
}

/** What a symbol in code refers to: an object, or a member (method, field, enum value) of one. */
export interface Symbol {
    objectType: string;
    objectName: string;
    member?: string;
    modelName?: string;
    isCustom?: boolean;
}

/** Types an identifier in code most likely refers to, in lookup order. */
const NavigableTypes = ['AxClass', 'AxTable', 'AxEnum', 'AxEdt', 'AxView', 'AxDataEntityView', 'AxMap', 'AxQuery', 'AxForm'];

const Keywords = new Set(('if else while for do switch case default break continue select from where join exists notexists outer firstonly firstonly10 '
    + 'firstonly100 firstonly1000 forupdate nofetch crosscompany validtimestate order group by asc desc index hint return public private protected '
    + 'static void int int64 str real boolean date utcdatetime anytype container guid this super new null true false class extends implements '
    + 'internal final display edit var next ttsbegin ttscommit ttsabort try catch throw finally retry using in is as abstract server client '
    + 'delete_from update_recordset insert_recordset count sum avg minof maxof element').split(' '));

/** Most levels of base classes searched for a method. */
const MaxBaseDepth = 6;

/**
 * Go to / Peek Definition, Find All References and hover for X++ code, resolved through the metadata and the
 * cross-reference database (no language server needed): types, Class::member, this./super./variable.member and calls.
 */
export class XppNavigation implements vscode.DefinitionProvider, vscode.HoverProvider, vscode.ReferenceProvider {
    private readonly typeCache = new Map<string, Match | null>();

    constructor(private readonly server: XppServerClient) { }

    async provideDefinition(document: vscode.TextDocument, position: vscode.Position): Promise<vscode.Location | undefined> {
        const symbol = await this.resolve(document, position);
        return symbol ? (await this.definition(symbol)).location : undefined;
    }

    async provideHover(document: vscode.TextDocument, position: vscode.Position): Promise<vscode.Hover | undefined> {
        const symbol = await this.resolve(document, position);
        if (!symbol) {
            return undefined;
        }
        const range = document.getWordRangeAtPosition(position);
        const type = symbol.objectType.replace(/^Ax/, '');
        if (!symbol.member) {
            const text = new vscode.MarkdownString(`**${symbol.objectName}** · ${type}${symbol.modelName ? ` in model \`${symbol.modelName}\`` : ''}${symbol.isCustom ? ' (yours)' : ''}`);
            const found = await this.server.bridge('find_object', { objectName: symbol.objectName, exactMatch: true, maxResults: 10 }).catch(() => undefined);
            const label: string | undefined = (found?.matches as { objectType: string; label?: string }[] | undefined)?.find(m => m.objectType === symbol.objectType)?.label;
            if (label && !label.startsWith('@')) {
                text.appendMarkdown(`\n\n${label.replace(/[\\`*_[\]<>]/g, '\\$&')}`);
            }
            const link = (title: string, commandId: string, args: unknown[]) => `[${title}](command:${commandId}?${encodeURIComponent(JSON.stringify(args))})`;
            const element = { item: { objectType: symbol.objectType, objectName: symbol.objectName } };
            text.appendMarkdown('\n\n' + [
                link('Open designer', 'xpp.openDesigner', [symbol.objectType, symbol.objectName]),
                link('Find references', 'xpp.findReferences', [element]),
                ...(symbol.objectType === 'AxTable' ? [link('Browse data', 'xpp.database.browseTable', [element])] : []),
                ...(['AxClass', 'AxTable', 'AxForm'].includes(symbol.objectType) ? [link('Analyze performance', 'xpp.analyzePerformance', [element])] : []),
            ].join(' · '));
            text.isTrusted = { enabledCommands: ['xpp.openDesigner', 'xpp.findReferences', 'xpp.database.browseTable', 'xpp.analyzePerformance'] };
            return new vscode.Hover(text, range);
        }
        const found = await this.definition(symbol);
        const md = new vscode.MarkdownString(`**${found.owner.objectName}.${symbol.member}** · ${found.signature ? 'method of ' : ''}${found.owner.objectType.replace(/^Ax/, '')} ${found.owner.objectName}`);
        if (found.signature) {
            md.appendCodeblock(found.signature, 'xpp');
        }
        return new vscode.Hover(md, range);
    }

    async provideReferences(document: vscode.TextDocument, position: vscode.Position, context: vscode.ReferenceContext): Promise<vscode.Location[]> {
        const symbol = await this.resolve(document, position);
        if (!symbol) {
            return [];
        }
        const found = await this.definition(symbol);
        const result = await this.server.bridge('where_used', {
            objectType: found.owner.objectType, objectName: found.owner.objectName,
            ...(symbol.member && found.signature ? { memberName: symbol.member } : {}), maxResults: '200',
        });
        if (!result?.success) {
            vscode.window.setStatusBarMessage(`$(warning) ${result?.message ?? 'Where-used failed.'}`, 8000);
            return context.includeDeclaration && found.location ? [found.location] : [];
        }
        const locations: vscode.Location[] = context.includeDeclaration && found.location ? [found.location] : [];
        const texts = new Map<string, string>();
        for (const ref of result.references as { objectType?: string; objectName: string; memberPath: string[]; line: number }[]) {
            if (!ref.objectType || !CodeTypes.has(ref.objectType)) {
                continue;
            }
            const path = ref.memberPath ?? [];
            const uri = path.length > 2 && path[path.length - 2] === 'Methods'
                ? methodUri(ref.objectType, ref.objectName, path)
                : objectUri(ref.objectType, ref.objectName);
            let text = texts.get(uri.toString());
            if (text === undefined) {
                text = await Promise.resolve(vscode.workspace.openTextDocument(uri)).then(d => d.getText(), () => '');
                texts.set(uri.toString(), text);
            }
            const line = ref.line > 0 && text ? compilerLineToDocumentLine(text, ref.line) : 0;
            locations.push(new vscode.Location(uri, new vscode.Position(Math.max(0, line), 0)));
        }
        return locations;
    }

    /** The symbol at the position: a type, or a member with the type it belongs to. */
    async resolve(document: vscode.TextDocument, position: vscode.Position): Promise<Symbol | undefined> {
        const range = document.getWordRangeAtPosition(position, /[A-Za-z_]\w*/);
        if (!range) {
            return undefined;
        }
        const word = document.getText(range);
        const lineText = document.lineAt(position.line).text;
        const before = lineText.slice(0, range.start.character);
        const after = lineText.slice(range.end.character);
        const current = currentObject(document);

        let m: RegExpExecArray | null;
        if ((m = /(\w+)\s*::\s*$/.exec(before))) {
            // Type::member - a static method, an enum value or a table's static find.
            const owner = await this.findType(m[1]);
            return owner ? { ...owner, member: word } : undefined;
        }
        if ((m = /(\w+)\s*(\(\s*\))?\s*\.\s*$/.exec(before))) {
            const qualifier = m[1];
            let owner: Symbol | undefined;
            if (/^(this|element)$/i.test(qualifier)) {
                owner = current;
            } else if (/^super$/i.test(qualifier) && current) {
                owner = await this.baseOf(current);
            } else if (!m[2]) {
                const declared = declaredType(document, qualifier, position);
                owner = declared ? await this.findType(declared) : undefined;
            }
            return owner ? { objectType: owner.objectType, objectName: owner.objectName, member: word } : undefined;
        }
        if (/^\s*\(/.test(after) && !/\bnew\s+$/.test(before) && current && !Keywords.has(word.toLowerCase())) {
            // An unqualified call: a method of this object or a base class, else a global function (class Global).
            const own = await this.definition({ ...current, member: word });
            if (own.signature) {
                return { ...own.owner, member: word };
            }
            const global = await this.definition({ objectType: 'AxClass', objectName: 'Global', member: word });
            if (global.signature) {
                return { objectType: 'AxClass', objectName: 'Global', member: word };
            }
        }
        if (word.length < 3 || Keywords.has(word.toLowerCase())) {
            return undefined;
        }
        return this.findType(word);
    }

    async findType(name: string): Promise<Symbol | undefined> {
        const key = name.toLowerCase();
        if (!this.typeCache.has(key)) {
            const result = await this.server.bridge('find_object', { objectName: name, exactMatch: true, maxResults: 10 }).catch(() => undefined);
            if (!result) {
                return undefined;   // not cached: the server may still be starting
            }
            const matches: Match[] = result.matches ?? [];
            this.typeCache.set(key, NavigableTypes.map(t => matches.find(x => x.objectType === t)).find(x => x) ?? null);
        }
        return this.typeCache.get(key) ?? undefined;
    }

    async baseOf(symbol: Symbol): Promise<Symbol | undefined> {
        if (symbol.objectType !== 'AxClass') {
            return undefined;
        }
        const text = await documentText(objectUri(symbol.objectType, symbol.objectName));
        const base = /\bclass\s+\w+\s+extends\s+(\w+)/i.exec(text)?.[1];
        return base ? { objectType: 'AxClass', objectName: base } : undefined;
    }

    /**
     * Where a symbol is defined: a method's signature line in its object or a base class, else the object itself
     * (fields, enum values and kernel members have no code; the object opens at the line naming them, when there is one).
     */
    async definition(symbol: Symbol): Promise<{ location?: vscode.Location; owner: Symbol; signature?: string }> {
        let owner: Symbol | undefined = { objectType: symbol.objectType, objectName: symbol.objectName };
        if (!symbol.member) {
            return { location: new vscode.Location(objectUri(owner.objectType, owner.objectName), new vscode.Position(0, 0)), owner };
        }
        const member = symbol.member.toLowerCase();
        for (let depth = 0; owner && depth <= MaxBaseDepth; depth++) {
            const uri = objectUri(owner.objectType, owner.objectName);
            const text = await documentText(uri);
            if (!text) {
                break;
            }
            const span = methodSpans(text).find(s => s.name.toLowerCase() === member);
            if (span) {
                const lines = text.split(/\r?\n/);
                const pattern = new RegExp(`\\b${symbol.member}\\s*\\(`, 'i');
                let line = span.startLine;
                while (line < lines.length - 1 && !pattern.test(lines[line])) {
                    line++;
                }
                return { location: new vscode.Location(uri, new vscode.Position(line, Math.max(0, lines[line].search(pattern)))), owner, signature: lines[line].trim() };
            }
            const next = await this.baseOf(owner);
            if (!next) {
                break;
            }
            owner = next;
        }
        // Not a method in code: open the object, at the first line naming the member if any (e.g. an enum value).
        const first = { objectType: symbol.objectType, objectName: symbol.objectName };
        const uri = objectUri(first.objectType, first.objectName);
        const text = await documentText(uri);
        const index = text ? text.split(/\r?\n/).findIndex(l => new RegExp(`\\b${symbol.member}\\b`, 'i').test(l)) : -1;
        return { location: new vscode.Location(uri, new vscode.Position(Math.max(0, index), 0)), owner: first };
    }
}

export async function documentText(uri: vscode.Uri): Promise<string> {
    return Promise.resolve(vscode.workspace.openTextDocument(uri)).then(d => d.getText(), () => '');
}

/** The object a code view document shows, if any. */
export function currentObject(document: vscode.TextDocument): Symbol | undefined {
    if (document.uri.scheme !== XppScheme) {
        return undefined;
    }
    try {
        const { objectType, objectName } = parseObjectUri(document.uri);
        return { objectType, objectName };
    } catch {
        return undefined;
    }
}

/** The declared type of a variable or parameter: the nearest declaration above the position, else a class member. */
export function declaredType(document: vscode.TextDocument, variable: string, position: vscode.Position): string | undefined {
    const text = document.getText();
    const offset = document.offsetAt(position);
    const pattern = new RegExp(`\\b([A-Za-z_]\\w*)(?:\\[\\s*\\d*\\s*\\])?\\s+(?:[A-Za-z_]\\w*\\s*(?:=[^;,()]*)?,\\s*)*${variable}\\b\\s*(?=[;=,)\\[])`, 'g');
    let best: string | undefined;
    let fallback: string | undefined;
    for (let m = pattern.exec(text); m; m = pattern.exec(text)) {
        const type = m[1];
        if (Keywords.has(type.toLowerCase()) && !/^(int|int64|str|real|boolean|date|utcdatetime|anytype|container|guid|var)$/i.test(type)) {
            continue;
        }
        if (m.index < offset) {
            best = type;
        } else {
            fallback ??= type;
        }
    }
    return best ?? fallback;
}

const RecentElementsKey = 'xpp.openObject.recent';

/** Type prefixes in Open Element: "t:cust" finds tables. */
const TypePrefixes: Record<string, string[]> = {
    c: ['AxClass'], t: ['AxTable'], f: ['AxForm'], e: ['AxEdt'], n: ['AxEnum'], q: ['AxQuery'], v: ['AxView'], d: ['AxDataEntityView'],
    m: ['AxMenuItemDisplay', 'AxMenuItemAction', 'AxMenuItemOutput'], s: ['AxSecurityRole', 'AxSecurityDuty', 'AxSecurityPrivilege'],
    x: ['AxTableExtension', 'AxFormExtension', 'AxEnumExtension', 'AxEdtExtension', 'AxDataEntityViewExtension'],
};

type ElementPick = vscode.QuickPickItem & { match?: Match; create?: boolean };

const CodeButton: vscode.QuickInputButton = { iconPath: new vscode.ThemeIcon('code'), tooltip: 'View Code' };
const SideButton: vscode.QuickInputButton = { iconPath: new vscode.ThemeIcon('split-horizontal'), tooltip: 'Open to the Side' };
const AddButton: vscode.QuickInputButton = { iconPath: new vscode.ThemeIcon('add'), tooltip: 'Add to Project' };

/**
 * Open Element: recent elements first; type at least 3 characters (optionally after a type prefix such as "t:") and pick one.
 * Elements open the way an Application Explorer click does.
 */
export async function openObject(server: XppServerClient, context: vscode.ExtensionContext): Promise<void> {
    const pick = vscode.window.createQuickPick<ElementPick>();
    pick.title = 'Open Element';
    pick.placeholder = 'Element name, e.g. CustTable; prefixes: c: class, t: table, f: form, e: EDT, n: enum, m: menu item, q: query, v: view, d: entity, s: security, x: extension';
    pick.matchOnDescription = true;
    const recent = () => context.workspaceState.get<Match[]>(RecentElementsKey, []);
    const toItem = (m: Match): ElementPick => ({
        label: `$(${iconFor(m.objectType)}) ${m.objectName}`,
        description: `${typeName(m.objectType)} · ${m.modelName}${m.isCustom ? ' (yours)' : ''}`,
        buttons: [...CodeTypes.has(m.objectType) && m.objectType !== 'AxClass' ? [CodeButton] : [], SideButton, ...m.isCustom ? [AddButton] : []],
        match: m,
    });
    const showRecent = () => {
        const items = recent().map(toItem);
        pick.items = items.length ? [{ label: 'Recently opened', kind: vscode.QuickPickItemKind.Separator }, ...items] : [];
    };
    showRecent();
    let timer: NodeJS.Timeout | undefined;
    pick.onDidChangeValue(value => {
        clearTimeout(timer);
        const prefixed = /^([a-z]):\s*(.*)$/i.exec(value.trim());
        const types = prefixed ? TypePrefixes[prefixed[1].toLowerCase()] : undefined;
        const name = (types ? prefixed![2] : value).trim();
        if (name.length < 3) {
            if (!value.trim()) {
                showRecent();
            } else {
                pick.items = [];
            }
            return;
        }
        timer = setTimeout(async () => {
            pick.busy = true;
            try {
                const result = await server.bridge('find_object', { objectName: name, exactMatch: false, maxResults: 50, ...types?.length === 1 ? { objectType: types[0] } : {} });
                const matches = ((result.matches ?? []) as Match[]).filter(m => !types || types.includes(m.objectType));
                // Keep VS Code's own filtering from hiding results whose text doesn't contain the prefix.
                pick.items = matches.length
                    ? matches.map(m => ({ ...toItem(m), alwaysShow: true }))
                    : [{ label: `$(add) New Element named '${name}'…`, alwaysShow: true, create: true }];
            } finally {
                pick.busy = false;
            }
        }, 300);
    });
    const remember = (m: Match) => void context.workspaceState.update(RecentElementsKey,
        [m, ...recent().filter(r => r.objectType !== m.objectType || r.objectName !== m.objectName)].slice(0, 15));
    pick.onDidTriggerItemButton(async e => {
        const m = e.item.match;
        if (!m) {
            return;
        }
        remember(m);
        if (e.button === CodeButton) {
            pick.hide();
            await vscode.commands.executeCommand('vscode.open', objectUri(m.objectType, m.objectName));
        } else if (e.button === SideButton) {
            pick.hide();
            await vscode.commands.executeCommand('xpp.openDesignerToSide', { item: { objectType: m.objectType, objectName: m.objectName } });
        } else if (e.button === AddButton) {
            await vscode.commands.executeCommand('xpp.addToProject', { item: { objectType: m.objectType, objectName: m.objectName } });
        }
    });
    pick.onDidAccept(() => {
        const selected = pick.selectedItems[0];
        pick.hide();
        if (selected?.create) {
            void vscode.commands.executeCommand('xpp.newElement');
            return;
        }
        const match = selected?.match;
        if (!match) {
            return;
        }
        remember(match);
        const open = elementOpenCommand(match.objectType, match.objectName);
        void vscode.commands.executeCommand(open.command, ...(open.arguments ?? []));
    });
    pick.onDidHide(() => pick.dispose());
    pick.show();
}

export const XppDocumentSelector: vscode.DocumentSelector = [{ scheme: XppScheme, language: 'xpp' }, { scheme: 'file', language: 'xpp' }, { scheme: 'untitled', language: 'xpp' }];
