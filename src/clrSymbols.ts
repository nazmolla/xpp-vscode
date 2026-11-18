import { promises as fs } from 'fs';
import { csharpProjectTree, FileNode } from './solution';

/**
 * The .NET types X++ code can use from the C# projects of the solution: namespaces, types (nested ones too) and their
 * members, read from the C# sources so X++ code can go to them and complete them. A light reader: it finds declarations,
 * it doesn't compile. No vscode dependency.
 */

export interface ClrMember {
    name: string;
    kind: 'method' | 'constructor' | 'property' | 'field' | 'enumValue';
    isStatic: boolean;
    /** The declaration's text up to its body, for hovers and completion details. */
    signature: string;
    file: string;
    line: number;
    column: number;
}

export interface ClrType {
    /** Namespace.Outer.Name (nested types use dots, as X++ code writes them). */
    fullName: string;
    name: string;
    namespace: string;
    kind: string;
    file: string;
    line: number;
    column: number;
    members: ClrMember[];
}

interface Scope {
    kind: 'namespace' | 'type';
    name: string;
    /** Brace depth inside the scope's body. */
    depth: number;
    type?: ClrType;
    bodyStart?: number;
}

const TypeKeyword = /\b(class|struct|interface|enum|record(?:\s+(?:class|struct))?)\s+([A-Za-z_]\w*)/y;
const Token = /\bnamespace\s+([A-Za-z_][\w.]*)\s*([;{])|\b(?:class|struct|interface|enum|record)\b|[{};]|=>/g;
const NotNames = new Set(['if', 'while', 'for', 'foreach', 'switch', 'using', 'lock', 'return', 'new', 'catch', 'when', 'nameof', 'typeof', 'sizeof', 'default', 'base', 'this', 'operator']);

/** Comments, strings and character literals blanked out (same length, newlines kept), so positions stay right. */
export function blankCSharp(text: string): string {
    const out = text.split('');
    const blank = (from: number, to: number) => {
        for (let i = from; i < to && i < out.length; i++) {
            if (out[i] !== '\n' && out[i] !== '\r') {
                out[i] = ' ';
            }
        }
    };
    let i = 0;
    while (i < text.length) {
        const c = text[i];
        const next = text[i + 1];
        if (c === '/' && next === '/') {
            const end = text.indexOf('\n', i);
            blank(i, end < 0 ? text.length : end);
            i = end < 0 ? text.length : end;
        } else if (c === '/' && next === '*') {
            const end = text.indexOf('*/', i + 2);
            blank(i, end < 0 ? text.length : end + 2);
            i = end < 0 ? text.length : end + 2;
        } else if (c === '"' || ((c === '@' || c === '$') && (next === '"' || ((next === '@' || next === '$') && text[i + 2] === '"')))) {
            const start = i;
            let j = text.indexOf('"', i);
            const verbatim = text.slice(i, j).includes('@');
            const raw = text.startsWith('"""', j);
            if (raw) {
                const end = text.indexOf('"""', j + 3);
                j = end < 0 ? text.length : end + 3;
            } else {
                j++;
                while (j < text.length) {
                    if (verbatim && text[j] === '"' && text[j + 1] === '"') {
                        j += 2;
                    } else if (!verbatim && text[j] === '\\') {
                        j += 2;
                    } else if (text[j] === '"' || (!verbatim && text[j] === '\n')) {
                        j++;
                        break;
                    } else {
                        j++;
                    }
                }
            }
            blank(start, j);
            i = j;
        } else if (c === '\'') {
            let j = i + 1;
            while (j < text.length && text[j] !== '\'' && text[j] !== '\n') {
                j += text[j] === '\\' ? 2 : 1;
            }
            blank(i, j + 1);
            i = j + 1;
        } else {
            i++;
        }
    }
    return out.join('');
}

/** The types declared in a C# file, with their members. */
export function parseCSharp(file: string, text: string): ClrType[] {
    const code = blankCSharp(text);
    const lineStarts = [0];
    for (let i = 0; i < code.length; i++) {
        if (code[i] === '\n') {
            lineStarts.push(i + 1);
        }
    }
    const position = (offset: number) => {
        let lo = 0, hi = lineStarts.length - 1;
        while (lo < hi) {
            const mid = (lo + hi + 1) >> 1;
            if (lineStarts[mid] <= offset) {
                lo = mid;
            } else {
                hi = mid - 1;
            }
        }
        return { line: lo, column: offset - lineStarts[lo] };
    };
    const types: ClrType[] = [];
    const scopes: Scope[] = [];
    let fileNamespace = '';
    let depth = 0;
    let pending: ClrType | undefined;
    // Where the current member declaration starts, when directly in a type's body.
    let segment = -1;
    const typeScope = () => {
        const top = scopes[scopes.length - 1];
        return top?.kind === 'type' && top.depth === depth ? top : undefined;
    };
    const member = (owner: ClrType, header: string, headerStart: number, end: '{' | ';' | '=>') => {
        // Attributes and modifiers in front; the header is everything up to the body, initializer or end.
        const cleaned = header.replace(/\[[^\]]*\]/g, m => ' '.repeat(m.length));
        if (/\b(class|struct|interface|enum|record|delegate)\b/.test(cleaned) && !/\(/.test(cleaned.split('=')[0])) {
            return;
        }
        const paren = cleaned.indexOf('(');
        const equals = cleaned.search(/=(?!>)/);
        let match: RegExpExecArray | null = null;
        let kind: ClrMember['kind'];
        if (paren >= 0 && (equals < 0 || paren < equals)) {
            const names = [...cleaned.slice(0, paren).matchAll(/([A-Za-z_]\w*)\s*(?:<[^<>]*>)?\s*$/g)];
            match = names[names.length - 1] ?? null;
            if (!match || NotNames.has(match[1])) {
                return;
            }
            kind = match[1] === owner.name ? 'constructor' : 'method';
        } else {
            const declarator = cleaned.slice(0, equals >= 0 ? equals : cleaned.length).split(',')[0];
            match = /([A-Za-z_]\w*)\s*$/.exec(declarator);
            if (!match || /^\s*$/.test(declarator.slice(0, match.index)) && owner.kind !== 'interface') {
                return;
            }
            kind = end === '{' || end === '=>' ? 'property' : 'field';
        }
        const name = match[1];
        const offset = headerStart + cleaned.lastIndexOf(name, match.index + match[0].length);
        const at = position(offset);
        owner.members.push({
            name, kind,
            isStatic: /\b(static|const)\b/.test(cleaned) || owner.kind === 'enum',
            signature: text.slice(headerStart, headerStart + header.length).replace(/\[[^\]]*\]/g, '').replace(/\s+/g, ' ').trim(),
            file, line: at.line, column: at.column,
        });
    };
    Token.lastIndex = 0;
    for (let m = Token.exec(code); m; m = Token.exec(code)) {
        const token = m[0];
        if (m[1]) {
            if (m[2] === ';') {
                fileNamespace = m[1];
            } else {
                depth++;
                scopes.push({ kind: 'namespace', name: m[1], depth });
            }
            segment = -1;
            continue;
        }
        if (token === '{') {
            const owner = typeScope();
            if (pending) {
                depth++;
                scopes.push({ kind: 'type', name: pending.name, depth, type: pending, bodyStart: m.index + 1 });
                pending = undefined;
                segment = m.index + 1;
                continue;
            }
            if (owner && segment >= 0) {
                member(owner.type!, code.slice(segment, m.index), segment, '{');
                segment = -1;
            }
            depth++;
            continue;
        }
        if (token === '}') {
            const top = scopes[scopes.length - 1];
            if (top && top.depth === depth) {
                scopes.pop();
                if (top.type?.kind === 'enum' && top.bodyStart !== undefined) {
                    enumValues(top.type, code, top.bodyStart, m.index, position);
                }
            }
            depth--;
            if (typeScope()) {
                segment = m.index + 1;
            }
            continue;
        }
        if (token === ';' || token === '=>') {
            const owner = typeScope();
            if (pending && token === ';') {
                // A type without a body (record R(int X);).
                pending = undefined;
            }
            if (owner && segment >= 0) {
                if (owner.type!.kind !== 'enum') {
                    member(owner.type!, code.slice(segment, m.index), segment, token);
                }
                segment = token === ';' ? m.index + 1 : -1;
            } else if (owner && token === ';') {
                segment = m.index + 1;
            }
            continue;
        }
        // A type declaration (class, struct, interface, enum, record).
        TypeKeyword.lastIndex = m.index;
        const decl = TypeKeyword.exec(code);
        if (!decl) {
            continue;
        }
        Token.lastIndex = decl.index + decl[0].length;
        const outer = scopes.filter(s => s.kind === 'type').map(s => s.name);
        const namespace = [fileNamespace, ...scopes.filter(s => s.kind === 'namespace').map(s => s.name)].filter(Boolean).join('.');
        const at = position(decl.index + decl[0].length - decl[2].length);
        pending = {
            fullName: [namespace, ...outer, decl[2]].filter(Boolean).join('.'),
            name: decl[2],
            namespace,
            kind: decl[1].split(/\s+/)[0],
            file, line: at.line, column: at.column,
            members: [],
        };
        types.push(pending);
        segment = -1;
    }
    return types;
}

