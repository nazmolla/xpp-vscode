import * as vscode from 'vscode';
import { listModels } from './aot';
import { readProject } from './project';
import { XppServerClient } from './serverClient';

/** F&O Studio: Upgrade Readiness: where a model uses obsolete classes, tables and methods (the same report as xpp_find_deprecations). */
export function registerUpgradeReadiness(command: (id: string, handler: (...args: any[]) => unknown) => void, server: XppServerClient): void {
    command('xpp.upgradeReadiness', async (arg?: { model?: { name: string } }) => {
        const project = await readProject().catch(() => undefined);
        const model = arg?.model?.name ?? (await vscode.window.showQuickPick(
            (await listModels()).filter(m => m.isCustom).map(m => ({ label: m.name, description: m.name === project?.model ? 'project model' : undefined })),
            { title: 'Upgrade readiness: model' }))?.label;
        if (!model) {
            return;
        }
        const result = await vscode.window.withProgress({ location: vscode.ProgressLocation.Notification, title: `Checking ${model} for obsolete APIs…` },
            () => server.tool('xpp_find_deprecations', { modelName: model }));
        if (result.isError) {
            throw new Error(result.text);
        }
        const doc = await vscode.workspace.openTextDocument({ language: 'markdown', content: `# Upgrade readiness: ${model}\n\n${result.text}\n` });
        await vscode.window.showTextDocument(doc, { preview: true });
    });
}
