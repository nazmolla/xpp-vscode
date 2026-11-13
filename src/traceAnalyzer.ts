import { randomBytes } from 'crypto';
import * as path from 'path';
import * as vscode from 'vscode';
import { openSearchItem } from './productivity/searchCode';
import { XppServerClient } from './serverClient';
import { registerPanelSerializer, stateAttribute } from './webviewState';
import { showReportableError } from './reportProblem';

interface TraceNode { m: string; n: number; incl: number; excl: number; sqlN?: number; sqlMs?: number; c?: TraceNode[]; hidden?: number }

export interface TraceReport {
    success: boolean;
    message?: string;
    file: string;
    durationMs: number;
    events: number;
    xppCalls: number;
    requests: { thread: number; startMs: number; ms: number; method: string; batch: boolean; unfinished: boolean; sqlN: number; sqlMs: number; tree: TraceNode; topExclusive?: { m: string; ms: number; wait: boolean }[] }[];
    methods: { m: string; n: number; incl: number; excl: number; max: number; sqlN: number; sqlMs: number; wait: boolean }[];
    sql: { sql: string; n: number; ms: number; max: number; callers: { m: string; n: number }[] }[];
    sqlTotal: { n: number; ms: number };
    gaps: { thread: number; startMs: number; ms: number; method?: string }[];
}

const SearchTypes = ['AxClass', 'AxTable', 'AxView', 'AxDataEntityView', 'AxMap', 'AxQuery'];

/** F&O Studio: Analyze Trace: reads a performance trace (.etl) with F&O Studio's own parser and shows requests, call trees, hot methods, SQL and pauses. */
export function registerTraceAnalyzer(context: vscode.ExtensionContext, command: (id: string, handler: (...args: any[]) => unknown) => void, server: XppServerClient): void {
    command('xpp.analyzeTrace', async (uri?: vscode.Uri) => {
        const file = uri instanceof vscode.Uri && uri.fsPath.toLowerCase().endsWith('.etl') ? uri
            : (await vscode.window.showOpenDialog({ title: 'Trace to analyze', filters: { 'Event traces': ['etl'] }, canSelectMany: false }))?.[0];
        if (!file) {
            return;
        }
        const report = await vscode.window.withProgress({ location: vscode.ProgressLocation.Notification, title: `Reading ${path.basename(file.fsPath)}…` },
            () => server.trace<TraceReport>({ path: file.fsPath }));
        if (!report.success) {
            throw new Error(report.message ?? 'The trace could not be read.');
        }
        TracePanel.show(context, server, report);
    });
    registerPanelSerializer<{ file: string }>(context, 'xppTrace', async (panel, state) => {
        const report = await server.trace<TraceReport>({ path: state.file });
        if (!report.success) {
            throw new Error(report.message);
        }
        TracePanel.revive(panel, context, server, report);
    });
}

class TracePanel {
    private constructor(private readonly panel: vscode.WebviewPanel, private readonly context: vscode.ExtensionContext, private readonly server: XppServerClient, report: TraceReport) {
        panel.webview.html = this.html(report.file);
        panel.webview.onDidReceiveMessage(message => this.onMessage(message, report).catch(err => showReportableError('tracePanel', err)));
    }

    static show(context: vscode.ExtensionContext, server: XppServerClient, report: TraceReport): void {
        const panel = vscode.window.createWebviewPanel('xppTrace', `${path.basename(report.file)} [Trace]`, vscode.ViewColumn.Active, {
            enableScripts: true,
            retainContextWhenHidden: true,
            localResourceRoots: [vscode.Uri.joinPath(context.extensionUri, 'media')],
        });
        new TracePanel(panel, context, server, report);
    }

    static revive(panel: vscode.WebviewPanel, context: vscode.ExtensionContext, server: XppServerClient, report: TraceReport): void {
        new TracePanel(panel, context, server, report);
    }

    private async onMessage(message: { type: string; method?: string; text?: string }, report: TraceReport): Promise<void> {
        switch (message.type) {
            case 'ready':
                await this.panel.webview.postMessage({ type: 'report', report });
                break;
            case 'open':
                await this.open(message.method ?? '');
                break;
            case 'copy':
                await vscode.env.clipboard.writeText(message.text ?? '');
                vscode.window.setStatusBarMessage('Copied', 2000);
                break;
        }
    }

