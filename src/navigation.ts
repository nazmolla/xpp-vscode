import * as vscode from 'vscode';
import { objectUri, XppScheme } from './codeView';
import { XppServerClient } from './serverClient';

interface Match {
    objectType: string;
    objectName: string;
    modelName: string;
    isCustom: boolean;
}

/** Types an identifier in code most likely refers to, in lookup order. */
const NavigableTypes = ['AxClass', 'AxTable', 'AxEnum', 'AxEdt', 'AxView', 'AxDataEntityView', 'AxMap', 'AxQuery', 'AxForm'];

/** Go to definition and hover for type names, resolved through the metadata (no language server needed). */
export class XppNavigation implements vscode.DefinitionProvider, vscode.HoverProvider {
    private readonly cache = new Map<string, Match | null>();

    constructor(private readonly server: XppServerClient) { }

    async provideDefinition(document: vscode.TextDocument, position: vscode.Position): Promise<vscode.Location | undefined> {
        const match = await this.resolve(document, position);
        return match ? new vscode.Location(objectUri(match.objectType, match.objectName), new vscode.Position(0, 0)) : undefined;
    }

    async provideHover(document: vscode.TextDocument, position: vscode.Position): Promise<vscode.Hover | undefined> {
        const match = await this.resolve(document, position);
        if (!match) {
            return undefined;
        }
        const text = new vscode.MarkdownString(`**${match.objectName}** — ${match.objectType.replace(/^Ax/, '')} in model \`${match.modelName}\`${match.isCustom ? ' (yours)' : ''}`);
        return new vscode.Hover(text, document.getWordRangeAtPosition(position));
    }

    private async resolve(document: vscode.TextDocument, position: vscode.Position): Promise<Match | undefined> {
        const range = document.getWordRangeAtPosition(position, /[A-Za-z_]\w*/);
        if (!range) {
            return undefined;
        }
        const word = document.getText(range);
        if (word.length < 3 || /^(if|else|while|for|select|from|where|join|return|public|private|protected|static|void|int|str|real|boolean|this|super|new|null|true|false|class|extends|implements|internal|final|display|edit|var)$/i.test(word)) {
            return undefined;
        }
        const key = word.toLowerCase();
        if (this.cache.has(key)) {
            return this.cache.get(key) ?? undefined;
        }
        const result = await this.server.bridge('find_object', { objectName: word, exactMatch: true, maxResults: 10 });
        const matches: Match[] = result.matches ?? [];
        const best = NavigableTypes.map(t => matches.find(m => m.objectType === t)).find(m => m) ?? null;
        this.cache.set(key, best);
        return best ?? undefined;
    }
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
