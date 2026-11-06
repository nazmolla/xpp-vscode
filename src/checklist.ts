import * as vscode from 'vscode';
import { XppBuilder } from './build';
import { readActiveEnvironment } from './environment';
import { readProject } from './project';
import { ResultsView } from './results';
import { XppServerClient } from './serverClient';

interface Step { name: string; ok: boolean; detail: string }

const CodeTypes = new Set(['AxClass', 'AxTable', 'AxForm', 'AxDataEntityView']);

/**
 * F&O Studio: Pre-Deploy Checklist: build, best practice, performance, security gaps and (on unified environments) tests
 * for the project, in one report; when everything blocking passes, offers to create the deployable package or deploy.
 */
export async function preDeployChecklist(server: XppServerClient, builder: XppBuilder, results: ResultsView): Promise<void> {
    const project = await readProject();
    if (!project?.model) {
        throw new Error('Open a D365FO project first.');
    }
    const model = project.model;
    const steps: Step[] = [];
    const cancelled = await vscode.window.withProgress({ location: { viewId: 'xpp.results' }, title: `Pre-deploy checklist: ${project.name}`, cancellable: true },
        async (progress, token) => {
            progress.report({ message: 'build' });
            const built = await builder.build(model, token, project.items).catch(() => false);
            steps.push({ name: 'Build', ok: built, detail: built ? 'Succeeded.' : 'Failed: fix the errors in the Problems view.' });
            if (token.isCancellationRequested) {
                return true;
            }

            progress.report({ message: 'best practice' });
            const bp = await builder.bestPractice(model, token, project.items).catch(() => undefined);
            steps.push({ name: 'Best practice', ok: !!bp && bp.errors === 0, detail: bp ? `${bp.errors} error(s), ${bp.warnings} warning(s) in the project's elements.` : 'Could not run.' });
            if (token.isCancellationRequested) {
                return true;
            }

            progress.report({ message: 'performance' });
            let warnings = 0;
            for (const item of project.items.filter(i => CodeTypes.has(i.objectType))) {
                const result = await server.bridge('analyze_performance', { objectType: item.objectType, objectName: item.objectName }).catch(() => undefined);
                warnings += (result?.findings ?? []).filter((f: { severity: string }) => f.severity === 'Warning').length;
            }
            steps.push({ name: 'Performance', ok: true, detail: warnings ? `${warnings} warning(s); run Analyze Performance on the project's classes to see them.` : 'No warnings.' });

            progress.report({ message: 'security' });
            const gaps = await server.tool('xpp_analyze_security', { op: 'gaps', models: [model] }).catch(() => undefined);
            const gapText = gaps?.text ?? '';
            // Lines are "- <Error|Warning|Info> <kind> <type> <name>: <message>"; errors block a deployment.
            const blocking = /^- Error /m.test(gapText);
            steps.push({ name: 'Security', ok: !blocking, detail: gapText ? gapText.split('\n').slice(0, 12).join('\n') : 'Could not run.' });

            const env = await readActiveEnvironment();
            const runner = `${model.replace(/\W/g, '')}ScratchRunner`;
            const scratch = await server.bridge('find_object', { objectName: runner, objectType: 'AxClass', exactMatch: true, maxResults: 1 }).catch(() => undefined);
            const hasScratch = (scratch?.matches ?? []).some((m: { modelName?: string }) => (m.modelName ?? '').toLowerCase() === model.toLowerCase());
            steps.push({ name: 'Scratch code', ok: !hasScratch, detail: hasScratch
                ? `${runner} (Run Snippet's scratch class) is in ${model} and would ship in the package: delete it (project view > Delete) before packaging.`
                : 'No scratch runner in the model.' });
            steps.push({ name: 'Tests', ok: true, detail: env.local ? 'Run tests on the VM (cloud-hosted/VHD).' : 'Run them after deploying: Test Explorer or Run Tests (they run against the deployed code).' });
            return token.isCancellationRequested;
        });
    if (cancelled) {
        return;
    }

    const passed = steps.every(s => s.ok);
    const content = [
        `# Pre-deploy checklist: ${project.name} (${model})`, '',
        `**${passed ? 'Ready to package and deploy.' : 'Not ready: fix the failed steps first.'}**`, '',
        ...steps.flatMap(s => [`## ${s.ok ? '✅' : '❌'} ${s.name}`, '', s.detail, '']),
    ].join('\n');
    await results.show({
        title: `Pre-Deploy Checklist: ${project.name}`, time: new Date(), markdown: content, rerun: { command: 'xpp.preDeployChecklist' },
        summary: passed ? 'Ready to package and deploy' : 'Not ready: fix the failed steps first',
        sections: steps.map(s => {
            const lines = s.detail.split('\n').map(l => l.replace(/^-\s*/, '').trim()).filter(Boolean);
            return {
                title: s.name, severity: s.ok ? 'pass' as const : 'error' as const, description: lines.length === 1 ? lines[0] : undefined,
                findings: lines.length > 1 ? lines.map(l => ({ label: l, severity: /^Error\b/.test(l) ? 'error' as const : /^Warning\b/.test(l) ? 'warning' as const : 'info' as const })) : [],
            };
        }),
    });
    if (passed) {
        const env = await readActiveEnvironment();
        const choice = await vscode.window.showInformationMessage('The checklist passed.', 'Create Deployment Package', ...(env.local ? [] : ['Deploy']));
        if (choice === 'Create Deployment Package') {
            await vscode.commands.executeCommand('xpp.tools.createPackage');
        } else if (choice === 'Deploy') {
            await vscode.commands.executeCommand('xpp.pac.deploy');
        }
    }
}
