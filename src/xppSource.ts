/**
 * An object's X++ code as one document, laid out like Visual Studio's code editor: the class declaration
 * without its closing brace, the methods inside the class body (stored already indented), then the brace.
 * The compiler numbers lines the same way. Objects without a class declaration fall back to one block per method.
 */

export interface XppSource {
    declaration: string;
    methods: string[];
}

export interface MethodSpan {
    name: string;
    startLine: number;
}

interface ClassLayout {
    /** Offset where the first method starts; the declaration's head and members come before it. */
    firstMethodStart: number;
    /** [start, end) of each method, from its first non-blank line to its closing brace. */
    methods: [number, number][];
    /** Members (fields, macros) that appear after the first method; they belong to the declaration. */
    lateMembers: string[];
    /** Offset of the class's closing brace. */
    classEnd: number;
}

export function composeSource(declaration: string, methods: { name: string; source: string }[]): string {
    let decl = normalizeEol(declaration ?? '').trim();
    let body = methods.map(m => ({ name: m.name, source: normalizeEol(m.source) }));
    if (!decl && body[0]?.name.toLowerCase() === 'classdeclaration') {
        decl = body[0].source.trim();
        body = body.slice(1);
    }
    const close = decl.lastIndexOf('}');
    if (!decl || close < 0) {
        return [decl, ...body.map(m => m.source.trim())].filter(p => p.length > 0).join('\n\n') + '\n';
    }
    let head = decl.slice(0, close).replace(/[ \t]+$/, '');
    if (!head.endsWith('\n')) {
        head += '\n';
    }
    const methodText = body.map(m => m.source.replace(/^\s*\n/, '').replace(/\s+$/, '')).filter(s => s.length > 0);
    return methodText.length === 0
        ? decl + '\n'
        : head + methodText.join('\n\n') + '\n}\n';
}

function normalizeEol(text: string): string {
    return text.replace(/\r\n/g, '\n');
}

/**
 * Splits a document back into the declaration and method sources. originalDeclaration (what was loaded) tells
 * comments that ended the declaration apart from comments that start the first method.
 */
export function splitSource(text: string, originalDeclaration?: string): XppSource {
    const normalized = normalizeEol(text);
    if (normalized.trim() === '') {
        return { declaration: '', methods: [] };
    }
    const layout = classLayout(normalized, declarationTailOf(originalDeclaration));
    if (!layout) {
        const blocks = topLevelBlocks(normalized).map(b => b.trim()).filter(b => b.length > 0);
        if (blocks.length === 0) {
            throw new Error('No code block found.');
        }
        return { declaration: '', methods: blocks };
    }
    if (layout.methods.length === 0) {
        return { declaration: normalized.slice(0, layout.classEnd + 1).trim(), methods: [] };
    }
    let declaration = normalized.slice(0, layout.firstMethodStart).replace(/^\s+/, '');
    for (const member of layout.lateMembers) {
        declaration += member.replace(/^\s*\n/, '').replace(/\s+$/, '') + '\n';
    }
    return {
        declaration: declaration + '}',
        methods: layout.methods.map(([start, end]) => normalized.slice(start, end)),
    };
}

/** Line where each method starts. */
export function methodSpans(text: string): MethodSpan[] {
    const normalized = normalizeEol(text);
    const layout = classLayout(normalized);
    if (!layout) {
        const spans: MethodSpan[] = [];
        let offset = 0;
        for (const block of topLevelBlocks(normalized)) {
            const start = normalized.indexOf(block, offset);
            offset = start + block.length;
            spans.push({ name: methodName(block), startLine: lineOf(normalized, start + (block.length - block.trimStart().length)) });
        }
        return spans;
    }
    return layout.methods.map(([start, end]) => ({ name: methodName(normalized.slice(start, end)), startLine: lineOf(normalized, start) }));
}

/**
 * Converts a compiler line (1-based: the declaration without its closing brace, then each method back to back)
 * to a 0-based line in the document.
 */
