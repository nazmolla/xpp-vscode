import * as vscode from 'vscode';
import { typeLabel } from './aot';
import { XppBuilder } from './build';
import { ChangedElement, changedElements } from './changes';
import { objectUri } from './codeView';
import { Finding, ResultRun, ResultsView, worst } from './results';
import { XppServerClient } from './serverClient';

const CodeTypes = new Set(['AxClass', 'AxTable', 'AxForm', 'AxDataEntityView']);
const SecurityTypes = /^AxSecurity(Role|Duty|Privilege)(Extension)?$/;
const BaseTypes: Record<string, string> = { classstr: 'AxClass', tablestr: 'AxTable', formstr: 'AxForm' };

/**
 * F&O Studio: Review My Changes: the elements of your models that changed (uncommitted and on the branch), with a
 * pull-request summary, best-practice results, performance findings and Chain of Command wrappers that don't call next.
 */
export async function reviewChanges(server: XppServerClient, builder: XppBuilder, results: ResultsView, baseBranch?: string): Promise<void> {
    const configured = vscode.workspace.getConfiguration('xpp').get<string>('git.baseBranch');
    const base = baseBranch ?? await vscode.window.showInputBox({
        title: 'Review my changes', prompt: 'Compare with this branch (empty: only uncommitted changes)', value: configured || 'main', ignoreFocusOut: true,
    });
    if (base === undefined) {
        return;
    }
    const run = await vscode.window.withProgress({ location: { viewId: 'xpp.results' }, title: 'Reviewing your changes', cancellable: true },
        (progress, token) => buildReport(server, builder, base.trim() || undefined, progress, token));
    if (run) {
        await results.show({ ...run, rerun: { command: 'xpp.reviewChanges', args: [base] } });
    }
}

