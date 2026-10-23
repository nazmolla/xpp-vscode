import * as vscode from 'vscode';
import { CodeIntelligence, splitParameters } from '../codeIntel/providers';
import { XppServerClient } from '../serverClient';

/** What the productivity commands need from the extension. */
export interface ProductivityContext {
    /** Metadata bridge client (designers, explorer). */
    server: XppServerClient;
    /** Language-service client (xpp/lang). */
    languageServer: XppServerClient;
    log: vscode.OutputChannel;
    codeIntel: CodeIntelligence;
    /** Reloads an open code view document after its object changed. */
    refreshCode: (uri: vscode.Uri) => void;
    refreshTrees: () => void;
}

export type CommandRegistrar = (id: string, handler: (...args: any[]) => unknown) => void;

export interface Element {
    objectType: string;
    objectName: string;
}

export interface FindMatch {
    objectType: string;
    objectName: string;
    modelName: string;
    isCustom: boolean;
}

/** Calls a server action and turns an unknown action or a failure into an error with the server's message. */
export async function call<T = any>(client: XppServerClient, kind: 'bridge' | 'lang', action: string, body: object): Promise<T & { success: boolean; message?: string }> {
    let result: any;
    try {
        result = kind === 'bridge' ? await client.bridge(action, body) : await client.lang(action, body);
    } catch (err) {
        throw new Error(`${action}: ${(err as Error).message}`);
    }
    if (!result || typeof result !== 'object') {
        throw new Error(`${action}: the X++ server returned no result. Update the extension if this action is new.`);
    }
    if (result.success === false) {
        throw new Error(result.message || result.error || `${action} failed.`);
    }
    return result;
}

/** The element with exactly this type and name, if any. */
export async function findExact(server: XppServerClient, objectType: string, objectName: string): Promise<FindMatch | undefined> {
    const result = await server.bridge('find_object', { objectName, objectType, exactMatch: true, maxResults: 5 });
    return ((result?.matches ?? []) as FindMatch[]).find(m => m.objectType === objectType && m.objectName.toLowerCase() === objectName.toLowerCase());
}

/** A valid X++ identifier made of the words of a text: "Customer not found" -> CustomerNotFound. */
export function pascalCase(text: string, maxLength = 60): string {
    const words = text.normalize('NFKD').replace(/[^A-Za-z0-9]+/g, ' ').trim().split(/\s+/).filter(w => w);
    let id = words.map(w => w[0].toUpperCase() + w.slice(1)).join('');
    if (!id || /^\d/.test(id)) {
        id = 'Label' + id;
    }
    return id.slice(0, maxLength);
}

export function lowerFirst(text: string): string {
    return text ? text[0].toLowerCase() + text.slice(1) : text;
}

export function upperFirst(text: string): string {
    return text ? text[0].toUpperCase() + text.slice(1) : text;
}

export const IdentifierPattern = /^[A-Za-z_][A-Za-z0-9_]*$/;

export interface Parameter {
    type: string;
    name: string;
    defaultValue?: string;
}

/** A method header: modifiers, return type, name and parameters. */
export interface MethodSignature {
    attributes: string[];
    modifiers: string[];
    returnType: string;
    name: string;
    parameters: Parameter[];
}

const Modifiers = new Set(['public', 'private', 'protected', 'internal', 'static', 'final', 'abstract', 'server', 'client', 'display', 'edit', 'extern', 'delegate']);

