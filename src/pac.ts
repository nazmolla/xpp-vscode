import { exec, execFile } from 'child_process';
import { promises as fs } from 'fs';
import * as path from 'path';
import * as vscode from 'vscode';
import { packageOf } from './build';
import { prepareProjectReferences } from './dotnetReferences';
import { convertPackagePdbs } from './portablePdb';
import { orgName, readActiveEnvironment, selectedEnvironmentUrl, XppEnvironment } from './environment';
import { readProject } from './project';
import { builtPackages, DbSync, DeployPackage, deployPackages, syncUdeDatabase } from './udeDeploy';

/**
 * Deploy and database sync for unified developer environments, as Visual Studio does them: the packages built on this
 * machine go to the environment through Dataverse (udeDeploy.ts). The Power Platform CLI commands (sign in, initialize
 * a pac solution) stay for those who deploy with pac themselves. Cloud-hosted and VHD environments have their own AOS
 * and database: no deploy, and the database is synchronized with SyncEngine.
 */
export class PacCommands {
    /** pdb2pdb: the shipped Pdb2Pdb.exe, to convert the PDBs of referenced assemblies refreshed before a deploy. */
    constructor(private readonly pdb2pdb?: string) { }
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

    /**
     * Deploys the packages as built on this machine, as Visual Studio does (no recompile, so the debugger finds the build's
     * symbols). Returns the deployed models, or undefined when nothing was deployed. dbSync skips the questions (tasks).
     */
    async deploy(options?: { dbSync?: string }): Promise<string[] | undefined> {
        if (await this.local()) {
            return undefined;
        }
        const env = await readActiveEnvironment();
        const built = await builtPackages(env.modelStoreFolder);
        if (!built.length) {
            throw new Error(`Nothing to deploy: no package in ${env.modelStoreFolder} has been built. Build your model first.`);
        }
        const project = await readProject().catch(() => undefined);
        const projectPackage = project?.model ? (await packageOf(env.modelStoreFolder, project.model)).toLowerCase() : undefined;
        let packages: DeployPackage[];
        if (options?.dbSync !== undefined) {
            packages = built.filter(p => p.name.toLowerCase() === projectPackage);
            if (!packages.length) {
                throw new Error(project?.model ? `The package of ${project.model} has not been built.` : 'Open a D365FO project first: the task deploys the package of its model.');
            }
        } else {
            const picked = await vscode.window.showQuickPick(
                built.map(p => ({ label: p.name, description: p.folder, picked: p.name.toLowerCase() === projectPackage, pkg: p })),
                { title: 'Packages to deploy (as built on this machine)', canPickMany: true });
            if (!picked?.length) {
                return undefined;
            }
            packages = picked.map(p => p.pkg);
        }
        const sync = normalizeDbSync(options?.dbSync) ?? (await vscode.window.showQuickPick([
            { label: 'None' as DbSync, description: 'No database sync' },
            { label: 'Module' as DbSync, description: `Synchronize the tables of ${packages.map(p => p.name).join(', ')}` },
            { label: 'Full' as DbSync, description: 'Synchronize the whole database (slow)' },
        ], { title: 'Database sync after deploy' }))?.label;
        if (!sync) {
            return undefined;
        }
        const output = this.output();
        output.show(true);
        const projectPackageFolder = project ? packages.find(p => p.name.toLowerCase() === projectPackage)?.folder : undefined;
        output.appendLine(`Deploying ${packages.map(p => p.name).join(', ')} to ${selectedEnvironmentUrl() ?? 'the selected environment'} (database sync: ${sync})…`);
        await vscode.window.withProgress(
            { location: vscode.ProgressLocation.Notification, title: `Deploying ${packages.map(p => p.name).join(', ')}`, cancellable: true },
            async (progress, token) => {
                // The package carries the C# projects and assemblies the X++ project references, as built now (Visual Studio copies them at build).
                if (project && projectPackageFolder && !await prepareProjectReferences({
                    projectFile: project.file,
                    packageFolder: projectPackageFolder,
                    platformBin: env.binDirectory,
                    msbuildPath: vscode.workspace.getConfiguration('xpp').get<string>('build.msbuildPath') || undefined,
                    log: line => output.appendLine(line),
                    token,
                })) {
                    throw new Error('Deploy stopped: building or copying the projects and assemblies the X++ project references failed. See the X++ deploy output.');
                }
                if (projectPackageFolder && this.pdb2pdb) {
                    // The UDE debugger reads portable PDBs only, as after a build.
                    const converted = await convertPackagePdbs(this.pdb2pdb, path.join(projectPackageFolder, 'bin'));
                    converted.failed.forEach(f => output.appendLine(`Warning: ${path.basename(f.file)} was not converted to a portable PDB: ${f.error}`));
                }
                await deployPackages(packages, sync, { line: line => output.appendLine(line) }, { report: message => progress.report({ message }) }, token);
            });
        output.appendLine('Deployed.');
        vscode.window.showInformationMessage(`Deployed ${packages.map(p => p.name).join(', ')}.`);
        return modelsOf(packages);
    }

