import { exec } from 'child_process';
import { existsSync } from 'fs';
import * as path from 'path';
import * as vscode from 'vscode';
import { orgName, readActiveEnvironment, selectedEnvironmentUrl, XppEnvironment } from './environment';
import { readProject } from './project';

/**
 * Deploy and database sync for unified developer environments run through the Power Platform CLI
 * (pac package ... --package-type erp), which needs a solution folder with .erp/xpp.json. Cloud-hosted and VHD
 * environments have their own AOS and database: no deploy, and the database is synchronized with SyncEngine.
 */
export class PacCommands {
    async initSolution(): Promise<void> {
        if (await this.local()) {
            return;
        }
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
        if (await this.local()) {
            return;
        }
        const url = selectedEnvironmentUrl()
            || await vscode.window.showInputBox({ title: 'Dataverse environment URL', placeHolder: 'https://yourorg.crm.dynamics.com' });
        if (!url) {
            return;
        }
        if (await this.selectProfile(url)) {
            const choice = await vscode.window.showInformationMessage(`pac uses its profile ${profileName(url)} for ${orgName(url)}.`, 'Sign In Again');
            if (choice !== 'Sign In Again') {
                return;
            }
            await this.run('Remove profile', ['auth', 'delete', '--name', profileName(url)]);
        }
        await this.run('Sign in', ['auth', 'create', '--name', profileName(url), '--environment', url]);
    }

    async deploy(): Promise<void> {
        if (await this.local()) {
            return;
        }
        const root = this.solutionRoot();
        if (!root) {
            return;
        }
        const sync = await vscode.window.showQuickPick(['None', 'Incremental', 'Full'], { title: 'Database sync after deploy' });
        if (!sync) {
            return;
        }
        if (!await this.useProfile()) {
            return;
        }
        // Compile against the selected environment's version, not the one registered for pac's active environment.
        const env = await readActiveEnvironment().catch(() => undefined);
        const version = env?.dataverseUrl && env.applicationVersion ? ['--app-version', env.applicationVersion] : [];
        await this.run('Compile packages', ['package', 'compile', '--package-type', 'erp', '--solution-root', root, ...version]);
        await this.run('Deploy packages', ['package', 'deploy', '--package-type', 'erp', '--solution-root', root, '--db-sync', sync, ...this.environmentArgs()]);
    }

    async syncDatabase(): Promise<void> {
        const env = await readActiveEnvironment().catch(() => undefined);
        if (env?.local) {
            await this.syncLocalDatabase(env);
            return;
        }
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
        if (!await this.useProfile()) {
            return;
        }
        await this.run('Synchronize database', args);
    }

    /** Full synchronization of the local business database, as the metadata server's build does. */
    private async syncLocalDatabase(env: XppEnvironment): Promise<void> {
        const connection = `Data Source=${env.databaseServer ?? 'localhost'};Initial Catalog=${env.businessDatabaseName ?? 'AxDB'};Integrated Security=True;Enlist=True;Application Name=SyncEngine`;
        const syncEngine = path.join(env.binDirectory, 'SyncEngine.exe');
        // No shell, so the connection string reaches SyncEngine as one argument.
        await this.runTask('Synchronize database', new vscode.ProcessExecution(syncEngine, [
            '-syncmode=fullall',
            `-metadatabinaries=${env.frameworkDirectory}`,
            `-connect=${connection}`,
            '-fallbacktonative=False',
        ]), exitCode => `SyncEngine failed (exit ${exitCode}). It needs VS Code running as administrator; see the terminal output.`);
    }

    /** Tells the user that pac does not apply to a cloud-hosted or VHD environment. */
    private async local(): Promise<boolean> {
        const local = !!(await readActiveEnvironment().catch(() => undefined))?.local;
        if (local) {
            vscode.window.showInformationMessage('This is a cloud-hosted or VHD environment: your models run on the local AOS once they are built, so there is nothing to deploy with pac.');
        }
        return local;
    }

    private environmentArgs(): string[] {
        const url = selectedEnvironmentUrl();
        return url ? ['--environment', url] : [];
    }

    /** Selects the environment's profile; without one, asks before running on pac's active profile, which may be another tenant's. */
    private async useProfile(): Promise<boolean> {
        const url = selectedEnvironmentUrl();
        if (!url || await this.selectProfile(url)) {
            return true;
        }
        const choice = await vscode.window.showWarningMessage(
            `pac has no profile for ${orgName(url)}, so it would use its active profile.`, 'Sign In (pac)', 'Continue');
        if (choice === 'Sign In (pac)') {
            await this.signIn();
            return this.selectProfile(url);
        }
        return choice === 'Continue';
    }

    /** Makes the environment's profile from Sign In active. */
    private selectProfile(url: string): Promise<boolean> {
        // The name comes from a host name ([a-z0-9-]), so the command line needs no further quoting.
        return new Promise(resolve => exec(`"${this.pac()}" auth select --name ${profileName(url)}`,
            { windowsHide: true, timeout: 60_000 }, err => resolve(!err)));
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

    /** Runs pac (a .cmd, so through a shell) as a visible task. */
    private run(name: string, args: string[]): Promise<void> {
        return this.runTask(name, new vscode.ShellExecution(this.pac(), args), exitCode =>
            `pac ${args.slice(0, 2).join(' ')} failed (exit ${exitCode}). Is the Power Platform CLI installed and signed in? See the terminal output.`);
    }

    /** Runs a visible task and resolves when it finishes; rejects on a non-zero exit. */
    private async runTask(name: string, execution: vscode.ShellExecution | vscode.ProcessExecution, failure: (exitCode: number | undefined) => string): Promise<void> {
        const task = new vscode.Task({ type: 'xpp', task: name }, vscode.TaskScope.Workspace, name, 'X++', execution);
        task.presentationOptions = { reveal: vscode.TaskRevealKind.Always, panel: vscode.TaskPanelKind.Shared, clear: false };
        const running = await vscode.tasks.executeTask(task);
        const exitCode = await new Promise<number | undefined>(resolve => {
            const listener = vscode.tasks.onDidEndTaskProcess(e => {
                if (e.execution === running) {
                    listener.dispose();
                    resolve(e.exitCode);
                }
            });
        });
        if (exitCode !== 0) {
            throw new Error(failure(exitCode));
        }
    }
}

/** pac profile names are at most 30 characters. */
function profileName(url: string): string {
    return orgName(url).slice(0, 30);
}