/** Parses a method header such as "public static CustTable find(CustAccount _accountNum, boolean _forUpdate = false)". */
export function parseSignature(header: string): MethodSignature | undefined {
    let text = header.replace(/\/\/[^\n]*/g, ' ').replace(/\/\*[\s\S]*?\*\//g, ' ').replace(/\s+/g, ' ').trim();
    const attributes: string[] = [];
    while (text.startsWith('[')) {
        let depth = 0;
        let end = -1;
        for (let i = 0; i < text.length; i++) {
            if (text[i] === '[') { depth++; }
            if (text[i] === ']' && --depth === 0) { end = i; break; }
        }
        if (end < 0) {
            return undefined;
        }
        attributes.push(text.slice(0, end + 1));
        text = text.slice(end + 1).trim();
    }
    const open = text.indexOf('(');
    const close = text.lastIndexOf(')');
    if (open < 0 || close < open) {
        return undefined;
    }
    const words = text.slice(0, open).trim().split(' ').filter(w => w);
    const name = words.pop();
    if (!name || !IdentifierPattern.test(name)) {
        return undefined;
    }
    const modifiers: string[] = [];
    while (words.length && Modifiers.has(words[0].toLowerCase())) {
        modifiers.push(words.shift()!.toLowerCase());
    }
    const returnType = words.join(' ') || 'void';
    const parameters = splitParameters(text.slice(open + 1, close)).map(p => {
        const eq = p.indexOf('=');
        const declaration = (eq >= 0 ? p.slice(0, eq) : p).trim();
        const parts = declaration.split(' ');
        return {
            name: parts.pop() ?? '',
            type: parts.join(' '),
            defaultValue: eq >= 0 ? p.slice(eq + 1).trim() : undefined,
        };
    }).filter(p => p.name);
    return { attributes, modifiers, returnType, name, parameters };
}

/** The header of a method in source: the text from its first line to the opening brace. */
export function methodHeader(lines: string[], startLine: number): { text: string; endLine: number } | undefined {
    const parts: string[] = [];
    for (let l = startLine; l < lines.length && l < startLine + 40; l++) {
        const brace = lines[l].indexOf('{');
        if (brace >= 0) {
            parts.push(lines[l].slice(0, brace));
            return { text: parts.join('\n'), endLine: l };
        }
        parts.push(lines[l]);
    }
    return undefined;
}

/** Leading comment and attribute lines of a method are part of its span; the header starts at the first other line. */
export function headerStart(lines: string[], startLine: number): number {
    let l = startLine;
    let inComment = false;
    while (l < lines.length) {
        const t = lines[l].trim();
        if (inComment) {
            inComment = !t.includes('*/');
            l++;
            continue;
        }
        if (t === '' || t.startsWith('//') || t.startsWith('[')) {
            l++;
            continue;
        }
        if (t.startsWith('/*')) {
            inComment = !t.includes('*/');
            l++;
            continue;
        }
        break;
    }
    return l;
}

/** Primitive X++ types (values compared with assertEquals rather than assertNotNull). */
export const PrimitiveTypes = new Set(['int', 'int64', 'real', 'str', 'boolean', 'date', 'utcdatetime', 'guid', 'container', 'timeofday', 'recid', 'tableid', 'anytype']);

export function isPrimitive(type: string): boolean {
    return PrimitiveTypes.has(type.toLowerCase());
}

/** Line of the closing brace of a class document (the last non-blank line, when it is '}'). */
export function closingBraceLine(document: vscode.TextDocument): number | undefined {
    for (let l = document.lineCount - 1; l >= 0; l--) {
        const text = document.lineAt(l).text.trim();
        if (text === '') {
            continue;
        }
        return text === '}' ? l : undefined;
    }
    return undefined;
}

/** Inserts method sources (indented one level) before the class's closing brace, one blank line apart. */
export function insertMethodsEdit(document: vscode.TextDocument, methods: string[]): vscode.WorkspaceEdit | undefined {
    const closing = closingBraceLine(document);
    if (closing === undefined) {
        return undefined;
    }
    let previous = closing - 1;
    while (previous >= 0 && document.lineAt(previous).text.trim() === '') {
        previous--;
    }
    const afterOpenBrace = previous >= 0 && document.lineAt(previous).text.trim().endsWith('{');
    const body = methods.map(m => indent(m, '    ')).join('\n\n');
    const edit = new vscode.WorkspaceEdit();
    edit.replace(document.uri, new vscode.Range(new vscode.Position(previous + 1, 0), new vscode.Position(closing, 0)), (afterOpenBrace ? '' : '\n') + body + '\n');
    return edit;
}

export function indent(source: string, pad: string): string {
    return source.split('\n').map(l => l.trim() ? pad + l : '').join('\n');
}

/** Whether a document can be saved (the code view reports standard objects as read-only). */
export async function isWritable(uri: vscode.Uri): Promise<boolean> {
    try {
        const stat = await vscode.workspace.fs.stat(uri);
        return ((stat.permissions ?? 0) & vscode.FilePermission.Readonly) === 0;
    } catch {
        return false;
    }
}
