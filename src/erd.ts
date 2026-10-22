import { randomBytes } from 'crypto';
import * as vscode from 'vscode';
import { XppServerClient } from './serverClient';

export type ErdDirection = 'outgoing' | 'incoming' | 'both';

interface ErdField { name: string; type?: string }
interface ErdConstraint { kind: string; field?: string; relatedField?: string; value?: string }
interface ErdRelation { name: string; relatedTable: string; cardinality?: string; relatedCardinality?: string; relationshipType?: string; constraints: ErdConstraint[] }
interface ErdTable {
    name: string; label?: string; model?: string; isCustom?: boolean; tableGroup?: string;
    primaryKey: string[]; fields: ErdField[]; relations: ErdRelation[];
}

export interface ErdOptions { depth: number; direction: ErdDirection; allFields: boolean }

/** Tables shown at most: a diagram beyond this is unreadable and slow to collect. */
const MaxTables = 60;

/**
 * Entity relationship diagram of a table and its related tables (Visual Studio has none for F&O tables). Tables are
 * collected level by level through their relations (outgoing), the relations of other tables to them (incoming) or both.
 */
export class ErdPanel {
    private static readonly panels = new Map<string, ErdPanel>();

    static async show(server: XppServerClient, table: string): Promise<void> {
        const config = vscode.workspace.getConfiguration('xpp.erd');
        const options: ErdOptions = {
            depth: Math.min(5, Math.max(1, config.get<number>('depth', 1))),
            direction: config.get<ErdDirection>('direction', 'outgoing'),
            allFields: config.get<string>('fields', 'keys') === 'all',
        };
        const existing = ErdPanel.panels.get(table.toLowerCase());
        if (existing) {
            existing.panel.reveal();
            return;
        }
        const panel = vscode.window.createWebviewPanel('xppErd', `ERD: ${table}`, vscode.ViewColumn.Active, { enableScripts: true, retainContextWhenHidden: true });
        const erd = new ErdPanel(server, panel, table, options);
        ErdPanel.panels.set(table.toLowerCase(), erd);
        await erd.load();
    }

    private readonly details = new Map<string, ErdTable | null>();
    private readonly incoming = new Map<string, string[]>();
    private mermaid = '';
    private last: object | undefined;

    private constructor(
        private readonly server: XppServerClient,
        private readonly panel: vscode.WebviewPanel,
        private table: string,
        private options: ErdOptions,
    ) {
        panel.webview.html = this.html();
        panel.onDidDispose(() => ErdPanel.panels.delete(this.table.toLowerCase()));
        panel.webview.onDidReceiveMessage(message => this.onMessage(message).catch(err => vscode.window.showErrorMessage((err as Error).message)));
    }

    private async onMessage(message: any): Promise<void> {
        switch (message.command) {
            case 'ready':
                if (this.last) {
                    this.panel.webview.postMessage(this.last);
                }
                return;
            case 'options':
                this.options = { ...this.options, ...message.options };
                return this.load();
            case 'focus':
                ErdPanel.panels.delete(this.table.toLowerCase());
                this.table = message.table;
                ErdPanel.panels.set(this.table.toLowerCase(), this);
                this.panel.title = `ERD: ${this.table}`;
                return this.load();
            case 'open':
                await vscode.commands.executeCommand('xpp.openDesigner', 'AxTable', message.table);
                return;
            case 'copyMermaid':
                await vscode.env.clipboard.writeText(this.mermaid);
                vscode.window.setStatusBarMessage('$(clippy) Mermaid ER diagram copied', 4000);
                return;
            case 'saveMermaid':
                return this.save(`${this.table}-erd.mmd`, { Mermaid: ['mmd', 'md'] }, uri => uri.path.endsWith('.md') ? '```mermaid\n' + this.mermaid + '```\n' : this.mermaid);
            case 'saveSvg':
                return this.save(`${this.table}-erd.svg`, { SVG: ['svg'] }, () => message.svg);
        }
    }

