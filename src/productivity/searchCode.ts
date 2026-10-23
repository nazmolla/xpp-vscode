import * as vscode from 'vscode';
import { CodeTypes, typeLabel } from '../aot';
import { methodUri, objectUri } from '../codeView';
import { methodSpans } from '../xppSource';
import { call, ProductivityContext } from './common';

interface SearchItem {
    objectType: string;
    objectName: string;
    model: string;
    memberPath: string[];
    member: string;
    line: number;
    column: number;
    text: string;
}

interface SearchResult {
    items: SearchItem[];
    truncated: boolean;
    scanned: number;
    ms: number;
}

const OptionsKey = 'xpp.searchCode.options';

interface SearchOptions {
    regex: boolean;
    caseSensitive: boolean;
    last: string;
}

/** X++: Search Code in All Packages: text or regex over the code of every package, then a pick list of matching lines. */
export async function searchCode(ctx: ProductivityContext, state: vscode.Memento): Promise<void> {
    const options = state.get<SearchOptions>(OptionsKey, { regex: false, caseSensitive: false, last: '' });
    const editor = vscode.window.activeTextEditor;
    const selected = editor && !editor.selection.isEmpty && editor.selection.isSingleLine ? editor.document.getText(editor.selection) : '';
    const pattern = await askPattern(options, selected || options.last);
    if (!pattern) {
        return;
    }
    await state.update(OptionsKey, { ...options, last: pattern });
    const result = await vscode.window.withProgress(
        { location: vscode.ProgressLocation.Notification, title: `Searching X++ code for ${pattern}…` },
        () => call<SearchResult>(ctx.languageServer, 'lang', 'search_code', {
            pattern, regex: options.regex, caseSensitive: options.caseSensitive, maxResults: 500,
        }));
    const items = result.items ?? [];
    if (items.length === 0) {
        vscode.window.showInformationMessage(`No X++ code matches ${pattern}.`);
        return;
    }
    const picked = await vscode.window.showQuickPick(items.map(item => ({
        label: `${item.objectName}.${item.member || item.memberPath?.join('/') || ''}:${item.line}`,
        description: item.text?.trim(),
        detail: `${item.model} · ${typeLabel(item.objectType)}${item.memberPath?.length > 2 ? ' · ' + item.memberPath.slice(0, -2).join('/') : ''}`,
        item,
    })), {
        title: `${items.length}${result.truncated ? '+' : ''} match(es) for ${pattern}${result.ms ? ` (${(result.ms / 1000).toFixed(1)} s)` : ''}`,
        matchOnDescription: true, matchOnDetail: true,
    });
    if (picked) {
        await openSearchItem(picked.item);
    }
}

/** An input box with Regex and Match Case toggle buttons, like the editor's find widget. */
function askPattern(options: SearchOptions, value: string): Promise<string | undefined> {
    return new Promise(resolve => {
        const input = vscode.window.createInputBox();
        const update = () => {
            input.title = `Search X++ code in all packages (regex ${options.regex ? 'on' : 'off'}, match case ${options.caseSensitive ? 'on' : 'off'})`;
            input.prompt = 'Text to find. The buttons switch regular expression and match case.';
            input.buttons = [
                { iconPath: new vscode.ThemeIcon('regex'), tooltip: `Regular expression: ${options.regex ? 'on' : 'off'}` },
                { iconPath: new vscode.ThemeIcon('case-sensitive'), tooltip: `Match case: ${options.caseSensitive ? 'on' : 'off'}` },
            ];
        };
        input.value = value;
        input.placeholder = 'e.g. CustTable::find( or salesLine.*qty';
        input.ignoreFocusOut = true;
        update();
        input.onDidTriggerButton(button => {
            if (button.tooltip?.startsWith('Regular')) {
                options.regex = !options.regex;
            } else {
                options.caseSensitive = !options.caseSensitive;
            }
            update();
            input.validationMessage = validate(input.value, options);
        });
        input.onDidChangeValue(v => input.validationMessage = validate(v, options));
        let done = false;
        input.onDidAccept(() => {
            const v = input.value.trim();
            if (v.length < 2 || validate(v, options)) {
                input.validationMessage = validate(v, options) ?? 'Enter at least 2 characters.';
                return;
            }
            done = true;
            resolve(v);
            input.hide();
        });
        input.onDidHide(() => {
            if (!done) {
                resolve(undefined);
            }
            input.dispose();
        });
        input.show();
    });
}

function validate(value: string, options: SearchOptions): string | undefined {
    if (!options.regex) {
        return undefined;
    }
    try {
        new RegExp(value);
        return undefined;
    } catch (err) {
        return 'Invalid regular expression: ' + (err as Error).message;
    }
}

/** Opens a search hit: the method's line in the object's code, a nested method's own document, or the designer. */
export async function openSearchItem(item: SearchItem): Promise<void> {
    const memberPath = item.memberPath ?? [];
    const lineInMethod = Math.max(0, (item.line ?? 1) - 1);
    const column = Math.max(0, item.column ?? 0);
    if (memberPath.length > 2 && memberPath[memberPath.length - 2] === 'Methods') {
        const document = await vscode.workspace.openTextDocument(methodUri(item.objectType, item.objectName, memberPath));
        await reveal(document, lineInMethod, column);
        return;
    }
    if (!CodeTypes.has(item.objectType)) {
        await vscode.commands.executeCommand('xpp.openDesigner', item.objectType, item.objectName);
        return;
    }
    const document = await vscode.workspace.openTextDocument(objectUri(item.objectType, item.objectName));
    const method = item.member || (memberPath[0] === 'Methods' ? memberPath[1] : undefined);
    let line = lineInMethod;
    if (method && method.toLowerCase() !== 'classdeclaration') {
        const span = methodSpans(document.getText()).find(s => s.name.toLowerCase() === method.toLowerCase());
        if (span) {
            line = span.startLine + lineInMethod;
        }
    }
    await reveal(document, line, column);
}

async function reveal(document: vscode.TextDocument, line: number, column: number): Promise<void> {
    const l = Math.min(Math.max(0, line), document.lineCount - 1);
    const position = new vscode.Position(l, Math.min(column, document.lineAt(l).text.length));
    await vscode.window.showTextDocument(document, { selection: new vscode.Range(position, position) });
}