export function compilerLineToDocumentLine(text: string, compilerLine: number): number {
    const normalized = normalizeEol(text);
    const layout = classLayout(normalized);
    if (!layout) {
        // One block per method: each block is counted on its own, the first being a form's classDeclaration.
        let compilerStart = 1;
        let first = true;
        let offset = 0;
        for (const block of topLevelBlocks(normalized)) {
            const start = normalized.indexOf(block, offset);
            offset = start + block.length;
            const trimmed = block.trim();
            const docStart = lineOf(normalized, start + (block.length - block.trimStart().length));
            const lines = trimmed.split('\n').length - (first && methodName(trimmed) === 'classDeclaration' ? 1 : 0);
            if (compilerLine < compilerStart + lines) {
                return docStart + (compilerLine - compilerStart);
            }
            compilerStart += lines;
            first = false;
        }
        return Math.max(0, compilerLine - 1);
    }
    const declarationLines = layout.methods.length > 0 ? lineOf(normalized, layout.firstMethodStart) : lineOf(normalized, layout.classEnd);
    if (compilerLine <= declarationLines) {
        return Math.max(0, compilerLine - 1);
    }
    let compilerStart = declarationLines + 1;
    for (const [start, end] of layout.methods) {
        const lines = normalized.slice(start, end).split('\n').length;
        if (compilerLine < compilerStart + lines) {
            return lineOf(normalized, start) + (compilerLine - compilerStart);
        }
        compilerStart += lines;
    }
    return lineOf(normalized, layout.classEnd);
}

/** The inverse of compilerLineToDocumentLine: a 0-based document line to the compiler's 1-based line (blank lines between methods go to the next method). */
export function documentLineToCompilerLine(text: string, documentLine: number): number {
    const normalized = normalizeEol(text);
    const layout = classLayout(normalized);
    if (!layout || layout.methods.length === 0) {
        return documentLine + 1;
    }
    const declarationLines = lineOf(normalized, layout.firstMethodStart);
    if (documentLine < declarationLines) {
        return documentLine + 1;
    }
    let compilerStart = declarationLines + 1;
    for (const [start, end] of layout.methods) {
        const startLine = lineOf(normalized, start);
        const lines = normalized.slice(start, end).split('\n').length;
        if (documentLine < startLine + lines) {
            return compilerStart + Math.max(0, documentLine - startLine);
        }
        compilerStart += lines;
    }
    return compilerStart;
}

/** The element's source as the compiler numbers it: the declaration without its closing brace, then each method back to back. */
export function compilerView(declaration: string, methods: { name: string; source: string }[]): string {
    let decl = normalizeEol(declaration ?? '').trim();
    let body = methods.map(m => normalizeEol(m.source));
    if (!decl && methods[0]?.name.toLowerCase() === 'classdeclaration') {
        decl = body[0].trim();
        body = body.slice(1);
    }
    const lines = decl ? decl.split('\n').slice(0, -1) : [];
    for (const source of body) {
        lines.push(...source.replace(/^\s*\n/, '').replace(/\s+$/, '').split('\n'));
    }
    lines.push('}');
    return lines.join('\r\n') + '\r\n';
}

/** Finds the class body and the methods in it, or undefined if the text doesn't start with a class declaration. */
function classLayout(text: string, declarationTail = ''): ClassLayout | undefined {
    const open = findClassOpen(text);
    if (open < 0) {
        return undefined;
    }
    const methods: [number, number][] = [];
    const lateMembers: string[] = [];
    let depth = 1;
    let segmentStart = open + 1;
    let i = open + 1;
    while (i < text.length) {
        const skipped = skipNonCode(text, i);
        if (skipped > i) {
            i = skipped;
            continue;
        }
        const c = text[i];
        if (depth === 1 && c === '[') {
            i = skipBracketed(text, i);
            continue;
        }
        if (depth === 1 && c === ';') {
            // A comment on the same line after the ';' belongs to the member.
            const newline = text.indexOf('\n', i);
            const rest = text.slice(i + 1, newline < 0 ? text.length : newline);
            const end = /^\s*(\/\/.*)?$/.test(rest) ? (newline < 0 ? text.length : newline) : i + 1;
            if (methods.length > 0) {
                lateMembers.push(text.slice(segmentStart, end));
            }
            segmentStart = end;
            i = end;
            continue;
        } else if (c === '{') {
            depth++;
        } else if (c === '}') {
            depth--;
            if (depth === 1) {
                let start = firstContentLine(text, segmentStart);
                if (methods.length === 0 && declarationTail) {
                    start = skipDeclarationTail(text, start, declarationTail);
                }
                methods.push([start, i + 1]);
                segmentStart = i + 1;
            } else if (depth === 0) {
                if (text.slice(i + 1).trim().length > 0) {
                    throw new Error(`Code after the class's closing brace (line ${lineOf(text, i) + 1}) is outside the class.`);
                }
                const trailing = text.slice(segmentStart, i);
                if (trailing.trim().length > 0) {
                    if (methods.length === 0) {
                        return { firstMethodStart: i, methods, lateMembers, classEnd: i };
                    }
                    lateMembers.push(trailing);
                }
                return { firstMethodStart: methods.length > 0 ? methods[0][0] : i, methods, lateMembers, classEnd: i };
            }
        }
        i++;
    }
    throw new Error('Unbalanced braces: the class is not closed.');
}