    private async save(name: string, filters: Record<string, string[]>, content: (uri: vscode.Uri) => string): Promise<void> {
        const folder = vscode.workspace.workspaceFolders?.[0]?.uri;
        const uri = await vscode.window.showSaveDialog({ defaultUri: folder ? vscode.Uri.joinPath(folder, name) : undefined, filters });
        if (uri) {
            await vscode.workspace.fs.writeFile(uri, Buffer.from(content(uri), 'utf8'));
            vscode.window.setStatusBarMessage(`$(check) Saved ${uri.fsPath}`, 4000);
        }
    }

    private async load(): Promise<void> {
        this.panel.webview.postMessage({ type: 'busy', text: `Collecting relations of ${this.table}…` });
        try {
            const data = await this.collect();
            this.mermaid = toMermaid(data.tables, data.edges, data.shownFields);
            this.last = { type: 'diagram', root: this.table, options: this.options, ...data };
            this.panel.webview.postMessage(this.last);
        } catch (err) {
            this.panel.webview.postMessage({ type: 'error', message: (err as Error).message });
        }
    }

    private async fetch(names: string[]): Promise<void> {
        const wanted = names.filter(n => !this.details.has(n.toLowerCase()));
        for (let i = 0; i < wanted.length; i += 40) {
            const batch = wanted.slice(i, i + 40);
            const result = await this.server.bridge('table_erd', { tables: batch });
            if (!result.success) {
                throw new Error(result.message);
            }
            for (const t of result.tables as ErdTable[]) {
                this.details.set(t.name.toLowerCase(), t);
            }
            for (const n of batch) {
                if (!this.details.has(n.toLowerCase())) {
                    this.details.set(n.toLowerCase(), null);
                }
            }
        }
    }

    private async incomingOf(name: string): Promise<string[]> {
        let list = this.incoming.get(name.toLowerCase());
        if (!list) {
            const result = await this.server.bridge('incoming_relations', { table: name });
            list = result.success ? result.tables as string[] : [];
            this.incoming.set(name.toLowerCase(), list);
        }
        return list;
    }