function enumValues(type: ClrType, code: string, start: number, end: number, position: (offset: number) => { line: number; column: number }): void {
    let offset = start;
    for (const part of code.slice(start, end).split(',')) {
        const m = /^(\s*(?:\[[^\]]*\]\s*)?)([A-Za-z_]\w*)/.exec(part);
        if (m) {
            const at = position(offset + m[1].length);
            type.members.push({ name: m[2], kind: 'enumValue', isStatic: true, signature: `${type.name}.${m[2]}`, file: type.file, line: at.line, column: at.column });
        }
        offset += part.length + 1;
    }
}

/** using-directives of X++ code (using Contoso.Integration;). */
export function xppUsings(text: string): string[] {
    return [...text.matchAll(/^\s*using\s+([A-Za-z_][\w.]*)\s*;/gm)].map(m => m[1]);
}

/** The declared .NET type of a variable (qualified names kept): the nearest declaration before the offset, else any. */
export function clrDeclaredType(text: string, variable: string, offset: number): string | undefined {
    const pattern = new RegExp(`\\b([A-Za-z_][\\w.]*)(?:\\[\\s*\\])?\\s+${variable.replace(/[^\w]/g, '')}\\b\\s*(?=[;=,)])`, 'g');
    let best: string | undefined;
    let fallback: string | undefined;
    for (let m = pattern.exec(text); m; m = pattern.exec(text)) {
        if (/^(return|new|else|case|var)$/i.test(m[1])) {
            continue;
        }
        if (m.index < offset) {
            best = m[1];
        } else {
            fallback ??= m[1];
        }
    }
    return best ?? fallback;
}

