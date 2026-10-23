import * as vscode from 'vscode';
import { elementOpenCommand, typeLabel } from '../aot';
import { elementOf, TreeArg } from '../d365Tools';
import { currentObject } from '../navigation';
import { call, Element, ProductivityContext } from './common';

interface Example {
    objectType: string;
    objectName: string;
    model: string;
    reason: string;
    score: number;
}

/**
 * X++: Find Similar Standard Implementations: standard elements that do what an element does (same base class,
 * interfaces, patterns) or what a free-text query describes, e.g. "batch job with contract".
 */
export async function findExamples(ctx: ProductivityContext, arg?: TreeArg): Promise<void> {
    let element: Element | undefined = elementOf(arg);
    let query: string | undefined;
    if (!element) {
        const editor = vscode.window.activeTextEditor;
        const current = editor ? currentObject(editor.document) : undefined;
        query = await vscode.window.showInputBox({
            title: 'Find similar standard implementations',
            prompt: current
                ? `Describe what you need, or leave empty to find implementations like ${current.objectName}`
                : 'Describe what you need',
            placeHolder: 'e.g. batch job with contract, number sequence on a new table, form with list page pattern',
            ignoreFocusOut: true,
        });
        if (query === undefined) {
            return;
        }
        query = query.trim();
        if (!query) {
            if (!current) {
                return;
            }
            element = { objectType: current.objectType, objectName: current.objectName };
        }
    }
    const subject = element ? element.objectName : `"${query}"`;
    const result = await vscode.window.withProgress(
        { location: vscode.ProgressLocation.Notification, title: `Finding implementations like ${subject}…` },
        () => call<{ items: Example[]; interpretation?: string }>(ctx.languageServer, 'lang', 'find_examples', {
            ...(element ? { objectType: element.objectType, objectName: element.objectName } : { query }),
            maxResults: 50,
        }));
    const items = result.items ?? [];
    if (items.length === 0) {
        vscode.window.showInformationMessage(`No similar standard implementations found for ${subject}.${result.interpretation ? ' ' + result.interpretation : ''}`);
        return;
    }
    const picked = await vscode.window.showQuickPick(items.map(item => ({
        label: item.objectName,
        description: `${typeLabel(item.objectType)} · ${item.model}`,
        detail: item.reason,
        item,
    })), {
        title: result.interpretation || `Implementations like ${subject}`,
        placeHolder: `${items.length} element(s). Select one to open it.`,
        matchOnDescription: true, matchOnDetail: true,
    });
    if (picked) {
        const open = elementOpenCommand(picked.item.objectType, picked.item.objectName);
        await vscode.commands.executeCommand(open.command, ...(open.arguments ?? []));
    }
}