    private async collect() {
        const levels = new Map<string, number>([[this.table.toLowerCase(), 0]]);
        const names = new Map<string, string>([[this.table.toLowerCase(), this.table]]);
        let frontier = [this.table];
        let truncated = false;
        // Tables whose relations (out) or whose referring tables (in) were followed: only those relations are drawn.
        const expandedOut = new Set<string>();
        const expandedIn = new Set<string>();
        const add = (name: string, level: number, next: string[]): void => {
            const key = name.toLowerCase();
            if (levels.has(key)) {
                return;
            }
            if (levels.size >= MaxTables) {
                truncated = true;
                return;
            }
            levels.set(key, level);
            names.set(key, name);
            next.push(name);
        };
        await this.fetch(frontier);
        if (!this.details.get(this.table.toLowerCase())) {
            throw new Error(`Table '${this.table}' not found.`);
        }
        for (let depth = 1; depth <= this.options.depth && frontier.length > 0; depth++) {
            const next: string[] = [];
            for (const name of frontier) {
                const level = levels.get(name.toLowerCase()) ?? 0;
                const t = this.details.get(name.toLowerCase());
                if (this.options.direction !== 'incoming' && level >= 0 && t) {
                    expandedOut.add(name.toLowerCase());
                    for (const r of t.relations) {
                        if (r.relatedTable) {
                            add(r.relatedTable, depth, next);
                        }
                    }
                }
                if (this.options.direction !== 'outgoing' && level <= 0) {
                    expandedIn.add(name.toLowerCase());
                    for (const source of await this.incomingOf(name)) {
                        add(source, -depth, next);
                    }
                }
            }
            await this.fetch(next);
            frontier = next.filter(n => this.details.get(n.toLowerCase()));
        }

        const tables = [...levels.keys()].map(k => this.details.get(k)).filter((t): t is ErdTable => !!t);
        const inSet = new Map(tables.map(t => [t.name.toLowerCase(), t]));
        const edges: { from: string; to: string; relation: string; cardinality?: string; relatedCardinality?: string; fields: [string, string][] }[] = [];
        for (const t of tables) {
            for (const r of t.relations) {
                const target = inSet.get((r.relatedTable ?? '').toLowerCase());
                if (!target || !(expandedOut.has(t.name.toLowerCase()) || expandedIn.has(target.name.toLowerCase()))) {
                    continue;
                }
                edges.push({
                    from: t.name, to: target.name, relation: r.name, cardinality: r.cardinality, relatedCardinality: r.relatedCardinality,
                    fields: r.constraints.filter(c => c.field && c.relatedField).map(c => [c.field!, c.relatedField!] as [string, string]),
                });
            }
        }
        // Keys only: primary key and the fields the drawn relations use.
        const shownFields = new Map<string, Set<string>>(tables.map(t => [t.name, new Set(t.primaryKey.map(f => f.toLowerCase()))]));
        for (const e of edges) {
            for (const [f, rf] of e.fields) {
                shownFields.get(e.from)?.add(f.toLowerCase());
                shownFields.get(e.to)?.add(rf.toLowerCase());
            }
        }
        const view = tables.map(t => ({
            name: t.name, label: t.label, model: t.model, isCustom: t.isCustom, level: levels.get(t.name.toLowerCase()) ?? 0,
            primaryKey: t.primaryKey,
            fields: (this.options.allFields ? t.fields : t.fields.filter(f => shownFields.get(t.name)?.has(f.name.toLowerCase())))
                .concat(t.primaryKey.includes('RecId') && !this.options.allFields ? [{ name: 'RecId', type: 'RecId' }] : []),
        }));
        return { tables: view, edges, truncated, max: MaxTables, shownFields: Object.fromEntries([...shownFields].map(([k, v]) => [k, [...v]])) };
    }

    private html(): string {
        const nonce = randomBytes(16).toString('base64');
        return `<!DOCTYPE html><html><head><meta charset="UTF-8">
<meta http-equiv="Content-Security-Policy" content="default-src 'none'; style-src 'unsafe-inline'; script-src 'nonce-${nonce}';">
<style>
body { font-family: var(--vscode-font-family); font-size: 12px; color: var(--vscode-foreground); margin: 0; }
#bar { position: sticky; top: 0; z-index: 2; display: flex; gap: 10px; align-items: center; flex-wrap: wrap; padding: 6px 10px;
  background: var(--vscode-editor-background); border-bottom: 1px solid var(--vscode-panel-border); }
#bar label { display: flex; gap: 4px; align-items: center; }
select, button { font: inherit; color: var(--vscode-dropdown-foreground); background: var(--vscode-dropdown-background); border: 1px solid var(--vscode-dropdown-border); padding: 2px 6px; }
button { cursor: pointer; color: var(--vscode-button-secondaryForeground); background: var(--vscode-button-secondaryBackground); border: none; padding: 3px 8px; }
button:hover { background: var(--vscode-button-secondaryHoverBackground); }
#status { color: var(--vscode-descriptionForeground); }
#canvas { padding: 16px; overflow: auto; }
.box rect.body { fill: var(--vscode-editorWidget-background); stroke: var(--vscode-panel-border); }
.box rect.head { fill: var(--vscode-badge-background); }
.box.root rect.head { fill: var(--vscode-button-background); }
.box.custom rect.body { stroke: var(--vscode-charts-green); stroke-width: 2; }
.box text { fill: var(--vscode-foreground); font-family: var(--vscode-font-family); font-size: 12px; }
.box text.title { fill: var(--vscode-badge-foreground); font-weight: 600; cursor: pointer; }
.box.root text.title { fill: var(--vscode-button-foreground); }
.box text.type { fill: var(--vscode-descriptionForeground); }
.box text.pk { font-weight: 600; }
.edge { fill: none; stroke: var(--vscode-charts-blue); stroke-width: 1.3; opacity: .75; }
.edge:hover { stroke-width: 3; opacity: 1; }
.card { fill: var(--vscode-descriptionForeground); font-size: 10px; }
</style></head><body>
<div id="bar">
  <label>Levels <select id="depth">${[1, 2, 3, 4, 5].map(n => `<option>${n}</option>`).join('')}</select></label>
  <label>Relations <select id="direction"><option value="outgoing">Outgoing (tables it refers to)</option><option value="incoming">Incoming (tables referring to it)</option><option value="both">Both</option></select></label>
  <label>Fields <select id="fields"><option value="keys">Keys and relation fields</option><option value="all">All fields</option></select></label>
  <button id="svg">Export SVG…</button><button id="mmd">Export Mermaid…</button><button id="copy">Copy Mermaid</button>
  <span id="status"></span>
</div>
<div id="canvas"></div>
<script nonce="${nonce}">${ErdScript}</script>
</body></html>`;
    }
}

