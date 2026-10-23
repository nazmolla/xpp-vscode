import * as path from 'path';
import * as vscode from 'vscode';

/** Text, or a link that runs an action when clicked in the panel. */
export type Inline = string | { text: string; open: () => unknown };
/** A table cell or list entry: one inline, or several shown comma-separated. */
export type Cell = Inline | Inline[];

/** A security result built once and rendered both as HTML (the panel) and Markdown (copy and export). */
export class SecurityReport {
    private readonly html: string[] = [];
    private readonly md: string[] = [];
    readonly actions: (() => unknown)[] = [];

    constructor(readonly title: string, readonly fileName = 'security') { }

    heading(text: string): this {
        this.html.push(`<h3>${escapeHtml(text)}</h3>`);
        this.md.push(`## ${text}`, '');
        return this;
    }

    text(...parts: Inline[]): this {
        this.html.push(`<p>${parts.map(p => this.inlineHtml(p)).join('')}</p>`);
        this.md.push(parts.map(inlineText).join(''), '');
        return this;
    }

    note(text: string): this {
        this.html.push(`<p class="note">${escapeHtml(text)}</p>`);
        this.md.push(`_${text}_`, '');
        return this;
    }

    list(items: Cell[]): this {
        if (items.length === 0) {
            return this;
        }
        this.html.push(`<ul>${items.map(i => `<li>${this.cellHtml(i)}</li>`).join('')}</ul>`);
        this.md.push(...items.map(i => `- ${cellText(i)}`), '');
        return this;
    }

    table(headers: string[], rows: Cell[][], rowClass?: (row: number) => string | undefined): this {
        if (rows.length === 0) {
            return this;
        }
        const head = `<tr>${headers.map(h => `<th>${escapeHtml(h)}</th>`).join('')}</tr>`;
        const body = rows.map((r, i) => {
            const cls = rowClass?.(i);
            return `<tr${cls ? ` class="${escapeHtml(cls)}"` : ''}>${r.map(c => `<td>${this.cellHtml(c)}</td>`).join('')}</tr>`;
        }).join('');
        this.html.push(`<table>${head}${body}</table>`);
        this.md.push(`| ${headers.map(markdownCell).join(' | ')} |`, `|${headers.map(() => ' --- ').join('|')}|`,
            ...rows.map(r => `| ${r.map(c => markdownCell(cellText(c))).join(' | ')} |`), '');
        return this;
    }

    get markdown(): string {
        return [`# ${this.title}`, '', ...this.md].join('\n').replace(/\n{3,}/g, '\n\n').trimEnd() + '\n';
    }

    get body(): string {
        return this.html.join('\n');
    }

    private cellHtml(cell: Cell): string {
        return Array.isArray(cell) ? cell.map(c => this.inlineHtml(c)).join(', ') : this.inlineHtml(cell);
    }

    private inlineHtml(inline: Inline): string {
        if (typeof inline === 'string') {
            return escapeHtml(inline);
        }
        const id = this.actions.push(inline.open) - 1;
        return `<a href="#" class="link" data-id="${id}">${escapeHtml(inline.text)}</a>`;
    }
}

function inlineText(inline: Inline): string {
    return typeof inline === 'string' ? inline : inline.text;
}

function cellText(cell: Cell): string {
    return Array.isArray(cell) ? cell.map(inlineText).join(', ') : inlineText(cell);
}

function markdownCell(text: string): string {
    return text.replace(/\|/g, '\\|').replace(/\r?\n/g, ' ');
}

