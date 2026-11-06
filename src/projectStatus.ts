import * as path from 'path';
import * as vscode from 'vscode';
import { BuildResult, XppBuilder } from './build';
import { DeployState } from './deployState';
import { findActiveProject, readProject } from './project';

/** The active D365FO project, its model and the last build result, in the status bar (click to build the project). */
export class ProjectStatus implements vscode.Disposable {
    private readonly item = vscode.window.createStatusBarItem('xpp.project', vscode.StatusBarAlignment.Left, -3);
    private readonly subscriptions: vscode.Disposable[] = [];
    private lastBuild: BuildResult | undefined;

    constructor(builder: XppBuilder, private readonly deployState: DeployState) {
        this.item.name = 'F&O Studio project';
        this.subscriptions.push(
            deployState.onDidChange(() => void this.refresh()),
            builder.onDidFinish(result => {
                this.lastBuild = result;
                void this.refresh();
            }),
            vscode.workspace.onDidChangeConfiguration(e => e.affectsConfiguration('xpp.project') ? void this.refresh() : undefined),
            vscode.workspace.onDidSaveTextDocument(doc => doc.fileName.toLowerCase().endsWith('.rnrproj') ? void this.refresh() : undefined),
            vscode.window.onDidChangeVisibleTextEditors(() => this.item.text.includes('No D365FO project') || !this.item.text ? void this.refresh() : undefined),
        );
        void this.refresh();
    }

    async refresh(): Promise<void> {
        const file = await findActiveProject().catch(() => undefined);
        const project = file ? await readProject(file).catch(() => undefined) : undefined;
        if (!project) {
            // Outside D365FO work (no project, no X++ editor) the item stays hidden.
            if (!vscode.window.visibleTextEditors.some(e => e.document.languageId === 'xpp')) {
                this.item.hide();
                return;
            }
            this.item.text = '$(project) No D365FO project';
            this.item.tooltip = 'Open or create a D365FO project';
            this.item.command = 'xpp.selectProject';
            this.item.show();
            return;
        }
        const build = this.lastBuild?.model.toLowerCase() === project.model?.toLowerCase() ? this.lastBuild : undefined;
        const deployed = project.model ? await this.deployState.of(project.model) : undefined;
        const icon = build && !build.ok ? '$(error)' : deployed?.stale ? '$(cloud-upload)' : build ? '$(check)' : '$(project)';
        this.item.text = `${icon} ${project.name}`;
        const tooltip = new vscode.MarkdownString([
            `**${project.name}** (model ${project.model ?? 'unknown'}), ${project.items.length} element(s)`,
            `\`${path.basename(project.file)}\``,
            build
                ? `Last ${build.projectOnly ? 'project' : 'model'} build ${build.time.toLocaleTimeString()}: ${build.ok ? 'succeeded' : 'failed'}, ${build.errors} error(s), ${build.warnings} warning(s)`
                : 'Not built in this session',
            ...deployed ? [deployed.stale
                ? `Built ${new Date(deployed.builtAt!).toLocaleString()}, ${deployed.deployedAt ? `deployed ${new Date(deployed.deployedAt).toLocaleString()}` : 'not deployed from this machine'}: tests and the debugger run the older deployed code. [Deploy now](command:xpp.pac.deploy)`
                : `Deployed ${new Date(deployed.deployedAt!).toLocaleString()}: the environment runs the latest build.`] : [],
            'Click to build the project.',
        ].join('\n\n'));
        tooltip.isTrusted = { enabledCommands: ['xpp.pac.deploy'] };
        this.item.tooltip = tooltip;
        this.item.command = 'xpp.buildProject';
        this.item.show();
    }

    dispose(): void {
        this.subscriptions.forEach(s => s.dispose());
        this.item.dispose();
    }
}
