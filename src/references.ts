import { existsSync, promises as fs } from 'fs';
import * as path from 'path';
import * as vscode from 'vscode';
import { listModels } from './aot';
import { buildProject, csharpClassText, csharpProjectXml, findMsBuild, runProcess } from './dotnetReferences';
import { readActiveEnvironment } from './environment';
import { ProjectWorkspaces, readProject, XppProjectInfo } from './project';
import { XppServerClient } from './serverClient';
import { addToSolution, assemblyNameOf, findSolution, projectReferences, ReferenceInfo } from './solution';

/**
 * References of the X++ project to C# projects and assemblies, as Visual Studio's Add Reference: an item in the .rnrproj
 * and an AxReference in the project's model (bridge add_reference/remove_reference); the build copies the assemblies into
 * the package. Also New C# Project for the model, and building a C# project on its own.
 */
export function registerReferenceCommands(
    command: (id: string, handler: (...args: any[]) => unknown) => void,
    server: XppServerClient,
    projects: ProjectWorkspaces,
    output: vscode.OutputChannel,
    refresh: () => void,
): void {
    const references = new References(server, projects, output, refresh);
    command('xpp.addReference', () => references.add());
    command('xpp.removeReference', (node?: { ref?: ReferenceInfo }) => references.remove(node?.ref));
    command('xpp.newCSharpProject', () => references.newCSharpProject());
    command('xpp.buildCSharpProject', (node?: { file?: string }) => references.buildCSharp(node?.file));
    command('xpp.openThisProject', (node?: { file?: string }) => node?.file ? projects.open(node.file) : undefined);
    command('xpp.openProjectFile', (node?: { file?: string }) => node?.file ? vscode.window.showTextDocument(vscode.Uri.file(node.file)) : undefined);
}

const Recommended = ['ApplicationPlatform', 'ApplicationFoundation', 'ApplicationSuite'];

class References {
    constructor(
        private readonly server: XppServerClient,
        private readonly projects: ProjectWorkspaces,
        private readonly output: vscode.OutputChannel,
        private readonly refresh: () => void,
    ) { }

    async add(): Promise<void> {
        const project = await this.project();
        const solution = await findSolution(project.file);
        const referenced = new Set(projectReferences(project.file, await fs.readFile(project.file, 'utf8')).map(r => (r.path ?? '').toLowerCase()));
        type Pick = vscode.QuickPickItem & { action: 'project' | 'browseProject' | 'browseAssembly' | 'new'; file?: string };
        const picks: Pick[] = (solution?.projects ?? [])
            .filter(p => /\.csproj$/i.test(p) && !referenced.has(p.toLowerCase()))
            .map(p => ({ label: `$(file-code) ${path.basename(p, '.csproj')}`, description: path.dirname(p), action: 'project' as const, file: p }));
        if (picks.length) {
            picks.unshift({ label: 'Projects in the solution', kind: vscode.QuickPickItemKind.Separator, action: 'project' });
            picks.push({ label: '', kind: vscode.QuickPickItemKind.Separator, action: 'project' });
        }
        picks.push(
            { label: '$(folder-opened) Browse for a C# Project…', action: 'browseProject' },
            { label: '$(library) Browse for an Assembly (.dll)…', action: 'browseAssembly' },
            { label: '$(add) New C# Project…', action: 'new' },
        );
        const picked = await vscode.window.showQuickPick(picks, { title: `Add Reference to ${project.name}` });
        if (!picked) {
            return;
        }
        if (picked.action === 'new') {
            await this.newCSharpProject();
        } else if (picked.action === 'browseAssembly') {
            const dll = (await vscode.window.showOpenDialog({ title: 'Assembly to reference', filters: { Assemblies: ['dll'] }, canSelectMany: false }))?.[0];
            if (dll) {
                await this.addReference(project, dll.fsPath);
            }
        } else {
            const file = picked.file ?? (await vscode.window.showOpenDialog({ title: 'C# project to reference', filters: { 'C# projects': ['csproj'] }, canSelectMany: false }))?.[0]?.fsPath;
            if (file) {
                await this.addProjectReference(project, file);
            }
        }
    }