export function escapeHtml(text: string): string {
    return String(text).replace(/&/g, '&amp;').replace(/</g, '&lt;').replace(/>/g, '&gt;').replace(/"/g, '&quot;');
}

/** One panel for license analysis, who can access, gaps and segregation of duties results; each result replaces the previous one. */
export class SecurityPanel {
    private static panel: vscode.WebviewPanel | undefined;
    private static report: SecurityReport | undefined;

    static show(report: SecurityReport): void {
        SecurityPanel.report = report;
        let panel = SecurityPanel.panel;
        if (panel) {
            panel.reveal(undefined, false);
        } else {
            panel = vscode.window.createWebviewPanel('xppSecurity', report.title, vscode.ViewColumn.Active, { enableScripts: true, retainContextWhenHidden: true });
            panel.iconPath = new vscode.ThemeIcon('shield');
            panel.onDidDispose(() => {
                SecurityPanel.panel = undefined;
                SecurityPanel.report = undefined;
            });
            panel.webview.onDidReceiveMessage(message => void SecurityPanel.onMessage(message));
            SecurityPanel.panel = panel;
        }
        panel.title = report.title;
        panel.webview.html = SecurityPanel.html(report);
    }

    private static async onMessage(message: { type: string; id?: number }): Promise<void> {
        const report = SecurityPanel.report;
        if (!report) {
            return;
        }
        try {
            switch (message.type) {
                case 'open':
                    await report.actions[message.id ?? -1]?.();
                    break;
                case 'copy':
                    await vscode.env.clipboard.writeText(report.markdown);
                    vscode.window.setStatusBarMessage('Copied as Markdown.', 3000);
                    break;
                case 'export':
                    await exportText(report.markdown, report.fileName, 'md');
                    break;
            }
        } catch (err) {
            vscode.window.showErrorMessage((err as Error).message);
        }
    }

    private static html(report: SecurityReport): string {
        const nonce = Math.random().toString(36).slice(2) + Date.now().toString(36);
        return `<!DOCTYPE html><html><head><meta charset="UTF-8">
<meta http-equiv="Content-Security-Policy" content="default-src 'none'; style-src 'unsafe-inline'; script-src 'nonce-${nonce}';">
<style>
body { font-family: var(--vscode-font-family); font-size: var(--vscode-font-size); color: var(--vscode-foreground); padding: 0 16px 16px; }
header { display: flex; align-items: center; gap: 8px; position: sticky; top: 0; padding: 10px 0; background: var(--vscode-editor-background); border-bottom: 1px solid var(--vscode-panel-border); }
header h2 { flex: 1; margin: 0; font-size: 1.2em; font-weight: 600; }
button { color: var(--vscode-button-secondaryForeground); background: var(--vscode-button-secondaryBackground); border: none; padding: 4px 10px; cursor: pointer; border-radius: 2px; }
button:hover { background: var(--vscode-button-secondaryHoverBackground); }
h3 { margin: 18px 0 6px; font-weight: 600; }
table { border-collapse: collapse; margin: 4px 0 8px; }
th, td { text-align: left; vertical-align: top; padding: 3px 10px; border: 1px solid var(--vscode-panel-border); }
th { background: var(--vscode-editorWidget-background); font-weight: 600; }
tr.Error td:first-child { border-left: 3px solid var(--vscode-editorError-foreground); }
tr.Warning td:first-child { border-left: 3px solid var(--vscode-editorWarning-foreground); }
tr.Info td:first-child { border-left: 3px solid var(--vscode-editorInfo-foreground); }
a.link { color: var(--vscode-textLink-foreground); text-decoration: none; cursor: pointer; }
a.link:hover { text-decoration: underline; }
.note { color: var(--vscode-descriptionForeground); }
ul { margin: 4px 0 8px; padding-left: 22px; }
</style></head><body>
<header><h2>${escapeHtml(report.title)}</h2><button id="copy">Copy as Markdown</button><button id="export">Export…</button></header>
${report.body}
<script nonce="${nonce}">
const vscode = acquireVsCodeApi();
document.getElementById('copy').addEventListener('click', () => vscode.postMessage({ type: 'copy' }));
document.getElementById('export').addEventListener('click', () => vscode.postMessage({ type: 'export' }));
document.querySelectorAll('a.link').forEach(a => a.addEventListener('click', e => {
    e.preventDefault();
    vscode.postMessage({ type: 'open', id: Number(a.dataset.id) });
}));
</script></body></html>`;
    }
}

/** Saves text through a save dialog (UTF-8; CSV gets a byte order mark so Excel reads it right). */
export async function exportText(text: string, baseName: string, extension: 'md' | 'csv'): Promise<vscode.Uri | undefined> {
    const folder = vscode.workspace.workspaceFolders?.[0]?.uri.fsPath;
    const file = `${baseName.replace(/[\\/:*?"<>|\s]+/g, '-')}.${extension}`;
    const target = await vscode.window.showSaveDialog({
        title: 'Export',
        defaultUri: folder ? vscode.Uri.file(path.join(folder, file)) : undefined,
        filters: extension === 'md' ? { Markdown: ['md'] } : { CSV: ['csv'] },
    });
    if (!target) {
        return undefined;
    }
    await vscode.workspace.fs.writeFile(target, Buffer.from((extension === 'csv' ? '\uFEFF' : '') + text, 'utf8'));
    vscode.window.showInformationMessage(`Saved ${path.basename(target.fsPath)}.`, 'Open').then(choice => {
        if (choice) {
            void vscode.commands.executeCommand('vscode.open', target);
        }
    });
    return target;
}
