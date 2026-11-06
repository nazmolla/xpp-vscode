import { existsSync, promises as fs } from 'fs';
import * as path from 'path';
import * as vscode from 'vscode';
import { listModels } from './aot';
import { dynamics365Folder, readActiveEnvironment } from './environment';
import { readProject } from './project';
import { Finding, ResultsView, worst } from './results';
import { XppServerClient } from './serverClient';

/** F&O Studio: Upgrade Readiness: where a model uses obsolete classes, tables and methods (the same report as xpp_find_deprecations). */
export function registerUpgradeReadiness(command: (id: string, handler: (...args: any[]) => unknown) => void, server: XppServerClient, results: ResultsView): void {
    command('xpp.upgradeReadiness', async (arg?: string | { model?: { name: string } }) => {
        const project = await readProject().catch(() => undefined);
        const model = (typeof arg === 'string' ? arg : arg?.model?.name) ?? (await vscode.window.showQuickPick(
            (await listModels()).filter(m => m.isCustom).map(m => ({ label: m.name, description: m.name === project?.model ? 'project model' : undefined })),
            { title: 'Upgrade readiness: model' }))?.label;
        if (!model) {
            return;
        }
        const result = await vscode.window.withProgress({ location: { viewId: 'xpp.results' }, title: `Checking ${model} for obsolete APIs…` },
            () => server.tool('xpp_find_deprecations', { modelName: model }));
        if (result.isError) {
            throw new Error(result.text);
        }
        const [obsoleteText, ...rest] = result.text.split(/\n\n(?=Extension compatibility|Collisions with other models|Standard code drift)/);
        const compatibilityText = rest.find(r => r.startsWith('Extension compatibility')) ?? '';
        const collisionsText = rest.find(r => r.startsWith('Collisions with other models')) ?? '';
        const obsolete = parseObsolete(obsoleteText);
        const compatibility = parseCompatibility(compatibilityText);
        const collisions = parseCompatibility(collisionsText);
        await results.show({
            title: `Upgrade Readiness: ${model}`, time: new Date(), rerun: { command: 'xpp.upgradeReadiness', args: [model] },
            markdown: `# Upgrade readiness: ${model}\n\n${result.text}\n`,
            sections: [
                { title: 'Obsolete API uses', severity: worst(obsolete), description: firstLine(obsoleteText), findings: obsolete },
                { title: 'Extension compatibility', severity: worst(compatibility), description: firstLine(compatibilityText), findings: compatibility },
                { title: 'Collisions with other models', severity: worst(collisions), description: firstLine(collisionsText), findings: collisions },
            ],
        });
    });

    const drift = new Map<string, string>();
    vscode.workspace.registerTextDocumentContentProvider(DriftScheme, { provideTextDocumentContent: uri => drift.get(uri.path) ?? '' });
    command('xpp.standardCodeDrift', async (arg?: string) => {
        const project = await readProject().catch(() => undefined);
        const model = typeof arg === 'string' ? arg : (await vscode.window.showQuickPick(
            (await listModels()).filter(m => m.isCustom).map(m => ({ label: m.name, description: m.name === project?.model ? 'project model' : undefined })),
            { title: 'Standard code drift: model' }))?.label;
        if (!model) {
            return;
        }
        const env = await readActiveEnvironment();
        const current = env.applicationVersion;
        const versions = (await fs.readdir(dynamics365Folder()).catch(() => [] as string[]))
            .filter(v => /^\d+(\.\d+){3}$/.test(v) && v !== current && existsSync(path.join(dynamics365Folder(), v, 'PackagesLocalDirectory')));
        const picked = versions.length
            ? await vscode.window.showQuickPick(versions.map(v => ({ label: v, folder: path.join(dynamics365Folder(), v, 'PackagesLocalDirectory') })),
                { title: `Compare the standard code of ${current ?? 'the installed version'} with` })
            : undefined;
        const previousFolder = picked?.folder ?? (await vscode.window.showOpenDialog({
            title: 'PackagesLocalDirectory of the earlier version', canSelectFolders: true, canSelectFiles: false, openLabel: 'Compare With This Version',
        }))?.[0]?.fsPath;
        if (!previousFolder) {
            return;
        }
        const result = await vscode.window.withProgress({ location: { viewId: 'xpp.results' }, title: `Comparing the code ${model} wraps…` },
            () => server.bridge('wrapper_drift', { modelName: model, previousFolder }));
        if (!result.success) {
            throw new Error(result.message);
        }
        const previousLabel = picked?.label ?? path.basename(path.dirname(previousFolder));
        const findings: Finding[] = (result.changed as { className: string; method: string; target: string; changedLines: number; current: string; previous: string }[]).map((c, i) => {
            const left = `/${i}/${c.target.replace(/\W+/g, '_')}.${c.method}.${previousLabel}.xpp`;
            const right = `/${i}/${c.target.replace(/\W+/g, '_')}.${c.method}.${current ?? 'installed'}.xpp`;
            drift.set(left, c.previous);
            drift.set(right, c.current);
            return {
                label: `${c.target}.${c.method}`, description: `${c.changedLines} line(s) differ · wrapped by ${c.className}`, severity: 'warning',
                command: { command: 'vscode.diff', title: 'Compare', arguments: [vscode.Uri.from({ scheme: DriftScheme, path: left }), vscode.Uri.from({ scheme: DriftScheme, path: right }),
                    `${c.target}.${c.method}: ${previousLabel} \u2194 ${current ?? 'installed'}`] },
            };
        });
        const markdown = [`# Standard code drift: ${model} (${previousLabel} \u2192 ${current ?? 'installed'})`, '',
            `${result.wrappers} Chain of Command wrapper(s); ${findings.length} target method(s) changed, ${result.unchanged} unchanged, ${result.notFound} not found in one of the versions.`, '',
            ...findings.map(f => `- ${f.label}: ${f.description}`)].join('\n');
        await results.show({
            title: `Standard Code Drift: ${model}`, time: new Date(), markdown, rerun: { command: 'xpp.standardCodeDrift', args: [model] },
            summary: `${previousLabel} \u2192 ${current ?? 'installed'}: ${findings.length} wrapped method(s) changed`,
            sections: [{ title: 'Wrapped methods whose standard code changed', severity: findings.length ? 'warning' : 'pass',
                description: findings.length ? undefined : 'none; formatting-only changes are ignored', findings }],
        });
    });
}

