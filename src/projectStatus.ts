import * as path from 'path';
import * as vscode from 'vscode';
import { BuildResult, XppBuilder } from './build';
import { findActiveProject, readProject } from './project';

/** The active D365FO project, its model and the last build result, in the status bar (click to build the project). */
export class ProjectStatus implements vscode.Disposable {
    private readonly item = vscode.window.createStatusBarItem('xpp.project', vscode.StatusBarAlignment.Left, -3);
    private readonly subscriptions: vscode.Disposable[] = [];
    private lastBuild: BuildResult | undefined;

    constructor(builder: XppBuilder) {
        this.item.name = 'F&O Studio project';
        this.subscriptions.push(
            builder.onDidFinish(result => {
                this.lastBuild = result;
                void this.refresh();
            }),
            vscode.workspace.onDidChangeConfiguration(e => e.affectsConfiguration('xpp.project') ? void this.refresh() : undefined),
            vscode.workspace.onDidSaveTextDocument(doc => doc.fileName.toLowerCase().endsWith('.rnrproj') ? void this.refresh() : undefined),
        );
        void this.refresh();
    }

    async refresh(): Promise<void> {
        const file = await findActiveProject().catch(() => undefined);
        const project = file ? await readProject(file).catch(() => undefined) : undefined;
        if (!project) {
            this.item.text = '$(project) No D365FO project';
            this.item.tooltip = 'Open or create a D365FO project';
            this.item.command = 'xpp.selectProject';
            this.item.show();
            return;
        }
        const build = this.lastBuild?.model.toLowerCase() === project.model?.toLowerCase() ? this.lastBuild : undefined;
        const icon = !build ? '$(project)' : build.ok ? '$(check)' : '$(error)';
        this.item.text = `${icon} ${project.name}`;
        this.item.tooltip = new vscode.MarkdownString([
            `**${project.name}** (model ${project.model ?? 'unknown'}), ${project.items.length} element(s)`,
            `\`${path.basename(project.file)}\``,
            build
                ? `Last ${build.projectOnly ? 'project' : 'model'} build ${build.time.toLocaleTimeString()}: ${build.ok ? 'succeeded' : 'failed'}, ${build.errors} error(s), ${build.warnings} warning(s)`
                : 'Not built in this session',
            'Click to build the project.',
        ].join('\n\n'));
        this.item.command = 'xpp.buildProject';
        this.item.show();
    }

    dispose(): void {
        this.subscriptions.forEach(s => s.dispose());
        this.item.dispose();
    }
}