    /** As Visual Studio: the project is built first, so the reference names the assembly it produces. */
    async addProjectReference(project: XppProjectInfo, csproj: string): Promise<void> {
        const assembly = await this.build(csproj);
        if (!assembly) {
            return;
        }
        await this.addReference(project, assembly, csproj);
        const solution = await findSolution(project.file);
        if (solution) {
            await addToSolution(solution.file, csproj).catch(err => this.output.appendLine(`${path.basename(csproj)} was not added to ${path.basename(solution.file)}: ${(err as Error).message}`));
        }
        // The C# project becomes a folder of the project's workspace, so the C# extension loads it.
        await this.projects.prepare(project.file);
    }

    private async addReference(project: XppProjectInfo, assemblyPath: string, projectPath?: string): Promise<void> {
        const name = path.basename(assemblyPath, '.dll');
        const result = await this.server.bridge('add_reference', { modelName: project.model, objectType: 'AxReference', objectName: name, assemblyPath, projectPath });
        if (!result.success) {
            throw new Error(result.message ?? 'The reference was not added.');
        }
        this.refresh();
        vscode.window.showInformationMessage(`${result.message} Build the X++ project to copy it into the package.`);
    }

    async remove(ref: ReferenceInfo | undefined): Promise<void> {
        const project = await this.project();
        if (!ref) {
            const refs = projectReferences(project.file, await fs.readFile(project.file, 'utf8'));
            const picked = await vscode.window.showQuickPick(refs.map(r => ({ label: r.name, description: r.kind === 'project' ? 'C# project' : 'assembly', ref: r })),
                { title: `Remove a reference of ${project.name}` });
            ref = picked?.ref;
            if (!ref) {
                return;
            }
        }
        const assemblyName = ref.kind === 'project' && ref.path && existsSync(ref.path) ? assemblyNameOf(ref.path, await fs.readFile(ref.path, 'utf8')) : ref.name;
        const confirm = await vscode.window.showWarningMessage(
            `Remove the reference to ${ref.name}? The reference ${assemblyName} in model ${project.model} and the copy of ${assemblyName}.dll in its package go too. The ${ref.kind === 'project' ? 'C# project stays in the solution' : 'assembly file stays'}.`,
            { modal: true }, 'Remove');
        if (confirm !== 'Remove') {
            return;
        }
        const result = await this.server.bridge('remove_reference', { modelName: project.model, objectType: 'AxReference', objectName: assemblyName, name: ref.name, assemblyName });
        if (!result.success) {
            throw new Error(result.message ?? 'The reference was not removed.');
        }
        this.refresh();
        vscode.window.setStatusBarMessage(result.message ?? 'Removed.', 5000);
    }

