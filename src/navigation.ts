import * as vscode from 'vscode';
import { CodeTypes } from './aot';
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
interface Symbol {
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
            const text = new vscode.MarkdownString(`**${symbol.objectName}** ? ${type}${symbol.modelName ? ` in model \`${symbol.modelName}\`` : ''}${symbol.isCustom ? ' (yours)' : ''}`);
            return new vscode.Hover(text, range);
        }
        const found = await this.definition(symbol);
        const md = new vscode.MarkdownString(`**${found.owner.objectName}.${symbol.member}** ? ${found.signature ? 'method of ' : ''}${found.owner.objectType.replace(/^Ax/, '')} ${found.owner.objectName}`);
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
    private async resolve(document: vscode.TextDocument, position: vscode.Position): Promise<Symbol | undefined> {
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

    private async findType(name: string): Promise<Symbol | undefined> {
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

    private async baseOf(symbol: Symbol): Promise<Symbol | undefined> {
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
    private async definition(symbol: Symbol): Promise<{ location?: vscode.Location; owner: Symbol; signature?: string }> {
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

async function documentText(uri: vscode.Uri): Promise<string> {
    return Promise.resolve(vscode.workspace.openTextDocument(uri)).then(d => d.getText(), () => '');
}

/** The object a code view document shows, if any. */
function currentObject(document: vscode.TextDocument): Symbol | undefined {
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

/** Quick pick over metadata: type at least 3 characters, pick an object, open its code or designer. */
export async function openObject(server: XppServerClient): Promise<void> {
    const pick = vscode.window.createQuickPick<vscode.QuickPickItem & { match?: Match }>();
    pick.title = 'Open X++ object';
    pick.placeholder = 'Object name (at least 3 characters), e.g. CustTable';
    let timer: NodeJS.Timeout | undefined;
    pick.onDidChangeValue(value => {
        clearTimeout(timer);
        if (value.trim().length < 3) {
            pick.items = [];
            return;
        }
        timer = setTimeout(async () => {
            pick.busy = true;
            try {
                const result = await server.bridge('find_object', { objectName: value.trim(), exactMatch: false, maxResults: 50 });
                pick.items = (result.matches ?? []).map((m: Match) => ({
                    label: m.objectName,
                    description: m.objectType,
                    detail: m.modelName + (m.isCustom ? ' (yours)' : ''),
                    match: m,
                }));
            } finally {
                pick.busy = false;
            }
        }, 300);
    });
    pick.onDidAccept(() => {
        const match = pick.selectedItems[0]?.match;
        pick.hide();
        if (!match) {
            return;
        }
        if (match.objectType === 'AxForm') {
            void vscode.commands.executeCommand('xpp.openFormDesigner', match.objectName);
        } else {
            void vscode.window.showTextDocument(objectUri(match.objectType, match.objectName));
        }
    });
    pick.onDidHide(() => pick.dispose());
    pick.show();
}

export const XppDocumentSelector: vscode.DocumentSelector = [{ scheme: XppScheme, language: 'xpp' }, { scheme: 'file', language: 'xpp' }];
