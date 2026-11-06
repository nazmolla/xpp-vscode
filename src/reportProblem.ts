import * as os from 'os';
import * as vscode from 'vscode';
import { readActiveEnvironment } from './environment';

const MaxErrors = 20;
const IssuesUrl = 'https://github.com/nazmolla/xpp-vscode/issues/new';
const errors: { time: Date; command: string; message: string; stack?: string }[] = [];

/** Remembers a failed command for Report a Problem (in memory only). */
export function recordError(command: string, error: Error): void {
    errors.unshift({ time: new Date(), command, message: error.message, stack: error.stack });
    errors.splice(MaxErrors);
}

/**
 * Removes what could identify the user, the customer or the code: user folders, host names and URLs, e-mail addresses,
 * GUIDs, tokens and quoted text.
 */
export function redact(text: string): string {
    return text
        .replace(/eyJ[\w-]+\.[\w-]+\.[\w-]+/g, '<token>')
        .replace(/[A-Za-z0-9+/_-]{40,}={0,2}/g, '<secret>')
        .replace(/https?:\/\/[^\s'")]+/gi, '<url>')
        .replace(/\b[\w.+-]+@[\w-]+\.[\w.-]+\b/g, '<email>')
        .replace(/\b[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}\b/gi, '<guid>')
        .replace(/[A-Za-z]:\\Users\\[^\\\r\n]+/gi, '<user folder>')
        .replace(/\b[\w-]+\.(?:crm\d*|operations|sandbox\.operations|cloudax)\.dynamics\.com\b/gi, '<environment>')
        .replace(/(['"])(?:(?!\1).){3,}\1/g, '$1\u2026$1');
}

/**
 * F&O Studio: Report a Problem: a redacted bundle (versions, environment type, the recent failures, usage counts when
 * counting is on) shown first; the user decides whether to open a prefilled GitHub issue. Nothing is sent automatically.
 */
export async function reportProblem(context: vscode.ExtensionContext, usageKey: string, failedCommand?: string): Promise<void> {
    const env = await readActiveEnvironment().catch(() => undefined);
    const usage = vscode.workspace.getConfiguration('xpp').get<boolean>('usageStatistics')
        ? Object.entries(context.globalState.get<Record<string, number>>(usageKey, {})).sort((a, b) => b[1] - a[1]).slice(0, 15) : [];
    const relevant = failedCommand ? errors.filter(e => e.command === failedCommand).slice(0, 3) : errors.slice(0, 5);
    const description = await vscode.window.showInputBox({ title: 'Report a problem (1/2)', prompt: 'What did you do, and what happened? (optional)', ignoreFocusOut: true });
    if (description === undefined) {
        return;
    }
    const body = redact([
        '## What happened', '', description || '_(not described)_', '',
        '## Environment', '',
        `- F&O Studio ${context.extension.packageJSON.version}, VS Code ${vscode.version}, ${os.type()} ${os.release()}`,
        `- Environment: ${env ? `${env.local ? 'cloud-hosted/VHD' : 'unified developer environment'}, application ${env.applicationVersion ?? 'unknown'}` : 'none configured'}`, '',
        '## Recent failures', '',
        ...relevant.length ? relevant.flatMap(e => [`**${e.command}** (${e.time.toLocaleTimeString()}): ${e.message}`, '```', (e.stack ?? '').split('\n').slice(0, 8).join('\n'), '```', '']) : ['_none recorded in this session_', ''],
        ...usage.length ? ['## Most used commands (local counts)', '', ...usage.map(([id, n]) => `- ${id}: ${n}`), ''] : [],
    ].join('\n'));
    const preview = await vscode.workspace.openTextDocument({ language: 'markdown', content: body });
    await vscode.window.showTextDocument(preview, { preview: true });
    const choice = await vscode.window.showInformationMessage('Report a problem (2/2): this is everything the report contains (paths, URLs, names and quoted text are removed). Edit it if you like.',
        { modal: true }, 'Open GitHub Issue', 'Copy to Clipboard');
    const text = preview.getText();
    if (choice === 'Open GitHub Issue') {
        const title = relevant[0] ? `${relevant[0].command}: ${redact(relevant[0].message).slice(0, 80)}` : 'Problem report';
        // Long bodies don't fit in a URL: they go to the clipboard to paste into the issue.
        const fits = text.length < 6000;
        if (!fits) {
            await vscode.env.clipboard.writeText(text);
        }
        const url = `${IssuesUrl}?title=${encodeURIComponent(title)}&body=${encodeURIComponent(fits ? text : 'The report is on the clipboard: paste it here.')}`;
        await vscode.env.openExternal(vscode.Uri.parse(url));
    } else if (choice === 'Copy to Clipboard') {
        await vscode.env.clipboard.writeText(text);
    }
}
