/**
 * X++ formatter: 4-space indentation, Allman braces (an opening brace on its own line), case labels one level inside
 * switch, statements one level below their case, continuation lines (a statement over several lines, a condition
 * without braces) one level deeper. Text inside strings and comments is never changed; lines that start inside a
 * multi-line string or block comment are kept as they are.
 */

const Indent = '    ';

interface ScanState {
    inBlockComment: boolean;
    inVerbatim: boolean;
}

/** The code of a line with string contents blanked and comments removed; updates the multi-line state. */
function codeOf(line: string, state: ScanState): { code: string; commentAt: number } {
    let code = '';
    let commentAt = -1;
    for (let i = 0; i < line.length; i++) {
        const c = line[i];
        const next = line[i + 1];
        if (state.inBlockComment) {
            if (c === '*' && next === '/') { state.inBlockComment = false; i++; }
            continue;
        }
        if (state.inVerbatim) {
            if (c === '"' && next === '"') { i++; continue; }
            if (c === '"') { state.inVerbatim = false; code += '"'; }
            continue;
        }
        if (c === '/' && next === '/') { commentAt = commentAt < 0 ? i : commentAt; break; }
        if (c === '/' && next === '*') { state.inBlockComment = true; commentAt = commentAt < 0 ? i : commentAt; i++; continue; }
        if (c === '@' && next === '"') { state.inVerbatim = true; code += '@"'; i++; continue; }
        if (c === '"' || c === '\'') {
            const quote = c;
            code += quote;
            let j = i + 1;
            for (; j < line.length; j++) {
                if (line[j] === '\\') { j++; continue; }
                if (line[j] === quote) { break; }
            }
            code += quote;
            i = j;
            continue;
        }
        code += c;
    }
    return { code, commentAt };
}

/** Moves opening braces to their own line and splits "} else" so every brace stands alone (Allman). */
function splitBraces(lines: string[]): string[] {
    const out: string[] = [];
    const state: ScanState = { inBlockComment: false, inVerbatim: false };
    for (const raw of lines) {
        const startsInside = state.inBlockComment || state.inVerbatim;
        const { code } = codeOf(raw, state);
        const trimmed = raw.trim();
        if (startsInside || state.inBlockComment || state.inVerbatim || trimmed.startsWith('//')) {
            out.push(raw);
            continue;
        }
        const codeTrim = code.trim();
        // "} else {", "} catch", "} finally"
        const closeThen = /^\}\s*(else|catch|finally)\b/.exec(trimmed);
        if (closeThen && /^\}\s*(else|catch|finally)\b/.test(codeTrim)) {
            out.push('}');
            const rest = trimmed.slice(trimmed.indexOf(closeThen[1]));
            out.push(...splitBraces([rest]));
            continue;
        }
        // "header {" (possibly followed by a comment): the brace goes to the next line.
        if (codeTrim.endsWith('{') && codeTrim !== '{' && !/^\{.*\}$/.test(codeTrim)) {
            const braceAt = lastCodeBrace(raw);
            if (braceAt > 0) {
                const header = raw.slice(0, braceAt).trimEnd();
                const after = raw.slice(braceAt + 1).trim();
                if (header.trim()) {
                    out.push(header);
                    out.push('{' + (after ? ' ' + after : ''));
                    continue;
                }
            }
        }
        out.push(raw);
    }
    return out;
}

/** Index of the last '{' that is code (not in a string or comment), or -1. */
function lastCodeBrace(line: string): number {
    let found = -1;
    let quote = '';
    for (let i = 0; i < line.length; i++) {
        const c = line[i];
        if (quote) {
            if (c === '\\') { i++; continue; }
            if (c === quote) { quote = ''; }
            continue;
        }
        if (c === '/' && line[i + 1] === '/') { break; }
        if (c === '"' || c === '\'') { quote = c; continue; }
        if (c === '{') { found = i; }
    }
    return found;
}

export interface FormatOptions {
    /** Move braces to their own line (whole-document formatting). Range formatting only reindents. */
    allman: boolean;
}

export function formatXpp(text: string, options: FormatOptions = { allman: true }): string {
    const eol = text.includes('\r\n') ? '\r\n' : '\n';
    let lines = text.replace(/\r\n/g, '\n').split('\n');
    if (options.allman) {
        lines = splitBraces(lines);
    }
    return reindent(lines).join(eol);
}

/** Reindents lines; returns the same number of lines. */
export function reindent(lines: string[]): string[] {
    const out: string[] = [];
    const state: ScanState = { inBlockComment: false, inVerbatim: false };
    const stack: ('block' | 'switch')[] = [];
    let previousCode = '';          // last code line (trimmed), for continuation and switch detection
    let continuation = false;

    for (const raw of lines) {
        const startsInside = state.inBlockComment || state.inVerbatim;
        const trimmed = raw.replace(/\t/g, Indent).trim();
        if (startsInside) {
            const rest = codeOf(raw, state).code.trim();
            out.push(raw.replace(/\s+$/, ''));
            if (!state.inBlockComment && !state.inVerbatim && rest) {
                // The string or comment ended on this line: what follows decides whether the statement continues.
                continuation = !/[;{}:\]]$/.test(rest);
                previousCode = rest;
            }
            continue;
        }
        if (trimmed === '') {
            out.push('');
            continue;
        }
        const { code } = codeOf(trimmed, state);
        const codeTrim = code.trim();
        const isComment = codeTrim === '';

        let leadingCloses = 0;
        while (leadingCloses < codeTrim.length && codeTrim[leadingCloses] === '}') {
            leadingCloses++;
        }
        let depth = Math.max(0, stack.length - leadingCloses);
        const container = stack[stack.length - 1 - leadingCloses];
        if (container === 'switch' && leadingCloses === 0) {
            const isCaseLabel = /^(case\b[\s\S]*:|default\s*:)/.test(codeTrim);
            if (!isCaseLabel) {
                depth += 1;
            }
        }
        const startsBlock = codeTrim.startsWith('{');
        if (!isComment && continuation && !startsBlock && leadingCloses === 0) {
            depth += 1;
        }
        out.push(Indent.repeat(depth) + trimmed);

        if (isComment) {
            continue;
        }
        for (const c of codeTrim) {
            if (c === '{') {
                const header = codeTrim.slice(0, codeTrim.indexOf('{')).trim() || previousCode;
                stack.push(/\bswitch\s*\(/.test(header) ? 'switch' : 'block');
            } else if (c === '}') {
                stack.pop();
            }
        }
        // The next line continues this statement unless it ends one (;), a block ({ }), a label (:), an attribute (])
        // or is a preprocessor line (#...).
        continuation = !/[;{}:\]]$/.test(codeTrim) && !codeTrim.startsWith('#') && !/^(case\b|default\b)/.test(codeTrim);
        previousCode = codeTrim;
    }
    return out;
}