function flatten(nodes: FileNode[]): string[] {
    return nodes.flatMap(n => n.children ? flatten(n.children) : [n.path]);
}

/** The types of the C# projects given by projects(), re-read when a source file changes. */
export class ClrIndex {
    private readonly files = new Map<string, { mtime: number; types: ClrType[] }>();

    constructor(private projects: () => Promise<string[]>) { }

    /** Where the C# projects come from (the open X++ project's solution and references, or a test's). */
    useProjects(projects: () => Promise<string[]>): void {
        this.projects = projects;
        this.files.clear();
    }

    async types(): Promise<ClrType[]> {
        const sources = new Set<string>();
        for (const project of await this.projects().catch(() => [] as string[])) {
            for (const file of flatten(await csharpProjectTree(project))) {
                if (file.toLowerCase().endsWith('.cs')) {
                    sources.add(file);
                }
            }
        }
        const all: ClrType[] = [];
        for (const file of sources) {
            const stat = await fs.stat(file).catch(() => undefined);
            if (!stat) {
                continue;
            }
            let cached = this.files.get(file.toLowerCase());
            if (!cached || cached.mtime !== stat.mtimeMs) {
                cached = { mtime: stat.mtimeMs, types: parseCSharp(file, await fs.readFile(file, 'utf8').catch(() => '')) };
                this.files.set(file.toLowerCase(), cached);
            }
            all.push(...cached.types);
        }
        return all;
    }

    /**
     * The type a name in X++ code means (case-insensitive, as X++): the full name, a name under a using-directive's
     * namespace, or a simple name that only one type has.
     */
    async resolve(name: string, usings: string[] = []): Promise<ClrType | undefined> {
        const types = await this.types();
        const key = name.toLowerCase();
        const byFull = (full: string) => types.find(t => t.fullName.toLowerCase() === full);
        const found = byFull(key) ?? usings.map(u => byFull(`${u.toLowerCase()}.${key}`)).find(t => t);
        if (found || key.includes('.')) {
            return found;
        }
        const simple = types.filter(t => t.name.toLowerCase() === key);
        return simple.length === 1 ? simple[0] : undefined;
    }

    /** Namespaces (and their parents) the projects declare. */
    async namespaces(): Promise<Set<string>> {
        const result = new Set<string>();
        for (const t of await this.types()) {
            const parts = t.namespace.split('.').filter(Boolean);
            for (let i = 1; i <= parts.length; i++) {
                result.add(parts.slice(0, i).join('.'));
            }
        }
        return result;
    }
}

/** A member of a type by name (case-insensitive), constructors under the type's name. */
export function memberOf(type: ClrType, name: string): ClrMember | undefined {
    const key = name.toLowerCase();
    return type.members.find(m => m.name.toLowerCase() === key);
}
