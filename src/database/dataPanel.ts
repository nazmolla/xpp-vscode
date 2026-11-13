import { randomBytes } from 'crypto';
import { promises as fs } from 'fs';
import * as vscode from 'vscode';
import { XppServerClient } from '../serverClient';
import { stateAttribute } from '../webviewState';
import { DatabaseAccess, DatabaseTarget } from './connection';
import { BrowseFilter, BrowseRequest, DataService, QueryResult, ResultSet } from './dataService';

/** A table's shape from the metadata (bridge action table_data_shape). */
export interface TableShape {
    table: string;
    label?: string;
    tableType: string;
    perCompany: boolean;
    validTimeState: string;
    fields: { name: string; type: string; label?: string; enumType?: string }[];
    enums: Record<string, { extensible: boolean; values: Record<string, string> }>;
}

type RelatedFilter = BrowseFilter;
type PanelMode = { kind: 'table'; table: string; filters?: RelatedFilter[]; company?: string } | { kind: 'query'; sql: string; autoRun?: boolean };
export type { PanelMode };

/**
 * Business data in an editor tab: one table's rows (Browse Data) or the result of a SQL query. Reads are SELECT-only
 * and rolled back; changing data needs an explicit switch and a confirmation for every run.
 */
export class DataPanel {
    private running: vscode.CancellationTokenSource | undefined;
    private lastResult: QueryResult | undefined;

    static openTable(context: vscode.ExtensionContext, server: XppServerClient, data: DataService, access: DatabaseAccess, table: string, filters?: RelatedFilter[], company?: string): void {
        new DataPanel(context, server, data, access, { kind: 'table', table, filters, company });
    }

    static openQuery(context: vscode.ExtensionContext, server: XppServerClient, data: DataService, access: DatabaseAccess, sql = '', autoRun = false): void {
        new DataPanel(context, server, data, access, { kind: 'query', sql, autoRun });
    }

    /** Reopens a data tab after a window reload; queries are not run again by themselves. */
    static revive(context: vscode.ExtensionContext, server: XppServerClient, data: DataService, access: DatabaseAccess, panel: vscode.WebviewPanel, mode: PanelMode): void {
        new DataPanel(context, server, data, access, mode.kind === 'query' ? { ...mode, autoRun: false } : mode, panel);
    }

    private readonly panel: vscode.WebviewPanel;

    private constructor(
        private readonly context: vscode.ExtensionContext,
        private readonly server: XppServerClient,
        private readonly data: DataService,
        private readonly access: DatabaseAccess,
        private readonly mode: PanelMode,
        panel?: vscode.WebviewPanel,
    ) {
        this.panel = panel ?? vscode.window.createWebviewPanel('xppData', mode.kind === 'table' ? `${mode.table} [Data]` : 'SQL query', vscode.ViewColumn.Active, {
            enableScripts: true,
            retainContextWhenHidden: true,
            localResourceRoots: [vscode.Uri.joinPath(context.extensionUri, 'media')],
        });
        this.panel.webview.html = this.html();
        this.panel.webview.onDidReceiveMessage(msg => void this.onMessage(msg));
        this.panel.onDidDispose(() => this.running?.cancel());
    }

    private async onMessage(msg: any): Promise<void> {
        try {
            switch (msg.type) {
                case 'ready':
                    await this.post({ type: 'init', mode: this.mode });
                    if (this.mode.kind === 'table') {
                        await this.loadShape(this.mode.table);
                        await this.browse({ table: this.mode.table, maxRows: 200, filters: this.mode.filters, company: this.mode.company });
                    } else if (this.mode.autoRun && this.mode.sql) {
                        await this.query(this.mode.sql, 500, false);
                    }
                    await this.postTarget();
                    return;
                case 'plan':
                    await this.explain(String(msg.sql ?? ''));
                    return;
                case 'browse':
                    await this.browse(msg.request as BrowseRequest);
                    return;
                case 'query':
                    await this.query(String(msg.sql ?? ''), Number(msg.maxRows) || 500, !!msg.allowWrite);
                    return;
                case 'cancel':
                    this.running?.cancel();
                    return;
                case 'openAsQuery':
                    DataPanel.openQuery(this.context, this.server, this.data, this.access, String(msg.sql ?? ''));
                    return;
                case 'openRelated':
                    DataPanel.openTable(this.context, this.server, this.data, this.access, String(msg.table), msg.filters as RelatedFilter[], msg.company || undefined);
                    return;
                case 'export':
                    await this.exportCsv(Number(msg.index) || 0);
                    return;
                case 'copy':
                    await vscode.env.clipboard.writeText(String(msg.text ?? ''));
                    vscode.window.setStatusBarMessage('Copied', 2000);
                    return;
            }
        } catch (err) {
            await this.post({ type: 'error', message: (err as Error).message });
        }
    }

    private async loadShape(table: string): Promise<void> {
        const shape = await this.server.bridge<TableShape & { success: boolean; message?: string }>('table_data_shape', { table });
        if (!shape.success) {
            throw new Error(shape.message ?? `Table ${table} not found.`);
        }
        if (/TempDB|InMemory/i.test(shape.tableType)) {
            throw new Error(`${shape.table} is a temporary table (${shape.tableType}); its rows exist only while code runs.`);
        }
        await this.post({ type: 'shape', shape });
        // Relations with field constraints: the record inspector follows them to the related rows.
        const erd = await this.server.bridge('table_erd', { tables: [shape.table] }).catch(() => undefined);
        const relations = ((erd?.tables?.[0]?.relations ?? []) as { name: string; relatedTable: string; constraints: { field?: string; relatedField?: string; value?: string }[] }[])
            .map(r => ({ name: r.name, relatedTable: r.relatedTable, constraints: r.constraints.filter(c => c.field && c.relatedField) }))
            .filter(r => r.relatedTable && r.constraints.length);
        await this.post({ type: 'relations', relations });
    }

