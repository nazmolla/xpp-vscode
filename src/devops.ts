import { execFile } from 'child_process';
import { promises as fs } from 'fs';
import * as vscode from 'vscode';
import { listModels } from './aot';

/** Where the repository is hosted, from its origin URL. */
export interface RepositoryHost {
    kind: 'azure' | 'github';
    /** Azure DevOps: https://dev.azure.com/<org>/<project>; GitHub: https://github.com/<owner>/<repo>. */
    baseUrl: string;
    repository: string;
}

export function parseRemote(url: string): RepositoryHost | undefined {
    const clean = url.trim().replace(/\.git$/, '');
    let m = /^https:\/\/(?:[^@/]+@)?dev\.azure\.com\/([^/]+)\/([^/]+)\/_git\/([^/?#]+)/i.exec(clean)
        ?? /^git@ssh\.dev\.azure\.com:v3\/([^/]+)\/([^/]+)\/([^/?#]+)/i.exec(clean);
    if (m) {
        return { kind: 'azure', baseUrl: `https://dev.azure.com/${m[1]}/${m[2]}`, repository: decodeURIComponent(m[3]) };
    }
    m = /^https:\/\/(?:[^@/]+@)?([^./]+)\.visualstudio\.com\/(?:DefaultCollection\/)?([^/]+)\/_git\/([^/?#]+)/i.exec(clean);
    if (m) {
        return { kind: 'azure', baseUrl: `https://dev.azure.com/${m[1]}/${m[2]}`, repository: decodeURIComponent(m[3]) };
    }
    m = /^(?:https:\/\/github\.com\/|git@github\.com:)([^/]+)\/([^/?#]+)/i.exec(clean);
    return m ? { kind: 'github', baseUrl: `https://github.com/${m[1]}/${m[2]}`, repository: m[2] } : undefined;
}

function git(args: string[], cwd: string): Promise<string> {
    return new Promise((resolve, reject) => execFile('git', args, { cwd, windowsHide: true }, (err, stdout) => err ? reject(err) : resolve(stdout.trim())));
}

/** The Git repository of the workspace, else of the first of your models in one (team repositories link their packages). */
async function repositoryRoot(): Promise<string | undefined> {
    const candidates = [...(vscode.workspace.workspaceFolders ?? []).map(f => f.uri.fsPath)];
    for (const model of (await listModels().catch(() => [])).filter(m => m.isCustom)) {
        candidates.push(await fs.realpath(model.folder).catch(() => model.folder));
    }
    for (const folder of candidates) {
        const root = await git(['rev-parse', '--show-toplevel'], folder).catch(() => '');
        if (root) {
            return root;
        }
    }
    return undefined;
}

async function host(): Promise<{ root: string; host: RepositoryHost } | undefined> {
    const root = await repositoryRoot();
    const url = root && await git(['remote', 'get-url', 'origin'], root).catch(() => '');
    const parsed = url ? parseRemote(url) : undefined;
    if (!root || !parsed) {
        vscode.window.showErrorMessage('The workspace is not in a Git repository with an Azure DevOps or GitHub origin.');
        return undefined;
    }
    return { root, host: parsed };
}

/** Work item IDs named in the branch's commit messages (#123, AB#123), for review and the pull request. */
export async function branchWorkItems(baseBranch: string): Promise<number[]> {
    const root = await repositoryRoot();
    if (!root) {
        return [];
    }
    const log = await git(['log', '--format=%B', `${baseBranch}..HEAD`], root).catch(() => '');
    return [...new Set([...log.matchAll(/(?:^|[^\w&])(?:AB)?#(\d{1,9})\b/g)].map(m => Number(m[1])))].sort((a, b) => a - b);
}

export function workItemUrl(repository: RepositoryHost, id: number): string | undefined {
    return repository.kind === 'azure' ? `${repository.baseUrl}/_workitems/edit/${id}` : undefined;
}

/**
 * F&O Studio: Link Work Item…: puts the work item mention into the commit message (#123 for Azure Repos, AB#123 for
 * GitHub repositories linked to Azure Boards), so the commit, and the pull request, link to it.
 */
export async function linkWorkItem(): Promise<void> {
    const found = await host();
    if (!found) {
        return;
    }
    const id = await vscode.window.showInputBox({ title: 'Link work item', prompt: 'Azure Boards work item ID', validateInput: v => /^\d{1,9}$/.test(v.trim()) ? undefined : 'A work item number.' });
    if (!id) {
        return;
    }
    const mention = `${found.host.kind === 'azure' ? '' : 'AB'}#${id.trim()}`;
    const gitApi = vscode.extensions.getExtension<any>('vscode.git')?.exports?.getAPI(1);
    const repository = gitApi?.repositories?.find((r: any) => r.rootUri.fsPath.toLowerCase() === found.root.toLowerCase()) ?? gitApi?.repositories?.[0];
    if (!repository) {
        await vscode.env.clipboard.writeText(mention);
        vscode.window.showInformationMessage(`${mention} is on the clipboard: add it to your commit message.`);
        return;
    }
    const current: string = repository.inputBox.value ?? '';
    if (!current.includes(mention)) {
        repository.inputBox.value = current.trim() ? `${current.trimEnd()} ${mention}` : `${mention} `;
    }
    await vscode.commands.executeCommand('workbench.view.scm');
}

/** F&O Studio: Open Work Item…: the work item in Azure Boards. */
export async function openWorkItem(id?: number): Promise<void> {
    const found = await host();
    if (!found) {
        return;
    }
    const value = id ?? Number(await vscode.window.showInputBox({ title: 'Open work item', prompt: 'Work item ID', validateInput: v => /^\d{1,9}$/.test(v.trim()) ? undefined : 'A work item number.' }));
    if (!value) {
        return;
    }
    const url = workItemUrl(found.host, value);
    if (!url) {
        vscode.window.showInformationMessage('The repository is on GitHub: open the work item from Azure Boards (AB# mentions link there).');
        return;
    }
    await vscode.env.openExternal(vscode.Uri.parse(url));
}

/** F&O Studio: Open Pipelines: the build pipelines of the repository's project (Azure Pipelines) or its GitHub Actions. */
export async function openPipelines(): Promise<void> {
    const found = await host();
    if (!found) {
        return;
    }
    const branch = await git(['rev-parse', '--abbrev-ref', 'HEAD'], found.root).catch(() => '');
    const url = found.host.kind === 'azure'
        ? `${found.host.baseUrl}/_build`
        : `${found.host.baseUrl}/actions${branch && branch !== 'HEAD' ? `?query=${encodeURIComponent(`branch:${branch}`)}` : ''}`;
    await vscode.env.openExternal(vscode.Uri.parse(url));
}