/** Offset of the class body's opening brace, if the text begins with a class header rather than a method. */
function findClassOpen(text: string): number {
    let i = 0;
    while (i < text.length) {
        const skipped = skipNonCode(text, i);
        if (skipped > i) {
            i = skipped;
            continue;
        }
        if (text[i] === '[') {
            i = skipBracketed(text, i);
            continue;
        }
        if (text[i] === ';') {
            // using-directives may precede the class header.
            const statementStart = text.lastIndexOf(';', i - 1) + 1;
            if (/^using\s/.test(stripComments(text.slice(statementStart, i)).trim())) {
                i++;
                continue;
            }
            return -1;
        }
        if (text[i] === '(') {
            return -1;
        }
        if (text[i] === '{') {
            return /\bclass\b/.test(stripComments(text.slice(0, i))) ? i : -1;
        }
        i++;
    }
    return -1;
}

function stripComments(text: string): string {
    return text.replace(/\/\*[\s\S]*?\*\//g, '').replace(/\/\/.*$/gm, '').replace(/\[[^\]]*\]/g, '');
}

/**
 * Comments that ended the original declaration (after its last member) come right before the first method in the
 * document; they stay with the declaration rather than becoming part of the method.
 */
function skipDeclarationTail(text: string, contentStart: number, tail: string): number {
    const tailLines = tail.split('\n').map(l => l.trim()).filter(l => l.length > 0);
    let lineStart = contentStart;
    for (const expected of tailLines) {
        while (lineStart < text.length && text.slice(lineStart, text.indexOf('\n', lineStart) < 0 ? text.length : text.indexOf('\n', lineStart)).trim() === '') {
            lineStart = text.indexOf('\n', lineStart) + 1;
        }
        const newline = text.indexOf('\n', lineStart);
        const lineEnd = newline < 0 ? text.length : newline;
        if (text.slice(lineStart, lineEnd).trim() !== expected) {
            return contentStart;
        }
        lineStart = lineEnd + 1;
    }
    return firstContentLine(text, lineStart);
}

/** Comment and macro lines between a declaration's last member (or its opening brace) and its closing brace. */
function declarationTailOf(declaration: string | undefined): string {
    const decl = normalizeEol(declaration ?? '').trim();
    const close = decl.lastIndexOf('}');
    if (close < 0) {
        return '';
    }
    const body = decl.slice(0, close);
    let lastCode = body.indexOf('{');
    for (let i = lastCode + 1; i < body.length; i++) {
        const skipped = skipNonCode(body, i);
        if (skipped > i) {
            i = skipped - 1;
            continue;
        }
        if (body[i] === ';') {
            lastCode = i;
        }
    }
    if (lastCode < 0) {
        return '';
    }
    const newline = body.indexOf('\n', lastCode);
    return newline < 0 ? '' : body.slice(newline + 1).trim();
}

/** Start of the first line of a segment that has content, keeping its indentation. */
function firstContentLine(text: string, start: number): number {
    let i = start;
    let lineStart = start;
    while (i < text.length && /\s/.test(text[i])) {
        if (text[i] === '\n') {
            lineStart = i + 1;
        }
        i++;
    }
    return lineStart;
}

/** Index past a comment or string literal at i, or i itself. */
function skipNonCode(text: string, i: number): number {
    const c = text[i];
    const next = text[i + 1];
    if (c === '/' && next === '/') {
        const end = text.indexOf('\n', i);
        return end < 0 ? text.length : end;
    }
    if (c === '/' && next === '*') {
        const end = text.indexOf('*/', i + 2);
        return end < 0 ? text.length : end + 2;
    }
    if (c === '@' && next === '"') {
        let j = i + 2;
        while (j < text.length && !(text[j] === '"' && text[j + 1] !== '"')) {
            j += text[j] === '"' ? 2 : 1;
        }
        return j + 1;
    }
    if (c === '"' || c === '\'') {
        let j = i + 1;
        while (j < text.length && text[j] !== c && text[j] !== '\n') {
            j += text[j] === '\\' ? 2 : 1;
        }
        return j + 1;
    }
    return i;
}

/** The method name: the identifier before the first '(' after doc comments, [attributes] and #macro lines. */
export function methodName(source: string): string {
    const text = source.replace(/\r\n/g, '\n');
    let i = 0;
    for (;;) {
        while (i < text.length && /\s/.test(text[i])) {
            i++;
        }
        if (text.startsWith('//', i) || (text[i] === '#' && isMacroLine(text, i))) {
            const end = text.indexOf('\n', i);
            i = end < 0 ? text.length : end + 1;
        } else if (text[i] === '#') {
            // A #word on the signature line (e.g. #obsolete) acts as a modifier.
            i++;
            while (i < text.length && /\w/.test(text[i])) {
                i++;
            }
        } else if (text.startsWith('/*', i)) {
            const end = text.indexOf('*/', i + 2);
            i = end < 0 ? text.length : end + 2;
        } else if (text[i] === '[') {
            i = skipBracketed(text, i);
        } else {
            break;
        }
    }
    const paren = text.indexOf('(', i);
    const brace = text.indexOf('{', i);
    if (brace >= 0 && (paren < 0 || brace < paren)) {
        // A class-style header ("public class X extends Y {") is how forms store their declaration.
        return 'classDeclaration';
    }
    if (paren < 0) {
        return '';
    }
    const tokens = text.slice(i, paren).trim().split(/\s+/);
    return tokens[tokens.length - 1] ?? '';
}

/** Index just past the ']' that closes the '[' at start, ignoring brackets inside strings. */
function skipBracketed(text: string, start: number): number {
    let depth = 0;
    for (let i = start; i < text.length; i++) {
        const c = text[i];
        if (c === '"' || c === '\'') {
            i++;
            while (i < text.length && text[i] !== c) {
                i += text[i] === '\\' ? 2 : 1;
            }
        } else if (c === '[') {
            depth++;
        } else if (c === ']' && --depth === 0) {
            return i + 1;
        }
    }
    return text.length;
}

/** Macro directives (#define.X(..), #if, #localmacro, ...) and macro library lines (#SysAdmin) take the whole line. */
function isMacroLine(text: string, start: number): boolean {
    const end = text.indexOf('\n', start);
    const line = text.slice(start, end < 0 ? text.length : end).trim();
    return /^#(define|localmacro|globalmacro|endmacro|if|ifnot|endif|macrolib|undef|globaldefine|localdefine)\b/i.test(line)
        || /^#\w+\.\w+/.test(line)
        || /^#\w+\s*$/.test(line);
}

function lineOf(text: string, index: number): number {
    let line = 0;
    for (let i = 0; i < index; i++) {
        if (text.charCodeAt(i) === 10) {
            line++;
        }
    }
    return line;
}

/** Text chunks that each end with a top-level closing brace; leading comments/attributes stay with the block. */
function topLevelBlocks(text: string): string[] {
    const blocks: string[] = [];
    let depth = 0;
    let blockStart = 0;
    let i = 0;
    while (i < text.length) {
        const c = text[i];
        const next = text[i + 1];
        if (c === '/' && next === '/') {
            const end = text.indexOf('\n', i);
            i = end < 0 ? text.length : end;
            continue;
        }
        if (c === '/' && next === '*') {
            const end = text.indexOf('*/', i + 2);
            i = end < 0 ? text.length : end + 2;
            continue;
        }
        if (c === '@' && next === '"') {
            // Verbatim string: "" is an escaped quote.
            i += 2;
            while (i < text.length && !(text[i] === '"' && text[i + 1] !== '"')) {
                i += text[i] === '"' ? 2 : 1;
            }
            i++;
            continue;
        }
        if (c === '"' || c === '\'') {
            i++;
            while (i < text.length && text[i] !== c && text[i] !== '\n') {
                i += text[i] === '\\' ? 2 : 1;
            }
            i++;
            continue;
        }
        if (c === '{') {
            depth++;
        } else if (c === '}') {
            depth--;
            if (depth === 0) {
                blocks.push(text.slice(blockStart, i + 1));
                blockStart = i + 1;
            } else if (depth < 0) {
                throw new Error(`Unbalanced '}' at line ${lineOf(text, i) + 1}.`);
            }
        }
        i++;
    }
    if (depth !== 0) {
        throw new Error('Unbalanced braces: a block is not closed.');
    }
    if (text.slice(blockStart).trim().length > 0) {
        throw new Error(`Code after the last closing brace (line ${lineOf(text, blockStart) + 1}) is not inside a method.`);
    }
    return blocks;
}