    private async target(): Promise<DatabaseTarget> {
        const target = await this.access.require();
        if (!target) {
            throw new Error('No database access. Request credentials with F&O Studio: Request Database Credentials, then run again.');
        }
        return target;
    }

    private async postTarget(): Promise<void> {
        const target = await this.access.current().catch(() => undefined);
        await this.post({
            type: 'target',
            text: target ? `${target.database} on ${target.server}${target.credentials ? ` (${target.credentials.role})` : ''}` : 'No database access',
            canWrite: !!target && (!target.cloud || target.credentials?.role === 'Writer'),
        });
    }

    private async browse(request: BrowseRequest): Promise<void> {
        await this.execute(token => this.target().then(target => this.data.browse(target, request, token)));
    }

    private async query(sql: string, maxRows: number, allowWrite: boolean): Promise<void> {
        if (allowWrite) {
            const target = await this.target();
            const ok = await vscode.window.showWarningMessage(`Run this batch with changes on ${target.database}?`, {
                modal: true,
                detail: 'Changes made in SQL skip X++ business logic: insert/update/delete methods, validation, the database log, events, '
                    + 'number sequences and the server\'s caches. The changes are committed and cannot be undone here.',
            }, 'Run and Commit');
            if (ok !== 'Run and Commit') {
                return;
            }
        }
        await this.execute(token => this.target().then(target => this.data.query(target, sql, { maxRows, allowWrite }, token)));
    }

    private async execute(run: (token: vscode.CancellationToken) => Promise<QueryResult>): Promise<void> {
        this.running?.cancel();
        const source = this.running = new vscode.CancellationTokenSource();
        await this.post({ type: 'running' });
        try {
            const result = await run(source.token);
            if (source.token.isCancellationRequested) {
                return;
            }
            this.lastResult = result;
            await this.post({ type: 'result', result });
        } catch (err) {
            await this.post({ type: 'error', message: source.token.isCancellationRequested ? 'Cancelled.' : (err as Error).message });
        } finally {
            if (this.running === source) {
                this.running = undefined;
            }
            source.dispose();
            await this.postTarget();
        }
    }

    private async explain(sql: string): Promise<void> {
        const target = await this.target();
        const plan = await vscode.window.withProgress({ location: vscode.ProgressLocation.Window, title: 'Getting the query plan' }, () => this.data.plan(target, sql));
        const list = (items: string[], empty: string) => items.length ? items.map(i => `- ${i}`).join('\n') : empty;
        const content = [
            '# Query plan (estimated; the query was not run)', '', '```sql', sql.trim(), '```', '',
            `Estimated cost ${plan.estimatedCost}, ${plan.operators} operator(s).`, '',
            '## Where the cost goes', list(plan.notes, 'Nothing stands out.'), '',
            '## Missing indexes', list(plan.missingIndexes, 'None suggested.'), '',
            '## Warnings', list(plan.warnings, 'None.'),
        ].join('\n');
        await vscode.window.showTextDocument(await vscode.workspace.openTextDocument({ language: 'markdown', content }), { viewColumn: vscode.ViewColumn.Beside, preview: true });
    }

    private async exportCsv(index: number): Promise<void> {
        const set = this.lastResult?.resultSets[index];
        if (!set) {
            return;
        }
        const name = this.mode.kind === 'table' ? this.mode.table : 'query';
        const file = await vscode.window.showSaveDialog({ filters: { 'CSV': ['csv'] }, defaultUri: vscode.Uri.file(`${name}.csv`) });
        if (file) {
            await fs.writeFile(file.fsPath, toCsv(set), 'utf8');
            vscode.window.setStatusBarMessage(`Saved ${file.fsPath}`, 4000);
        }
    }

    private post(message: unknown): Thenable<boolean> {
        return this.panel.webview.postMessage(message);
    }

    private html(): string {
        const webview = this.panel.webview;
        const nonce = randomBytes(16).toString('base64');
        const media = (file: string) => webview.asWebviewUri(vscode.Uri.joinPath(this.context.extensionUri, 'media', file));
        return `<!DOCTYPE html>
<html lang="en">
<head>
<meta charset="UTF-8">
<meta http-equiv="Content-Security-Policy" content="default-src 'none'; style-src ${webview.cspSource}; script-src 'nonce-${nonce}';">
<meta name="viewport" content="width=device-width, initial-scale=1.0">
<link href="${media('dataPanel.css')}" rel="stylesheet">
<link href="${media('a11y.css')}" rel="stylesheet">
</head>
<body${stateAttribute(this.mode)}>
<div id="toolbar" role="toolbar"></div>
<div id="filters" hidden></div>
<div id="editor" hidden>
  <textarea id="sql" spellcheck="false" aria-label="SQL query" placeholder="SELECT TOP 100 ACCOUNTNUM, PARTY FROM CUSTTABLE WHERE DATAAREAID = 'usmf'"></textarea>
</div>
<div id="status" role="status" aria-live="polite"></div>
<div id="error" class="error" role="alert" hidden></div>
<div id="results"></div>
<script nonce="${nonce}" src="${media('dataPanel.js')}"></script>
</body>
</html>`;
    }
}

/** RFC 4180 CSV of a result set. */
export function toCsv(set: ResultSet): string {
    const cell = (v: unknown) => {
        const text = v === null || v === undefined ? '' : String(v);
        return /[",\r\n]/.test(text) ? `"${text.replace(/"/g, '""')}"` : text;
    };
    return [set.columns.map(c => cell(c.name)).join(','), ...set.rows.map(r => r.map(cell).join(','))].join('\r\n') + '\r\n';
}
