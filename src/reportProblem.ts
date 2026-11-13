import * as os from 'os';
import * as vscode from 'vscode';
import { readActiveEnvironment } from './environment';

const MaxErrors = 20;
const IssuesUrl = 'https://github.com/nazmolla/xpp-vscode/issues/new';
const IssuesApi = 'https://api.github.com/repos/nazmolla/xpp-vscode/issues';
const errors: { time: Date; command: string; message: string; stack?: string }[] = [];

/** Remembers a failed command for Report a Problem (in memory only). */
export function recordError(command: string, error: Error): void {
    errors.unshift({ time: new Date(), command, message: error.message, stack: error.stack });
    errors.splice(MaxErrors);
}

/**
 * Shows an unexpected error with Report Problem, for failures outside the command wrapper (webview panels, pickers,
 * background tasks). Guidance messages (open a project first...) don't need it.
 */
export function showReportableError(source: string, err: unknown, prefix = ''): void {
    const error = err instanceof Error ? err : new Error(String(err));
    recordError(source, error);
    void vscode.window.showErrorMessage(prefix + (error.message || 'Something went wrong.'), 'Report Problem').then(choice => {
        if (choice === 'Report Problem') {
            void vscode.commands.executeCommand('xpp.reportProblem', source);
        }
    });
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
        { modal: true, detail: 'Create GitHub Issue files it on github.com/nazmolla/xpp-vscode with your GitHub account (VS Code asks you to sign in the first time).' },
        'Create GitHub Issue', 'Open in Browser', 'Copy to Clipboard');
    const text = preview.getText();
    const title = relevant[0] ? `${relevant[0].command}: ${redact(relevant[0].message).slice(0, 80)}` : 'Problem report';
    if (choice === 'Create GitHub Issue') {
        const created = await createIssue(title, text);
        if (created) {
            const open = await vscode.window.showInformationMessage(`Thanks! Issue #${created.number} was created.`, 'Open Issue');
            if (open) {
                await vscode.env.openExternal(vscode.Uri.parse(created.url));
            }
            return;
        }
    }
    if (choice === 'Create GitHub Issue' || choice === 'Open in Browser') {
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

/** Files the issue through the GitHub API with the user's VS Code GitHub session; undefined when that isn't possible. */
async function createIssue(title: string, body: string): Promise<{ number: number; url: string } | undefined> {
    try {
        const session = await vscode.authentication.getSession('github', ['public_repo'], { createIfNone: true });
        const response = await fetch(IssuesApi, {
            method: 'POST',
            headers: {
                Authorization: `Bearer ${session.accessToken}`,
                Accept: 'application/vnd.github+json',
                'Content-Type': 'application/json',
                'User-Agent': 'fno-studio-vscode',
            },
            body: JSON.stringify({ title, body }),
        });
        if (!response.ok) {
            throw new Error(`GitHub answered ${response.status} ${response.statusText}`);
        }
        const issue = await response.json() as { number: number; html_url: string };
        return { number: issue.number, url: issue.html_url };
    } catch (err) {
        void vscode.window.showWarningMessage(`The issue could not be created directly (${(err as Error).message}); it opens in the browser instead.`);
        return undefined;
    }
}
