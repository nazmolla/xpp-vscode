import * as vscode from 'vscode';
import { typeLabel } from './aot';
import { XppBuilder } from './build';
import { ChangedElement, changedElements } from './changes';
import { objectUri } from './codeView';
import { XppServerClient } from './serverClient';

const CodeTypes = new Set(['AxClass', 'AxTable', 'AxForm']);
const SecurityTypes = /^AxSecurity(Role|Duty|Privilege)(Extension)?$/;
const BaseTypes: Record<string, string> = { classstr: 'AxClass', tablestr: 'AxTable', formstr: 'AxForm' };

/**
 * F&O Studio: Review My Changes: the elements of your models that changed (uncommitted and on the branch), with a
 * pull-request summary, best-practice results, performance findings and Chain of Command wrappers that don't call next.
 */
export async function reviewChanges(server: XppServerClient, builder: XppBuilder): Promise<void> {
    const configured = vscode.workspace.getConfiguration('xpp').get<string>('git.baseBranch');
    const base = await vscode.window.showInputBox({
        title: 'Review my changes', prompt: 'Compare with this branch (empty: only uncommitted changes)', value: configured || 'main', ignoreFocusOut: true,
    });
    if (base === undefined) {
        return;
    }
    const report = await vscode.window.withProgress({ location: vscode.ProgressLocation.Notification, title: 'Reviewing your changes', cancellable: true },
        (progress, token) => buildReport(server, builder, base.trim() || undefined, progress, token));
    if (report) {
        await vscode.window.showTextDocument(await vscode.workspace.openTextDocument({ language: 'markdown', content: report }), { preview: true });
    }
}

async function buildReport(server: XppServerClient, builder: XppBuilder, base: string | undefined,
    progress: vscode.Progress<{ message?: string }>, token: vscode.CancellationToken): Promise<string | undefined> {
    progress.report({ message: 'finding changed elements' });
    const changes = await changedElements(base);
    if (changes.length === 0) {
        vscode.window.showInformationMessage(`No changed elements in your models${base ? ` since ${base}` : ''} (models must be in a Git repository).`);
        return undefined;
    }
    const live = changes.filter(c => c.status !== 'D');
    const lines: string[] = [`# Review of my changes${base ? ` (since ${base}, plus uncommitted)` : ' (uncommitted)'}`, ''];

    lines.push('## Pull request summary', '');
    const byType = new Map<string, ChangedElement[]>();
    for (const c of changes) {
        byType.set(c.objectType, [...(byType.get(c.objectType) ?? []), c]);
    }
    lines.push(`${changes.length} element(s) in ${new Set(changes.map(c => c.model)).size} model(s):`, '');
    for (const [type, items] of byType) {
        lines.push(`- **${typeLabel(type)}** (${items.length}): ${items.map(i => `${i.objectName}${i.status === 'A' ? ' (new)' : i.status === 'D' ? ' (deleted)' : ''}`).join(', ')}`);
    }
    const security = changes.filter(c => SecurityTypes.test(c.objectType));
    const menuItems = changes.filter(c => /^AxMenuItem/.test(c.objectType) && c.status === 'A');
    lines.push('', '**Security impact:** ' + (security.length
        ? `${security.map(s => s.objectName).join(', ')} changed; check role licenses (Security view > Analyze License) and segregation of duties.`
        : 'no roles, duties or privileges changed.'));
    if (menuItems.length) {
        lines.push(`New menu items need a privilege: ${menuItems.map(m => m.objectName).join(', ')}.`);
    }

    const findings: string[] = [];
    let done = 0;
    for (const change of live.filter(c => CodeTypes.has(c.objectType))) {
        if (token.isCancellationRequested) {
            return undefined;
        }
        progress.report({ message: `${change.objectName} (${++done})` });
        const perf = await server.bridge('analyze_performance', { objectType: change.objectType, objectName: change.objectName }).catch(() => undefined);
        for (const f of perf?.findings ?? []) {
            if (f.severity === 'Warning') {
                findings.push(`- ${change.objectName}.${f.method} line ${f.line}: ${f.message} *${f.fix}*`);
            }
        }
        if (change.objectType === 'AxClass') {
            findings.push(...await chainOfCommandFindings(server, change.objectName));
        }
    }

    progress.report({ message: 'best practice checks' });
    let bestPractice = '';
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
            }
        }
    }

    lines.push('', '## Best practice', '', bestPractice || '- Not run.', '', '## Findings', '', ...(findings.length ? [...new Set(findings)] : ['No warnings.']));
    lines.push('', '_Best-practice results are also in the Problems view. Performance findings are heuristics; confirm before changing code._');
    return lines.join('\n');
}

/** Methods of a Chain of Command class that wrap a base method but never call next (the base logic would be skipped). */
async function chainOfCommandFindings(server: XppServerClient, className: string): Promise<string[]> {
    const cls = await server.bridge('read_object', { objectType: 'AxClass', objectName: className }).catch(() => undefined);
    const target = /ExtensionOf\s*\(\s*(classStr|tableStr|formStr)\s*\(\s*(\w+)\s*\)/i.exec(cls?.declaration ?? '');
    if (!cls?.success || !target) {
        return [];
    }
    const base = await server.bridge('read_object', { objectType: BaseTypes[target[1].toLowerCase()], objectName: target[2] }).catch(() => undefined);
    const baseMethods = new Set<string>((base?.methods ?? []).map((m: { name: string }) => m.name.toLowerCase()));
    const result: string[] = [];
    for (const method of cls.methods ?? []) {
        const source: string = method.source ?? '';
        if (baseMethods.has(method.name.toLowerCase()) && !new RegExp(`\\bnext\\s+${method.name}\\s*\\(`, 'i').test(source)) {
            result.push(`- ${className}.${method.name} wraps ${target[2]}.${method.name} but never calls \`next ${method.name}(...)\`: the standard logic is skipped (only allowed for [Replaceable] methods).`);
        }
    }
    return result;
}
