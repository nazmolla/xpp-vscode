import { existsSync, promises as fs } from 'fs';
import * as path from 'path';
import * as vscode from 'vscode';
import { XppBuilder } from './build';
import { readActiveEnvironment } from './environment';

interface RepoPackage { name: string; folder: string; references: string[] }

const WorkspaceFile = path.join('.xpp', 'workspace.json');

/**
 * F&O Studio: Open Team Repository: the packages of a team's repository (Metadata\<Package>\Descriptor\*.xml) are
 * linked into the custom metadata folder (junctions, the way teams set up Visual Studio), built in dependency order, and
 * the repository's project opened. .xpp/workspace.json records the packages for the next developer.
 */
export async function openTeamRepository(builder: XppBuilder, openProject: (file: string) => Promise<void>): Promise<void> {
    const source = await vscode.window.showQuickPick([
        { label: '$(repo-clone) Clone a Repository…', id: 'clone' },
        { label: '$(folder-opened) Use a Folder Already on This Machine…', id: 'folder' },
    ], { title: 'Open team repository' });
    if (!source) {
        return;
    }
    let root: string | undefined;
    if (source.id === 'clone') {
        const url = await vscode.window.showInputBox({ title: 'Repository URL', placeHolder: 'https://dev.azure.com/org/project/_git/repo', ignoreFocusOut: true,
            validateInput: v => /^(https:\/\/|git@)/.test(v.trim()) ? undefined : 'An https:// or git@ URL.' });
        const parent = url && (await vscode.window.showOpenDialog({ title: 'Folder to clone into', canSelectFolders: true, canSelectFiles: false, openLabel: 'Clone Here' }))?.[0];
        if (!url || !parent) {
            return;
        }
        // VS Code's Git extension clones (with its sign-in) and returns the new folder.
        root = await vscode.commands.executeCommand<string>('git.clone', url.trim(), parent.fsPath);
    } else {
        root = (await vscode.window.showOpenDialog({ title: 'Repository folder', canSelectFolders: true, canSelectFiles: false, openLabel: 'Use Repository' }))?.[0]?.fsPath;
    }
    if (!root || !existsSync(root)) {
        return;
    }

    const env = await readActiveEnvironment();
    const packages = await repositoryPackages(root);
    if (!packages.length) {
        throw new Error(`No D365FO packages found in ${root} (folders with a Descriptor folder holding model descriptors).`);
    }
    const picked = await vscode.window.showQuickPick(packages.map(p => ({ label: p.name, description: path.relative(root!, p.folder), picked: true, pkg: p })),
        { title: `Packages to link into ${env.modelStoreFolder}`, canPickMany: true, ignoreFocusOut: true });
    if (!picked?.length) {
        return;
    }
    const chosen = picked.map(p => p.pkg);

    const notes: string[] = [];
    for (const pkg of chosen) {
        const target = path.join(env.modelStoreFolder, pkg.name);
        const existing = await fs.lstat(target).catch(() => undefined);
        if (existing) {
            const linkTarget = existing.isSymbolicLink() ? await fs.readlink(target).catch(() => '') : '';
            if (path.resolve(linkTarget).toLowerCase() !== path.resolve(pkg.folder).toLowerCase()) {
                notes.push(`${pkg.name}: ${target} already exists and is not a link to this repository; it was left as it is.`);
            }
            continue;
        }
        await fs.symlink(pkg.folder, target, 'junction');
    }
    await writeWorkspaceFile(root, chosen);
    await vscode.commands.executeCommand('xpp.refreshAot');

    const order = buildOrder(chosen);
    const failed: string[] = [];
    await vscode.window.withProgress({ location: vscode.ProgressLocation.Notification, title: 'Building the team\'s packages', cancellable: true }, async (progress, token) => {
        for (const [i, pkg] of order.entries()) {
            if (token.isCancellationRequested) {
                return;
            }
            progress.report({ message: `${pkg.name} (${i + 1}/${order.length})`, increment: 100 / order.length });
            if (!await builder.build(pkg.name, token).catch(() => false)) {
                failed.push(pkg.name);
            }
        }
    });

    const projects = (await vscode.workspace.findFiles(new vscode.RelativePattern(root, '**/*.rnrproj'), '**/node_modules/**', 20)).map(u => u.fsPath);
    const summary = `Linked ${chosen.length} package(s) into ${env.modelStoreFolder}; ${failed.length ? `build failed for ${failed.join(', ')} (see Problems)` : 'all built'}.`;
    void vscode.window.showInformationMessage([summary, ...notes].join(' '), ...env.local ? ['Synchronize Database'] : ['Deploy', 'Synchronize Database'])
        .then(choice => choice === 'Deploy' ? vscode.commands.executeCommand('xpp.pac.deploy')
            : choice === 'Synchronize Database' ? vscode.commands.executeCommand('xpp.pac.dbSync') : undefined);
    if (projects.length) {
        const project = projects.length === 1 ? projects[0]
            : (await vscode.window.showQuickPick(projects.map(p => ({ label: path.basename(p, '.rnrproj'), description: path.relative(root!, p), file: p })),
                { title: 'Open a project of the repository' }))?.file;
        if (project) {
            await openProject(project);
        }
    }
}