const DriftScheme = 'xpp-drift';

function firstLine(text: string): string | undefined {
    return text.split('\n')[0].replace(/^(Extension compatibility|Collisions with other models): /, '').replace(/^Package \S+: /, '') || undefined;
}

/** "- [error] Class.method line 12 uses Other.method: message" */
function parseObsolete(text: string): Finding[] {
    return text.split('\n').filter(l => l.startsWith('- ')).map(l => {
        const m = /^- (\[error\] )?(\S+?)(?:\.(\w+))? line (\d+) uses (.*)$/.exec(l);
        if (!m) {
            return { label: l.slice(2), severity: 'warning' as const };
        }
        return {
            label: m[3] ? `${m[2]}.${m[3]}` : m[2], description: `line ${m[4]}: uses ${m[5]}`, severity: m[1] ? 'error' as const : 'warning' as const,
            command: { command: 'xpp.results.goToElement', title: 'Open', arguments: [m[2], m[3], Number(m[4])] },
        };
    });
}

/** "- Error Rule: Object.member: message" */
function parseCompatibility(text: string): Finding[] {
    return text.split('\n').filter(l => l.startsWith('- ')).map(l => {
        const m = /^- (\w+) ([^:]+): ([^:\s]+?)(?:\.(\w+))?: (.*)$/.exec(l);
        if (!m) {
            return { label: l.slice(2), severity: 'warning' as const };
        }
        const severity = /error/i.test(m[1]) ? 'error' as const : /warn/i.test(m[1]) ? 'warning' as const : 'info' as const;
        return {
            label: m[4] ? `${m[3]}.${m[4]}` : m[3], description: `${m[2]}: ${m[5]}`, severity,
            command: { command: 'xpp.results.goToElement', title: 'Open', arguments: [m[3], m[4]] },
        };
    });
}