async function buildReport(server: XppServerClient, builder: XppBuilder, base: string | undefined,
    progress: vscode.Progress<{ message?: string }>, token: vscode.CancellationToken): Promise<ResultRun | undefined> {
    progress.report({ message: 'finding changed elements' });
    const changes = await changedElements(base);
    if (changes.length === 0) {
        vscode.window.showInformationMessage(`No changed elements in your models${base ? ` since ${base}` : ''} (models must be in a Git repository).`);
        return undefined;
    }
    const live = changes.filter(c => c.status !== 'D');
    const title = `Review My Changes${base ? ` since ${base}` : ''}`;
    const lines: string[] = [`# Review of my changes${base ? ` (since ${base}, plus uncommitted)` : ' (uncommitted)'}`, ''];

    lines.push('## Pull request summary', '');
    const byType = new Map<string, ChangedElement[]>();
    for (const c of changes) {
        byType.set(c.objectType, [...(byType.get(c.objectType) ?? []), c]);
    }
    const models = new Set(changes.map(c => c.model)).size;
    lines.push(`${changes.length} element(s) in ${models} model(s):`, '');
    const summary: Finding[] = [];
    const itemText = (i: ChangedElement) => `${i.objectName}${i.status === 'A' ? ' (new)' : i.status === 'D' ? ' (deleted)' : ''}`;
    for (const [type, items] of byType) {
        lines.push(`- **${typeLabel(type)}** (${items.length}): ${items.map(itemText).join(', ')}`);
        summary.push({ label: `${typeLabel(type)} (${items.length})`, description: items.map(itemText).join(', '), severity: 'info' });
    }
    const security = changes.filter(c => SecurityTypes.test(c.objectType));
    const menuItems = changes.filter(c => /^AxMenuItem/.test(c.objectType) && c.status === 'A');
    const securityText = security.length
        ? `${security.map(s => s.objectName).join(', ')} changed; check role licenses (Security view > Analyze License) and segregation of duties.`
        : 'no roles, duties or privileges changed.';
    lines.push('', '**Security impact:** ' + securityText);
    summary.push({ label: 'Security impact', description: securityText, severity: security.length ? 'warning' : 'info' });
    if (menuItems.length) {
        lines.push(`New menu items need a privilege: ${menuItems.map(m => m.objectName).join(', ')}.`);
        summary.push({ label: 'New menu items need a privilege', description: menuItems.map(m => m.objectName).join(', '), severity: 'warning' });
    }

    const findings: string[] = [];
    const performance: Finding[] = [];
    const coc: Finding[] = [];
    let done = 0;
    for (const change of live.filter(c => CodeTypes.has(c.objectType))) {
        if (token.isCancellationRequested) {
            return undefined;
        }
        progress.report({ message: `${change.objectName} (${++done}/${live.length})` });
        const perf = await server.bridge('analyze_performance', { objectType: change.objectType, objectName: change.objectName }).catch(() => undefined);
        for (const f of perf?.findings ?? []) {
            if (f.severity === 'Warning') {
                findings.push(`- ${change.objectName}.${f.method} line ${f.line}: ${f.message} *${f.fix}*`);
                performance.push({
                    label: `${change.objectName}.${f.method}`, description: `line ${f.line}: ${f.message}`, tooltip: `${f.message}\n${f.fix}`, severity: 'warning',
                    location: { objectType: change.objectType, objectName: change.objectName, method: f.method, line: f.line },
                });
            }
        }
        if (change.objectType === 'AxClass') {
            for (const missing of await chainOfCommandFindings(server, change.objectName)) {
                findings.push(`- ${change.objectName}.${missing.method} wraps ${missing.target}.${missing.method} but never calls \`next ${missing.method}(...)\`: the standard logic is skipped (only allowed for [Replaceable] methods).`);
                coc.push({
                    label: `${change.objectName}.${missing.method}`, description: `does not call next (wraps ${missing.target}.${missing.method})`, severity: 'error',
                    tooltip: 'The standard logic is skipped; only allowed for [Replaceable] methods.',
                    location: { objectType: 'AxClass', objectName: change.objectName, method: missing.method },
                });
            }
        }
    }

    progress.report({ message: 'best practice checks' });
    let bestPractice = '';
    const bpFindings: Finding[] = [];
    for (const model of new Set(live.map(c => c.model))) {
        if (token.isCancellationRequested) {
            return undefined;
        }
        const items = live.filter(c => c.model === model).map(c => ({ objectType: c.objectType, objectName: c.objectName }));
        const result = await builder.bestPractice(model, token, items).catch(() => undefined);
        if (result) {
            bestPractice += `- ${model}: ${result.errors} error(s), ${result.warnings} warning(s) in the changed elements.\n`;
        }
    }
    for (const change of live) {
        for (const d of vscode.languages.getDiagnostics(objectUri(change.objectType, change.objectName))) {
            if (d.severity <= vscode.DiagnosticSeverity.Warning && d.source !== 'X++ performance') {
                findings.push(`- ${change.objectName} line ${d.range.start.line + 1}: ${d.message}${d.code ? ` (${d.code})` : ''}`);
                bpFindings.push({
                    label: `${change.objectName} line ${d.range.start.line + 1}`, description: d.message,
                    severity: d.severity === vscode.DiagnosticSeverity.Error ? 'error' : 'warning',
                    location: { objectType: change.objectType, objectName: change.objectName, line: d.range.start.line + 1 },
                });
            }
        }
    }

    lines.push('', '## Best practice', '', bestPractice || '- Not run.', '', '## Findings', '', ...(findings.length ? [...new Set(findings)] : ['No warnings.']));
    lines.push('', '_Best-practice results are also in the Problems view. Performance findings are heuristics; confirm before changing code._');
    return {
        title, time: new Date(), markdown: lines.join('\n'),
        summary: `${changes.length} changed element(s) in ${models} model(s)`,
        sections: [
            { title: 'Pull request summary', severity: 'info', description: `${changes.length} element(s)`, findings: summary },
            { title: 'Chain of Command', severity: worst(coc), description: coc.length ? undefined : 'every wrapper calls next', findings: coc },
            { title: 'Best practice and compiler', severity: worst(bpFindings), description: bpFindings.length ? undefined : bestPractice ? 'no warnings' : 'not run', findings: bpFindings },
            { title: 'Performance', severity: worst(performance), description: performance.length ? undefined : 'no warnings', findings: performance },
        ],
    };
}

/** Methods of a Chain of Command class that wrap a base method but never call next (the base logic would be skipped). */
async function chainOfCommandFindings(server: XppServerClient, className: string): Promise<{ method: string; target: string }[]> {
    const cls = await server.bridge('read_object', { objectType: 'AxClass', objectName: className }).catch(() => undefined);
    const target = /ExtensionOf\s*\(\s*(classStr|tableStr|formStr)\s*\(\s*(\w+)\s*\)/i.exec(cls?.declaration ?? '');
    if (!cls?.success || !target) {
        return [];
    }
    const base = await server.bridge('read_object', { objectType: BaseTypes[target[1].toLowerCase()], objectName: target[2] }).catch(() => undefined);
    const baseMethods = new Set<string>((base?.methods ?? []).map((m: { name: string }) => m.name.toLowerCase()));
    const result: { method: string; target: string }[] = [];
    for (const method of cls.methods ?? []) {
        const source: string = method.source ?? '';
        if (baseMethods.has(method.name.toLowerCase()) && !new RegExp(`\\bnext\\s+${method.name}\\s*\\(`, 'i').test(source)) {
            result.push({ method: method.name, target: target[2] });
        }
    }
    return result;
}