    /** A C# class library for the model, next to the X++ project in its solution, referenced by the X++ project. */
    async newCSharpProject(): Promise<void> {
        const project = await this.project();
        const env = await readActiveEnvironment();
        const model = (await listModels()).find(m => m.name.toLowerCase() === (project.model ?? '').toLowerCase());
        const name = await vscode.window.showInputBox({
            title: 'New C# Project (1/3): name',
            value: `${project.model ?? project.name}Library`,
            prompt: 'Also the assembly name and the root namespace',
            validateInput: v => /^[A-Za-z_][A-Za-z0-9_]*(\.[A-Za-z_][A-Za-z0-9_]*)*$/.test(v) ? undefined : 'Letters, digits and _, parts separated by dots.',
        });
        if (!name) {
            return;
        }
        // Compiled against these packages' assemblies; your own package can't be one (it will reference this project).
        const dependencies = model ? await modelDependencies(path.dirname(model.folder), model.name) : [];
        const available = dependencies.filter(p => p.toLowerCase() !== model?.packageName.toLowerCase()
            && existsSync(path.join(env.frameworkDirectory, p, 'bin', `Dynamics.AX.${p}.dll`)));
        const packages = available.length ? await vscode.window.showQuickPick(
            available.map(p => ({ label: p, picked: Recommended.includes(p) })),
            { title: 'New C# Project (2/3): X++ packages the C# code uses (their assemblies are referenced, not copied)', canPickMany: true }) : [];
        if (!packages) {
            return;
        }
        const solution = await findSolution(project.file);
        const defaultParent = solution ? path.dirname(solution.file) : path.dirname(path.dirname(project.file));
        const where = await vscode.window.showQuickPick(
            [{ label: defaultParent, description: solution ? `next to ${path.basename(solution.file)}` : 'next to the X++ project\'s folder', dir: defaultParent },
                { label: '$(folder-opened) Browse…', description: '', dir: '' }],
            { title: `New C# Project (3/3): parent folder (the project goes to <parent>\\${name})` });
        if (!where) {
            return;
        }
        const parent = where.dir || (await vscode.window.showOpenDialog({ canSelectFolders: true, canSelectFiles: false, title: 'Parent folder for the C# project' }))?.[0]?.fsPath;
        if (!parent) {
            return;
        }
        const dir = path.join(parent, name);
        const csproj = path.join(dir, `${name}.csproj`);
        if (existsSync(csproj)) {
            throw new Error(`${csproj} already exists. Add it with Add Reference.`);
        }
        await fs.mkdir(dir, { recursive: true });
        await fs.writeFile(csproj, csharpProjectXml(name, packages.map(p => p.label)), 'utf8');
        const classFile = path.join(dir, 'Greeter.cs');
        await fs.writeFile(classFile, csharpClassText(name), 'utf8');
        this.output.appendLine(`Created ${csproj}.`);
        if (solution) {
            await addToSolution(solution.file, csproj).catch(err => this.output.appendLine(`${path.basename(csproj)} was not added to ${path.basename(solution.file)}: ${(err as Error).message}`));
        }
        await vscode.window.showTextDocument(vscode.Uri.file(classFile));
        await this.addProjectReference(project, csproj);
    }

    async buildCSharp(csproj: string | undefined): Promise<void> {
        if (csproj) {
            await this.build(csproj);
        }
    }

    private async build(csproj: string): Promise<string | undefined> {
        const msbuild = await findMsBuild(vscode.workspace.getConfiguration('xpp').get<string>('build.msbuildPath') || undefined);
        this.output.show(true);
        this.output.appendLine(`=== Build ${path.basename(csproj)} (${new Date().toLocaleTimeString()}) ===`);
        const assembly = await vscode.window.withProgress({ location: vscode.ProgressLocation.Notification, title: `Building ${path.basename(csproj)}`, cancellable: true },
            (_progress, token) => buildProject(csproj, msbuild, (exe, args) => runProcess(exe, args, line => this.output.appendLine(line), token)));
        if (!assembly) {
            vscode.window.showErrorMessage(`${path.basename(csproj)} did not build. See the X++ Build output.`);
        }
        return assembly;
    }

    private async project(): Promise<XppProjectInfo> {
        const project = await readProject().catch(() => undefined);
        if (!project?.model) {
            throw new Error('Open a D365FO project first: references belong to an X++ project and its model.');
        }
        return project;
    }
}

/** The packages a model depends on (ModuleReferences in its descriptor). */
async function modelDependencies(packageFolder: string, model: string): Promise<string[]> {
    const xml = await fs.readFile(path.join(packageFolder, 'Descriptor', `${model}.xml`), 'utf8').catch(() => '');
    const section = /<ModuleReferences[^>]*>([\s\S]*?)<\/ModuleReferences>/.exec(xml)?.[1] ?? '';
    return [...section.matchAll(/<(?:\w+:)?string>\s*([^<]+?)\s*<\/(?:\w+:)?string>/g)].map(m => m[1]);
}