function toMermaid(tables: { name: string; fields: ErdField[]; primaryKey: string[] }[], edges: { from: string; to: string; relation: string; cardinality?: string; relatedCardinality?: string; fields: [string, string][] }[], _shown: unknown): string {
    const left: Record<string, string> = { ZeroMore: '}o', OneMore: '}|', ZeroOne: '|o', ExactlyOne: '||' };
    const right: Record<string, string> = { ZeroOne: 'o|', ExactlyOne: '||', ZeroMore: 'o{', OneMore: '|{' };
    const lines = ['erDiagram'];
    for (const e of edges) {
        lines.push(`    ${e.from} ${left[e.cardinality ?? ''] ?? '}o'}--${right[e.relatedCardinality ?? ''] ?? '||'} ${e.to} : "${e.relation}"`);
    }
    const fkOf = new Map<string, Set<string>>();
    for (const e of edges) {
        for (const [f] of e.fields) {
            fkOf.set(e.from, (fkOf.get(e.from) ?? new Set()).add(f.toLowerCase()));
        }
    }
    for (const t of tables) {
        lines.push(`    ${t.name} {`);
        for (const f of t.fields) {
            const keys = [t.primaryKey.some(k => k.toLowerCase() === f.name.toLowerCase()) ? 'PK' : '', fkOf.get(t.name)?.has(f.name.toLowerCase()) ? 'FK' : ''].filter(Boolean).join(', ');
            lines.push(`        ${(f.type || 'field').replace(/[^\w]/g, '_')} ${f.name}${keys ? ' ' + keys : ''}`);
        }
        lines.push('    }');
    }
    return lines.join('\n') + '\n';
}

