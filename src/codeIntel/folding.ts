import { codeOf, ScanState } from './formatter';

/** A collapsible range, zero-based lines; `end` is the last hidden line. */
export interface Fold {
    start: number;
    end: number;
    kind?: 'comment' | 'region';
}

const RegionStart = /^\s*\/\/\s*#?region\b/i;
const RegionEnd = /^\s*\/\/\s*#?endregion\b/i;
const CaseStart = /^\s*(case\b|default\s*:)/;
const CaseLabelOnly = /^\s*(case\b.*|default\s*):\s*$/;

/**
 * Folding ranges of X++ source: brace blocks (folded from their header line, so an Allman block collapses to
 * "if (x) …"), case sections, block comments, runs of line comments and // region markers.
 */
export function foldingRanges(lines: string[]): Fold[] {
    const folds: Fold[] = [];
    const add = (start: number, end: number, kind?: Fold['kind']) => {
        if (start >= 0 && end > start) {
            folds.push({ start, end, kind });
        }
    };
    const state: ScanState = { inBlockComment: false, inVerbatim: false };
    const braces: number[] = [];
    const cases = new Map<number, number>();
    const regions: number[] = [];
    let blockComment = -1;
    let lineComments = -1;
    let lastCode = -1;
    let lastCodeText = '';

    const closeCase = (depth: number, end: number) => {
        const start = cases.get(depth);
        if (start === undefined) {
            return;
        }
        cases.delete(depth);
        while (end > start && !lines[end].trim()) {
            end--;
        }
        add(start, end);
    };

    for (let i = 0; i < lines.length; i++) {
        const raw = lines[i];
        const startsInComment = state.inBlockComment;
        const startsInString = state.inVerbatim;
        const { code } = codeOf(raw, state);

        const isLineComment = !startsInComment && !startsInString && raw.trim().startsWith('//');
        const isRegionStart = isLineComment && RegionStart.test(raw);
        const isRegionEnd = isLineComment && RegionEnd.test(raw);
        if (isRegionStart) {
            regions.push(i);
        } else if (isRegionEnd) {
            const start = regions.pop();
            if (start !== undefined) {
                add(start, i, 'region');
            }
        }
        if (isLineComment && !isRegionStart && !isRegionEnd) {
            lineComments = lineComments < 0 ? i : lineComments;
        } else {
            add(lineComments, i - 1, 'comment');
            lineComments = -1;
        }

        if (!startsInComment && state.inBlockComment) {
            blockComment = i;
        } else if (startsInComment && !state.inBlockComment) {
            add(blockComment, i, 'comment');
            blockComment = -1;
        }

        if (CaseStart.test(code)) {
            closeCase(braces.length, i - 1);
            if (CaseLabelOnly.test(code)) {
                cases.set(braces.length, i);
            }
        }
        for (let c = 0; c < code.length; c++) {
            if (code[c] === '{') {
                // An opening brace alone on its line belongs to the statement or signature above it.
                const ownLine = !code.slice(0, c).trim();
                braces.push(ownLine && lastCode >= 0 && !/[;{}]$/.test(lastCodeText) ? lastCode : i);
            } else if (code[c] === '}') {
                closeCase(braces.length, i - 1);
                const start = braces.pop();
                if (start !== undefined) {
                    // "} else" or "} else {" stays visible when the block above it is collapsed.
                    add(start, /^[\s;),}]*$/.test(code.slice(c + 1)) ? i : i - 1);
                }
            }
        }
        const trimmed = code.trim();
        if (trimmed) {
            lastCode = i;
            lastCodeText = trimmed;
        }
    }
    add(lineComments, lines.length - 1, 'comment');

    // Outer range first when two start on the same line; the editor keeps the first.
    return folds.sort((a, b) => a.start - b.start || b.end - a.end);
}
