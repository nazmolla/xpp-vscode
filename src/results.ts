import * as vscode from 'vscode';
import { objectUri } from './codeView';
import { methodSpans } from './xppSource';

export type Severity = 'error' | 'warning' | 'info' | 'pass';

export interface Finding {
    label: string;
    description?: string;
    tooltip?: string;
    severity: Severity;
    /** Opens the element's code view at the method line (1-based within the method) or document line. */
    location?: { objectType: string; objectName: string; method?: string; line?: number };
    command?: vscode.Command;
}

export interface ResultSection {
    title: string;
    severity: Severity;
    description?: string;
    findings: Finding[];
}

export interface ResultRun {
    title: string;
    time: Date;
    summary?: string;
    sections: ResultSection[];
    /** The report as markdown, for Copy and Export. */
    markdown: string;
    rerun?: { command: string; args?: unknown[] };
}

type Node =
    | { kind: 'header'; run: ResultRun }
    | { kind: 'section'; section: ResultSection; run: ResultRun }
    | { kind: 'finding'; finding: Finding }
    | { kind: 'history' }
    | { kind: 'past'; run: ResultRun; index: number };

const Icons: Record<Severity, [string, string]> = {
    error: ['error', 'problemsErrorIcon.foreground'],
    warning: ['warning', 'problemsWarningIcon.foreground'],
    info: ['info', 'problemsInfoIcon.foreground'],
    pass: ['pass', 'testing.iconPassed'],
};
const SeverityText: Record<Severity, string> = { error: 'error', warning: 'warning', info: 'information', pass: 'passed' };

/**
 * The Results view (panel): Review My Changes, the Pre-Deploy Checklist and Upgrade Readiness as clickable findings, with
 * rerun, copy and export, and the earlier runs of this session.
 */
export class ResultsView implements vscode.TreeDataProvider<Node>, vscode.Disposable {
    private readonly changed = new vscode.EventEmitter<Node | undefined>();
    readonly onDidChangeTreeData = this.changed.event;
    private readonly runs: ResultRun[] = [];
    private view: vscode.TreeView<Node> | undefined;

    register(context: vscode.ExtensionContext, command: (id: string, handler: (...args: any[]) => unknown) => void): void {
        this.view = vscode.window.createTreeView('xpp.results', { treeDataProvider: this, showCollapseAll: true });
        context.subscriptions.push(this.view, this);
        command('xpp.results.rerun', () => {
            const rerun = this.runs[0]?.rerun;
            return rerun ? vscode.commands.executeCommand(rerun.command, ...(rerun.args ?? [])) : undefined;
        });
        command('xpp.results.copy', async () => {
            if (this.runs[0]) {
                await vscode.env.clipboard.writeText(this.runs[0].markdown);
                vscode.window.setStatusBarMessage('Report copied as markdown', 3000);
            }
        });
        command('xpp.results.export', async () => {
            const run = this.runs[0];
            if (!run) {
                return;
            }
            const target = await vscode.window.showSaveDialog({ filters: { Markdown: ['md'] }, saveLabel: 'Export Report' });
            if (target) {
                await vscode.workspace.fs.writeFile(target, Buffer.from(run.markdown, 'utf8'));
            }
        });
        command('xpp.results.clear', () => {
            this.runs.length = 0;
            this.update();
        });
        command('xpp.results.open', (run: ResultRun) => {
            const index = this.runs.indexOf(run);
            if (index > 0) {
                this.runs.splice(index, 1);
                this.runs.unshift(run);
                this.update();
            }
        });
        this.update();
    }

    /** Shows a run as the current result and reveals the view without taking focus. */
    async show(run: ResultRun): Promise<void> {
        this.runs.unshift(run);
        this.runs.splice(10);
        this.update();
        await vscode.commands.executeCommand('xpp.results.focus', { preserveFocus: true }).then(undefined, () => undefined);
    }

    dispose(): void {
        this.changed.dispose();
    }

    private update(): void {
        void vscode.commands.executeCommand('setContext', 'xpp.results.hasRun', this.runs.length > 0);
        void vscode.commands.executeCommand('setContext', 'xpp.results.canRerun', !!this.runs[0]?.rerun);
        if (this.view) {
            const run = this.runs[0];
            this.view.description = run ? `${run.title} · ${run.time.toLocaleTimeString([], { hour: '2-digit', minute: '2-digit' })}` : undefined;
        }
        this.changed.fire(undefined);
    }

