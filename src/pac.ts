import { execFile } from 'child_process';
import { existsSync } from 'fs';
import * as path from 'path';
import * as vscode from 'vscode';
import { orgName, readActiveEnvironment, selectedEnvironmentUrl } from './environment';
import { readProject } from './project';

/**
 * Deploy and database sync for unified developer environments run through the Power Platform CLI
 * (pac package ... --package-type erp), which needs a solution folder with .erp/xpp.json.
 */
export class PacCommands {
    async initSolution(): Promise<void> {
        const root = this.workspaceRoot();
        const project = await readProject();
        const model = await vscode.window.showInputBox({ title: 'Model(s) for the deployment solution', value: project?.model ?? '', prompt: 'Comma-separated model names' });
        if (!root || !model) {
            return;
        }
        await this.run('Initialize erp solution', ['package', 'init', '--package-type', 'erp', '--model', model, '--outputDirectory', root]);
    }

    /** One named profile per environment, so environments in other tenants keep their own account. */
    async signIn(): Promise<void> {
        const url = selectedEnvironmentUrl()
            || await vscode.window.showInputBox({ title: 'Dataverse environment URL', placeHolder: 'https://yourorg.crm.dynamics.com' });
        if (!url) {
            return;
        }
        if (await this.selectProfile(url)) {
            vscode.window.showInformationMessage(`pac uses its profile ${profileName(url)} for ${orgName(url)}.`);
            return;
        }
        await this.run('Sign in', ['auth', 'create', '--name', profileName(url), '--environment', url]);
    }

    async deploy(): Promise<void> {
        const root = this.solutionRoot();
        if (!root) {
            return;
        }
        const sync = await vscode.window.showQuickPick(['None', 'Incremental', 'Full'], { title: 'Database sync after deploy' });
        if (!sync) {
            return;
        }
        await this.selectProfile();
        // Compile against the selected environment's version, not the one registered for pac's active environment.
        const env = await readActiveEnvironment().catch(() => undefined);
        const version = env?.dataverseUrl && env.applicationVersion ? ['--app-version', env.applicationVersion] : [];
        await this.run('Compile packages', ['package', 'compile', '--package-type', 'erp', '--solution-root', root, ...version]);
        await this.run('Deploy packages', ['package', 'deploy', '--package-type', 'erp', '--solution-root', root, '--db-sync', sync, ...this.environmentArgs()]);
    }

    async syncDatabase(): Promise<void> {
        const mode = await vscode.window.showQuickPick(['Full', 'Module'], { title: 'Database synchronization' });
        if (!mode) {
            return;
        }
        const args = ['package', 'db-sync', '--db-sync', mode, ...this.environmentArgs()];
        if (mode === 'Module') {
            const project = await readProject();
            const modules = await vscode.window.showInputBox({ title: 'Modules to synchronize', value: project?.model ?? '' });
            if (!modules) {
                return;
            }
            args.push('--modules', modules);
        }
        await this.selectProfile();
        await this.run('Synchronize database', args);
    }

    private environmentArgs(): string[] {
        const url = selectedEnvironmentUrl();
        return url ? ['--environment', url] : [];
    }

    /** Makes the environment's profile from Sign In active; without one, pac keeps its active profile. */
    private selectProfile(url = selectedEnvironmentUrl()): Promise<boolean> {
        if (!url) {
            return Promise.resolve(false);
        }
        const shell = process.platform === 'win32';
        return new Promise(resolve => execFile(shell ? `"${this.pac()}"` : this.pac(), ['auth', 'select', '--name', profileName(url)],
            { shell, windowsHide: true, timeout: 60_000 }, err => resolve(!err)));
    }

    private pac(): string {
        return vscode.workspace.getConfiguration('xpp').get<string>('pacPath') || 'pac';
    }

    private workspaceRoot(): string | undefined {
        const root = vscode.workspace.workspaceFolders?.[0]?.uri.fsPath;
        if (!root) {
            vscode.window.showErrorMessage('Open a workspace folder first.');
        }
        return root;
    }

    private solutionRoot(): string | undefined {
        const root = this.workspaceRoot();
        if (root && !existsSync(path.join(root, '.erp', 'xpp.json'))) {
            void vscode.window.showErrorMessage('This workspace is not a deployment solution (.erp/xpp.json is missing).', 'Initialize')
                .then(choice => choice === 'Initialize' ? this.initSolution() : undefined);
            return undefined;
        }
        return root;
    }

    /** Runs pac as a visible task and resolves when it finishes; rejects on a non-zero exit. */
    private async run(name: string, args: string[]): Promise<void> {
        const task = new vscode.Task({ type: 'xpp-pac' }, vscode.TaskScope.Workspace, name, 'X++', new vscode.ShellExecution(this.pac(), args));
        task.presentationOptions = { reveal: vscode.TaskRevealKind.Always, panel: vscode.TaskPanelKind.Shared, clear: false };
        const execution = await vscode.tasks.executeTask(task);
        const exitCode = await new Promise<number | undefined>(resolve => {
            const listener = vscode.tasks.onDidEndTaskProcess(e => {
                if (e.execution === execution) {
                    listener.dispose();
                    resolve(e.exitCode);
                }
            });
        });
        if (exitCode !== 0) {
            throw new Error(`pac ${args.slice(0, 2).join(' ')} failed (exit ${exitCode}). Is the Power Platform CLI installed and signed in? See the terminal output.`);
        }
    }
}

/** pac profile names are at most 30 characters. */
function profileName(url: string): string {
    return orgName(url).slice(0, 30);
}