/** The packages recorded in .xpp/workspace.json, else every folder with a Descriptor folder of model descriptors. */
async function repositoryPackages(root: string): Promise<RepoPackage[]> {
    const recorded = await fs.readFile(path.join(root, WorkspaceFile), 'utf8').then(t => JSON.parse(t) as { packages?: string[] }).catch(() => undefined);
    const folders = recorded?.packages?.length
        ? recorded.packages.map(p => path.resolve(root, p)).filter(p => p.startsWith(path.resolve(root)) && existsSync(p))
        : await findPackageFolders(root, 4);
    const result: RepoPackage[] = [];
    for (const folder of folders) {
        const descriptors = (await fs.readdir(path.join(folder, 'Descriptor')).catch(() => [] as string[])).filter(f => f.toLowerCase().endsWith('.xml'));
        const references = new Set<string>();
        for (const file of descriptors) {
            const text = await fs.readFile(path.join(folder, 'Descriptor', file), 'utf8').catch(() => '');
            const block = /<ModuleReferences[^>]*>([\s\S]*?)<\/ModuleReferences>/.exec(text)?.[1] ?? '';
            for (const m of block.matchAll(/<[\w:]*string>([^<]+)<\/[\w:]*string>/g)) {
                references.add(m[1].trim());
            }
        }
        result.push({ name: path.basename(folder), folder, references: [...references] });
    }
    return result;
}

async function findPackageFolders(folder: string, depth: number): Promise<string[]> {
    const entries = await fs.readdir(folder, { withFileTypes: true }).catch(() => []);
    if (entries.some(e => e.isDirectory() && e.name.toLowerCase() === 'descriptor')) {
        return [folder];
    }
    if (depth === 0) {
        return [];
    }
    const found: string[] = [];
    for (const e of entries.filter(e => e.isDirectory() && !e.name.startsWith('.') && !/^(node_modules|bin|obj)$/i.test(e.name))) {
        found.push(...await findPackageFolders(path.join(folder, e.name), depth - 1));
    }
    return found;
}

/** Referenced packages first; references outside the repository (standard packages) are already built. */
function buildOrder(packages: RepoPackage[]): RepoPackage[] {
    const byName = new Map(packages.map(p => [p.name.toLowerCase(), p]));
    const done = new Set<string>();
    const order: RepoPackage[] = [];
    const visit = (p: RepoPackage, path: Set<string>) => {
        const key = p.name.toLowerCase();
        if (done.has(key) || path.has(key)) {
            return;
        }
        path.add(key);
        for (const ref of p.references) {
            const dep = byName.get(ref.toLowerCase());
            if (dep) {
                visit(dep, path);
            }
        }
        done.add(key);
        order.push(p);
    };
    packages.forEach(p => visit(p, new Set()));
    return order;
}

async function writeWorkspaceFile(root: string, packages: RepoPackage[]): Promise<void> {
    const file = path.join(root, WorkspaceFile);
    if (existsSync(file)) {
        return;
    }
    await fs.mkdir(path.dirname(file), { recursive: true });
    const content = { packages: packages.map(p => path.relative(root, p.folder).replace(/\\/g, '/')) };
    await fs.writeFile(file, JSON.stringify(content, null, 2) + '\n', 'utf8');
}