/** The webview: layout in columns by level (incoming left, outgoing right), boxes and relation curves as SVG. */
const ErdScript = String.raw`
const vscode = acquireVsCodeApi();
const canvas = document.getElementById('canvas');
const status = document.getElementById('status');
const $ = id => document.getElementById(id);
let current = null;
const sendOptions = () => vscode.postMessage({ command: 'options', options: { depth: Number($('depth').value), direction: $('direction').value, allFields: $('fields').value === 'all' } });
['depth', 'direction', 'fields'].forEach(id => $(id).addEventListener('change', sendOptions));
$('copy').addEventListener('click', () => vscode.postMessage({ command: 'copyMermaid' }));
$('mmd').addEventListener('click', () => vscode.postMessage({ command: 'saveMermaid' }));
$('svg').addEventListener('click', () => vscode.postMessage({ command: 'saveSvg', svg: exportSvg() }));

window.addEventListener('message', e => {
  const m = e.data;
  if (m.type === 'busy') { status.textContent = m.text; }
  else if (m.type === 'error') { status.textContent = m.message; }
  else if (m.type === 'diagram') { current = m; render(m); }
});

const esc = s => String(s ?? '').replace(/&/g, '&amp;').replace(/</g, '&lt;').replace(/>/g, '&gt;').replace(/"/g, '&quot;');
const ROW = 18, HEAD = 26, GAP_X = 110, GAP_Y = 26, CHAR = 7;

function render(m) {
  $('depth').value = String(m.options.depth);
  $('direction').value = m.options.direction;
  $('fields').value = m.options.allFields ? 'all' : 'keys';
  status.textContent = m.tables.length + ' table(s), ' + m.edges.length + ' relation(s)' + (m.truncated ? ' · limited to ' + m.max + ' tables, lower the levels' : '') + ' · click a table to open it, double-click to center the diagram on it';
  const cols = new Map();
  for (const t of m.tables) { (cols.get(t.level) || cols.set(t.level, []).get(t.level)).push(t); }
  const levels = [...cols.keys()].sort((a, b) => a - b);
  const pos = new Map();
  let x = 0, height = 0;
  const widths = new Map();
  for (const level of levels) {
    const list = cols.get(level).sort((a, b) => a.name.localeCompare(b.name));
    const w = Math.max(170, ...list.map(t => Math.max(t.name.length * (CHAR + 1) + 20, ...t.fields.map(f => (f.name.length + (f.type || '').length + 3) * CHAR + 30))));
    widths.set(level, w);
    let y = 0;
    for (const t of list) {
      const h = HEAD + Math.max(1, t.fields.length) * ROW + 6;
      pos.set(t.name, { x, y, w, h, t });
      y += h + GAP_Y;
    }
    height = Math.max(height, y);
    x += w + GAP_X;
  }
  // Center each column vertically.
  for (const level of levels) {
    const list = cols.get(level);
    const colHeight = list.reduce((s, t) => s + pos.get(t.name).h + GAP_Y, 0);
    const offset = (height - colHeight) / 2;
    for (const t of list) { pos.get(t.name).y += offset; }
  }
  const width = Math.max(x - GAP_X, 200) + 40, total = height + 40;
  const rowY = (p, field) => {
    const i = p.t.fields.findIndex(f => f.name.toLowerCase() === String(field || '').toLowerCase());
    return i < 0 ? p.y + HEAD / 2 : p.y + HEAD + i * ROW + ROW / 2 + 2;
  };
  const parts = [];
  const cardText = { ZeroMore: '0..*', OneMore: '1..*', ZeroOne: '0..1', ExactlyOne: '1' };
  for (const e of m.edges) {
    const a = pos.get(e.from), b = pos.get(e.to);
    if (!a || !b) continue;
    const [f, rf] = e.fields[0] || [];
    const y1 = rowY(a, f), y2 = rowY(b, rf);
    let x1, x2, c1, c2;
    if (a === b) { x1 = a.x + a.w; x2 = a.x + a.w; c1 = x1 + 50; c2 = x2 + 50; }
    else if (b.x > a.x) { x1 = a.x + a.w; x2 = b.x; c1 = x1 + 50; c2 = x2 - 50; }
    else if (b.x < a.x) { x1 = a.x; x2 = b.x + b.w; c1 = x1 - 50; c2 = x2 + 50; }
    else { x1 = a.x + a.w; x2 = b.x + b.w; c1 = x1 + 60; c2 = x2 + 60; }
    const title = e.from + '.' + e.relation + ' → ' + e.to + (e.fields.length ? '\n' + e.fields.map(p => p[0] + ' = ' + e.to + '.' + p[1]).join('\n') : '');
    parts.push('<path class="edge" marker-end="url(#arrow)" d="M' + x1 + ',' + y1 + ' C' + c1 + ',' + y1 + ' ' + c2 + ',' + y2 + ' ' + x2 + ',' + y2 + '"><title>' + esc(title) + '</title></path>');
    const lx = x1 + (c1 > x1 ? 6 : -30), rx = x2 + (c2 > x2 ? 6 : -30);
    parts.push('<text class="card" x="' + lx + '" y="' + (y1 - 3) + '">' + esc(cardText[e.cardinality] || '*') + '</text>');
    parts.push('<text class="card" x="' + rx + '" y="' + (y2 - 3) + '">' + esc(cardText[e.relatedCardinality] || '1') + '</text>');
  }
  for (const p of pos.values()) {
    const t = p.t;
    const pk = new Set(t.primaryKey.map(k => k.toLowerCase()));
    const cls = 'box' + (t.name.toLowerCase() === m.root.toLowerCase() ? ' root' : '') + (t.isCustom ? ' custom' : '');
    parts.push('<g class="' + cls + '" data-table="' + esc(t.name) + '"><title>' + esc(t.name + (t.model ? ' (' + t.model + ')' : '')) + '</title>' +
      '<rect class="body" x="' + p.x + '" y="' + p.y + '" width="' + p.w + '" height="' + p.h + '" rx="4"/>' +
      '<rect class="head" x="' + p.x + '" y="' + p.y + '" width="' + p.w + '" height="' + HEAD + '" rx="4"/>' +
      '<text class="title" x="' + (p.x + 8) + '" y="' + (p.y + 17) + '">' + esc(t.name) + '</text>' +
      t.fields.map((f, i) => {
        const y = p.y + HEAD + i * ROW + 14;
        return '<text class="' + (pk.has(f.name.toLowerCase()) ? 'pk' : '') + '" x="' + (p.x + 8) + '" y="' + y + '">' + (pk.has(f.name.toLowerCase()) ? '🔑 ' : '') + esc(f.name) + '</text>' +
          '<text class="type" text-anchor="end" x="' + (p.x + p.w - 8) + '" y="' + y + '">' + esc(f.type || '') + '</text>';
      }).join('') + '</g>');
  }
  canvas.innerHTML = '<svg id="erd" xmlns="http://www.w3.org/2000/svg" width="' + width + '" height="' + total + '" viewBox="-20 -20 ' + width + ' ' + total + '">' +
    '<defs><marker id="arrow" viewBox="0 0 10 10" refX="9" refY="5" markerWidth="7" markerHeight="7" orient="auto-start-reverse"><path d="M0,0 L10,5 L0,10 z" fill="currentColor" style="color: var(--vscode-charts-blue)"/></marker></defs>' +
    parts.join('') + '</svg>';
  canvas.querySelectorAll('g.box').forEach(g => {
    g.addEventListener('click', () => vscode.postMessage({ command: 'open', table: g.dataset.table }));
    g.addEventListener('dblclick', () => vscode.postMessage({ command: 'focus', table: g.dataset.table }));
  });
}

// A standalone SVG: the theme's CSS variables resolved into inline styles.
function exportSvg() {
  const svg = $('erd');
  if (!svg) return '';
  const clone = svg.cloneNode(true);
  const source = svg.querySelectorAll('*'), target = clone.querySelectorAll('*');
  const props = ['fill', 'stroke', 'stroke-width', 'opacity', 'font-family', 'font-size', 'font-weight', 'color'];
  source.forEach((el, i) => {
    const cs = getComputedStyle(el);
    target[i].setAttribute('style', props.map(p => p + ':' + cs.getPropertyValue(p)).join(';'));
  });
  const bg = getComputedStyle(document.body).getPropertyValue('--vscode-editor-background');
  clone.insertAdjacentHTML('afterbegin', '<rect x="-20" y="-20" width="100%" height="100%" fill="' + bg + '"/>');
  return '<?xml version="1.0" encoding="UTF-8"?>\n' + clone.outerHTML;
}

vscode.postMessage({ command: 'ready' });
`;