    getChildren(node?: Node): Node[] {
        const run = this.runs[0];
        if (!node) {
            if (!run) {
                return [];
            }
            return [
                ...run.summary ? [{ kind: 'header', run } as Node] : [],
                ...run.sections.map(section => ({ kind: 'section', section, run } as Node)),
                ...this.runs.length > 1 ? [{ kind: 'history' } as Node] : [],
            ];
        }
        if (node.kind === 'section') {
            return node.section.findings.map(finding => ({ kind: 'finding', finding }));
        }
        if (node.kind === 'history') {
            return this.runs.slice(1).map((past, i) => ({ kind: 'past', run: past, index: i + 1 }));
        }
        return [];
    }

    getTreeItem(node: Node): vscode.TreeItem {
        switch (node.kind) {
            case 'header': {
                const item = new vscode.TreeItem(node.run.summary!, vscode.TreeItemCollapsibleState.None);
                item.iconPath = new vscode.ThemeIcon('report');
                return item;
            }
            case 'section': {
                const s = node.section;
                const item = new vscode.TreeItem(s.title, s.findings.length ? vscode.TreeItemCollapsibleState.Expanded : vscode.TreeItemCollapsibleState.None);
                item.description = s.description ?? (s.findings.length ? String(s.findings.length) : undefined);
                item.iconPath = icon(s.severity);
                item.accessibilityInformation = { label: `${s.title}, ${SeverityText[s.severity]}${item.description ? `, ${item.description}` : ''}` };
                return item;
            }
            case 'finding': {
                const f = node.finding;
                const item = new vscode.TreeItem(f.label, vscode.TreeItemCollapsibleState.None);
                item.description = f.description;
                item.tooltip = f.tooltip ?? [f.label, f.description].filter(Boolean).join('\n');
                item.iconPath = icon(f.severity);
                item.accessibilityInformation = { label: `${SeverityText[f.severity]}: ${f.label}${f.description ? `, ${f.description}` : ''}` };
                item.command = f.command ?? (f.location ? { command: 'xpp.results.goTo', title: 'Open', arguments: [f.location] } : undefined);
                return item;
            }
            case 'history': {
                const item = new vscode.TreeItem('Earlier runs', vscode.TreeItemCollapsibleState.Collapsed);
                item.iconPath = new vscode.ThemeIcon('history');
                return item;
            }
            case 'past': {
                const item = new vscode.TreeItem(node.run.title, vscode.TreeItemCollapsibleState.None);
                item.description = node.run.time.toLocaleTimeString();
                const worst = node.run.sections.some(s => s.severity === 'error') ? 'error' : node.run.sections.some(s => s.severity === 'warning') ? 'warning' : 'pass';
                item.iconPath = icon(worst);
                item.command = { command: 'xpp.results.open', title: 'Show', arguments: [node.run] };
                return item;
            }
        }
    }
}

function icon(severity: Severity): vscode.ThemeIcon {
    const [id, color] = Icons[severity];
    return new vscode.ThemeIcon(id, new vscode.ThemeColor(color));
}

/** Opens a finding's element at its line; method lines count from the method's first line. */
export async function goToFinding(location: { objectType: string; objectName: string; method?: string; line?: number }): Promise<void> {
    const document = await vscode.workspace.openTextDocument(objectUri(location.objectType, location.objectName));
    let line = Math.max(0, (location.line ?? 1) - 1);
    if (location.method) {
        const start = methodSpans(document.getText()).find(s => s.name.toLowerCase() === location.method!.toLowerCase())?.startLine ?? 0;
        line = start + (location.line ? location.line - 1 : 0);
    }
    const position = new vscode.Position(Math.min(line, document.lineCount - 1), 0);
    await vscode.window.showTextDocument(document, { selection: new vscode.Range(position, position) });
}

export function worst(findings: { severity: Severity }[], fallback: Severity = 'pass'): Severity {
    return findings.some(f => f.severity === 'error') ? 'error' : findings.some(f => f.severity === 'warning') ? 'warning'
        : findings.some(f => f.severity === 'info') ? 'info' : fallback;
}
