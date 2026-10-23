import * as vscode from 'vscode';
import { typeLabel } from '../aot';
import { call, ProductivityContext } from './common';
import { openSearchItem } from './searchCode';

interface Usage {
    objectType: string | null;
    objectName: string;
    memberPath: string[];
    member: string | null;
    line: number;
    column: number;
    text: string | null;
    model: string | null;
    label?: string;
}

interface LabelMatch {
    labels: string[];
    text: string;
    whole: boolean;
    usages: Usage[];
    usageCount: number;
}

interface TraceResult {
    labels: LabelMatch[];
    literal: Usage[];
    xrefProblem?: string;
    ms: number;
}

/**
 * X++: Find Message Source: paste an error, warning or info message (values included); the labels whose text matches
 * (placeholders match any value) and the code that uses them, then the line opens.
 */
export async function findMessageSource(ctx: ProductivityContext, entry?: { message?: string }): Promise<void> {
    const editor = vscode.window.activeTextEditor;
    const selected = entry?.message?.trim() || (editor && !editor.selection.isEmpty ? editor.document.getText(editor.selection).trim() : '');
    const message = await vscode.window.showInputBox({
        title: 'Find message source',
        prompt: 'The message as shown in the infolog or error dialog, values included',
        placeHolder: 'e.g. Field Customer group must be filled in.',
        value: selected.length < 400 ? selected : '',
        ignoreFocusOut: true,
        validateInput: v => v.trim().length < 4 ? 'Enter at least 4 characters.' : undefined,
    });
    if (!message) {
        return;
    }
    const result = await vscode.window.withProgress(
        { location: vscode.ProgressLocation.Notification, title: 'Finding where the message comes from…' },
        () => call<TraceResult>(ctx.languageServer, 'lang', 'message_source', { message: message.trim(), maxLabels: 5, maxResults: 40 }));

    type Pick = vscode.QuickPickItem & { usage?: Usage };
    const picks: Pick[] = [];
    for (const match of result.labels ?? []) {
        const ids = match.labels.slice(0, 3).join(', ') + (match.labels.length > 3 ? ` +${match.labels.length - 3}` : '');
        picks.push({ label: `"${match.text}" · ${ids}${match.whole ? '' : ' (part of the message)'}`, kind: vscode.QuickPickItemKind.Separator });
        if (match.usages.length === 0) {
            picks.push({
                label: '$(info) Not used in X++ code',
                detail: match.usageCount < 0 ? `Where-used is unavailable: ${result.xrefProblem ?? 'no cross-reference database'}`
                    : 'The platform (kernel) raises it, or its package was not built with cross-reference data.',
            });
        }
        picks.push(...match.usages.map(usage => toPick(usage)));
    }
    if ((result.literal ?? []).length > 0) {
        picks.push({ label: 'Code with the message as literal text', kind: vscode.QuickPickItemKind.Separator });
        picks.push(...result.literal.map(usage => toPick(usage)));
    }
    if (picks.length === 0) {
        vscode.window.showInformationMessage('No label text or code matches this message.');
        return;
    }
    const picked = await vscode.window.showQuickPick(picks, {
        title: `Where "${message.length > 60 ? message.slice(0, 57) + '…' : message}" comes from`,
        matchOnDescription: true, matchOnDetail: true,
    });
    if (picked?.usage?.objectType) {
        await openSearchItem({ ...picked.usage, objectType: picked.usage.objectType, member: picked.usage.member ?? '', text: picked.usage.text ?? '', model: picked.usage.model ?? '' });
    }
}

function toPick(usage: Usage): vscode.QuickPickItem & { usage: Usage } {
    if (!usage.objectType) {
        return { label: `$(symbol-property) ${usage.objectName}`, description: 'metadata', usage };
    }
    const where = usage.member ? `${usage.objectName}.${usage.member}` : usage.objectName;
    return {
        label: `${where}:${usage.line}`,
        description: usage.text ?? '',
        detail: `${typeLabel(usage.objectType)}${usage.memberPath.length > 2 ? ' · ' + usage.memberPath.slice(0, -2).join('/') : ''}${usage.label ? ' · ' + usage.label : ''}`,
        usage,
    };
}