    /** mode and modules skip the questions (tasks). */
    async syncDatabase(options?: { mode?: string; modules?: string }): Promise<void> {
        const env = await readActiveEnvironment().catch(() => undefined);
        if (env?.local) {
            await this.syncLocalDatabase(env);
            return;
        }
        const mode = options?.mode ?? await vscode.window.showQuickPick(['Full', 'Module'], { title: 'Database synchronization' });
        if (mode !== 'Full' && mode !== 'Module') {
            return;
        }
        let modules: string[] = [];
        if (mode === 'Module') {
            const project = await readProject().catch(() => undefined);
            const value = options?.modules ?? (options?.mode ? project?.model : undefined)
                ?? await vscode.window.showInputBox({ title: 'Modules to synchronize', value: project?.model ?? '' });
            if (!value) {
                return;
            }
            modules = value.split(',').map(m => m.trim()).filter(Boolean);
        }
        const output = this.output();
        output.show(true);
        output.appendLine(`Synchronizing the database (${mode}${modules.length ? `: ${modules.join(', ')}` : ''})…`);
        await vscode.window.withProgress(
            { location: vscode.ProgressLocation.Notification, title: 'Synchronizing the database', cancellable: true },
            (progress, token) => syncUdeDatabase(mode, modules, { line: line => output.appendLine(line) }, { report: message => progress.report({ message }) }, token));
        output.appendLine('Database synchronized.');
    }

    private channel?: vscode.OutputChannel;

    private output(): vscode.OutputChannel {
        return this.channel ??= vscode.window.createOutputChannel('X++ Deploy');
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

    /** Tells the user that deploying does not apply to a cloud-hosted or VHD environment. */
    private async local(): Promise<boolean> {
        const local = !!(await readActiveEnvironment().catch(() => undefined))?.local;
        if (local) {
            vscode.window.showInformationMessage('This is a cloud-hosted or VHD environment: your models run on the local AOS once they are built, so there is nothing to deploy.');
        }
        return local;
    }

    /** Makes the environment's profile from Sign In active. */
    private selectProfile(url: string): Promise<boolean> {
        const pac = this.pac();
        const args = ['auth', 'select', '--name', profileName(url)];
        const options = { windowsHide: true, timeout: 60_000 };
        return new Promise(resolve => pac === 'pac' || /\.(cmd|bat)$/i.test(pac)
            // pac from PATH may be a .cmd shim, which needs a shell; pac() has no quotes and the name is a host name ([a-z0-9-]).
            ? exec(`"${pac}" ${args.join(' ')}`, options, err => resolve(!err))
            : execFile(pac, args, options, err => resolve(!err)));
    }

    private pac(): string {
        const configured = vscode.workspace.getConfiguration('xpp').get<string>('pacPath') || 'pac';
        return configured.includes('"') ? 'pac' : configured;
    }

    private workspaceRoot(): string | undefined {
        const root = vscode.workspace.workspaceFolders?.[0]?.uri.fsPath;
        if (!root) {
            vscode.window.showErrorMessage('Open a workspace folder first.');
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


function normalizeDbSync(value: string | undefined): DbSync | undefined {
    if (value === undefined) {
        return undefined;
    }
    // Incremental needed an argument file with pac; the closest sync for the deployed packages is Module.
    return value === 'Full' ? 'Full' : value === 'None' ? 'None' : 'Module';
}

/** The models in deployed packages (their descriptors), so the deploy state of each model is updated. */
async function modelsOf(packages: DeployPackage[]): Promise<string[]> {
    const models: string[] = [];
    for (const pkg of packages) {
        const descriptors = await fs.readdir(path.join(pkg.folder, 'Descriptor')).catch(() => [] as string[]);
        const names = descriptors.filter(f => f.toLowerCase().endsWith('.xml')).map(f => f.slice(0, -4));
        models.push(...(names.length ? names : [pkg.name]));
    }
    return models;
}