    /** Opens a traced method ("Class::method", "Forms.Form+DataSource::method") in the code view. */
    private async open(traced: string): Promise<void> {
        const target = parseTracedMethod(traced);
        if (!target) {
            return;
        }
        let objectType = target.objectType;
        if (!objectType) {
            const found = await this.server.bridge('find_object', { objectName: target.objectName, exactMatch: true, maxResults: 10 });
            const types = ((found?.matches ?? []) as { objectType: string }[]).map(m => m.objectType);
            objectType = SearchTypes.find(t => types.includes(t));
        }
        if (!objectType) {
            vscode.window.showInformationMessage(`${target.objectName} is not in the metadata (a kernel class); it has no X++ code to open.`);
            return;
        }
        await openSearchItem({ objectType, objectName: target.objectName, model: '', memberPath: [], member: target.member, line: 1, column: 0, text: '' });
    }

    private static body(): string {
        return `<header class="hdr">
  <div class="hdr-title"><span class="codicon codicon-pulse" aria-hidden="true"></span><h1 id="file">Reading trace…</h1></div>
  <div id="stats" class="stats" role="group" aria-label="Summary"></div>
</header>
<section id="overview" class="overview" aria-label="Timeline">
  <div class="overview-head">
    <button id="tlToggle" class="section-toggle" aria-expanded="true" aria-controls="timeline"><span class="codicon codicon-chevron-down" aria-hidden="true"></span>Timeline</button>
    <span class="legend" aria-hidden="true"><span class="sw sw-ui"></span>Request<span class="sw sw-batch"></span>Batch task<span class="sw sw-pause"></span>Pause<span class="sw sw-sel"></span>Selected</span>
    <span class="spacer"></span>
    <label class="check"><input id="tlBatch" type="checkbox"><span>Show batch tasks</span></label>
  </div>
  <div id="timeline" class="timeline"></div>
</section>
<div class="toolbar">
  <div id="tabs" class="tabs" role="tablist" aria-label="Views"></div>
  <div class="search"><span class="codicon codicon-search" aria-hidden="true"></span><input id="filter" type="text" placeholder="Filter methods and SQL" aria-label="Filter methods and SQL" spellcheck="false"><button id="filterClear" class="icon-btn" title="Clear filter (Escape)" aria-label="Clear filter" hidden><span class="codicon codicon-close" aria-hidden="true"></span></button></div>
</div>
<main id="panel" class="panel" role="tabpanel" tabindex="-1"><div class="empty"><span class="codicon codicon-loading codicon-modifier-spin empty-icon" aria-hidden="true"></span><div class="empty-title">Reading trace…</div></div></main>`;
    }

    private html(file: string): string {
        const webview = this.panel.webview;
        const nonce = randomBytes(16).toString('base64');
        const media = (file: string) => webview.asWebviewUri(vscode.Uri.joinPath(this.context.extensionUri, 'media', file));
        return `<!DOCTYPE html>
<html lang="en">
<head>
<meta charset="UTF-8">
<meta http-equiv="Content-Security-Policy" content="default-src 'none'; style-src ${webview.cspSource}; font-src ${webview.cspSource}; script-src 'nonce-${nonce}';">
<meta name="viewport" content="width=device-width, initial-scale=1.0">
<link href="${media('codicons/codicon.css')}" rel="stylesheet">
<link href="${media('trace.css')}" rel="stylesheet">
<link href="${media('a11y.css')}" rel="stylesheet">
</head>
<body class="loading"${stateAttribute({ file })}>
${TracePanel.body()}
<script nonce="${nonce}" src="${media('trace.js')}"></script>
</body>
</html>`;
    }
}

/** "Forms.PurchTable+PurchLine::linkActive" to the form PurchTable and method linkActive; classes and tables have no type. */
export function parseTracedMethod(traced: string): { objectType?: string; objectName: string; member: string } | undefined {
    const match = /^(?:(Forms)\.)?([A-Za-z_]\w*)(?:\+[\w+]+)?::(\w+)$/.exec(traced);
    if (!match) {
        return undefined;
    }
    return { objectType: match[1] ? 'AxForm' : undefined, objectName: match[2], member: match[3] };
}
