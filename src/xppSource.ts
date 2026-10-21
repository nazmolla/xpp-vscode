/**
 * An object's X++ code as one document: the declaration block first, then each method.
 * Splitting finds top-level { } blocks, skipping braces inside strings and comments.
 */

export interface XppSource {
    declaration: string;
    methods: string[];
}

export interface MethodSpan {
    name: string;
    startLine: number;
}

export function composeSource(declaration: string, methods: { name: string; source: string }[]): string {
    const parts = [normalize(declaration)];
    for (const m of methods) {
        parts.push(normalize(m.source));
    }
    return parts.filter(p => p.length > 0).join('\n\n') + '\n';
}

function normalize(text: string | undefined): string {
    return (text ?? '').replace(/\r\n/g, '\n').trim();
}

/** Splits a document back into the declaration and method sources; forms keep theirs as a classDeclaration method. */
export function splitSource(text: string, hasDeclaration = true): XppSource {
    const blocks = topLevelBlocks(text.replace(/\r\n/g, '\n')).map(b => b.trim()).filter(b => b.length > 0);
    if (blocks.length === 0) {
        throw new Error('No code block found.');
    }
    return hasDeclaration
        ? { declaration: blocks[0], methods: blocks.slice(1) }
        : { declaration: '', methods: blocks };
}

/** Line where each method starts, for mapping compiler diagnostics into the document. */
export function methodSpans(text: string, hasDeclaration = true): MethodSpan[] {
    const normalized = text.replace(/\r\n/g, '\n');
    const spans: MethodSpan[] = [];
    let offset = 0;
    topLevelBlocks(normalized).forEach((block, index) => {
        const start = normalized.indexOf(block, offset);
        offset = start + block.length;
        if (index === 0 && hasDeclaration) {
            return;
        }
        const leading = block.length - block.trimStart().length;
        spans.push({ name: methodName(block), startLine: lineOf(normalized, start + leading) });
    });
    return spans;
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

/**
 * Converts a compiler line (1-based, counted over the whole object as if the methods sat inside the class body:
 * declaration without its closing brace, then each method back to back) to a 0-based line in the composed document.
 */
export function compilerLineToDocumentLine(text: string, compilerLine: number): number {
    const normalized = text.replace(/\r\n/g, '\n');
    let offset = 0;
    let compilerStart = 1;
    let first = true;
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
