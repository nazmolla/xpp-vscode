import * as vscode from 'vscode';
import { XppBuilder } from './build';
import { readActiveEnvironment } from './environment';
import { readProject } from './project';
import { XppServerClient } from './serverClient';

interface Step { name: string; ok: boolean; detail: string }

const CodeTypes = new Set(['AxClass', 'AxTable', 'AxForm']);

/**
 * F&O Studio: Pre-Deploy Checklist: build, best practice, performance, security gaps and (on unified environments) tests
 * for the project, in one report; when everything blocking passes, offers to create the deployable package or deploy.
 */
export async function preDeployChecklist(server: XppServerClient, builder: XppBuilder): Promise<void> {
    const project = await readProject();
    if (!project?.model) {
        throw new Error('Open a D365FO project first.');
    }
    const model = project.model;
    const steps: Step[] = [];
    const cancelled = await vscode.window.withProgress({ location: vscode.ProgressLocation.Notification, title: `Pre-deploy checklist: ${project.name}`, cancellable: true },
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
    await vscode.window.showTextDocument(await vscode.workspace.openTextDocument({ language: 'markdown', content }), { preview: true });
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
