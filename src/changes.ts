import { execFile } from 'child_process';
import { promises as fs } from 'fs';
import * as path from 'path';
import * as vscode from 'vscode';
import { listModels, typeLabel } from './aot';
import { XppScheme, objectUri } from './codeView';

export interface ChangedElement {
    objectType: string;
    objectName: string;
    model: string;
    /** M modified, A added, D deleted, R renamed (Git's letters). */
    status: string;
    /** Changed in the working tree (not yet committed) rather than only on the branch. */
    uncommitted: boolean;
}

function git(args: string[], cwd: string): Promise<string> {
    return new Promise((resolve, reject) => execFile('git', args, { cwd, windowsHide: true, maxBuffer: 32 * 1024 * 1024 },
        (err, stdout) => err ? reject(err) : resolve(stdout)));
}

/** <AxType>/<Name>.xml under a model folder (other files of the model, e.g. descriptors, are not elements). */
export function elementOfPath(relativeToModel: string): { objectType: string; objectName: string } | undefined {
    const m = /^(Ax\w+)\/([^/]+)\.xml$/i.exec(relativeToModel.replace(/\\/g, '/'));
    return m ? { objectType: m[1], objectName: m[2] } : undefined;
}

/**
 * Elements of your models that differ from Git: uncommitted changes, plus (with a base branch) everything the branch
 * changed since it left the base. Model folders are often junctions into the repository, so their real paths are used.
 */
export async function changedElements(baseBranch?: string): Promise<ChangedElement[]> {
    const result = new Map<string, ChangedElement>();
    for (const model of (await listModels()).filter(m => m.isCustom)) {
        const real = await fs.realpath(model.folder).catch(() => undefined);
        const repo = real && (await git(['rev-parse', '--show-toplevel'], real).catch(() => '')).trim();
        if (!real || !repo) {
            continue;
        }
        const modelPath = path.relative(repo, real).replace(/\\/g, '/');
        const add = (file: string, status: string, uncommitted: boolean) => {
            const relative = file.replace(/^"|"$/g, '');
            if (!relative.toLowerCase().startsWith(modelPath.toLowerCase() + '/')) {
                return;
            }
            const element = elementOfPath(relative.slice(modelPath.length + 1));
            if (element) {
                const key = `${element.objectType}/${element.objectName}`.toLowerCase();
                const existing = result.get(key);
                result.set(key, { ...element, model: model.name, status: existing?.uncommitted ? existing.status : status, uncommitted: uncommitted || !!existing?.uncommitted });
            }
        };
        for (const line of (await git(['status', '--porcelain', '--untracked-files=all', '--', modelPath], repo).catch(() => '')).split('\n')) {
            if (line.length > 3) {
                const code = line.slice(0, 2).trim();
                add(line.slice(3).split(' -> ').pop()!, code === '??' ? 'A' : code[0], true);
            }
        }
        if (baseBranch) {
            const base = (await git(['merge-base', 'HEAD', baseBranch], repo).catch(() => '')).trim();
            if (base) {
                for (const line of (await git(['diff', '--name-status', base, 'HEAD', '--', modelPath], repo).catch(() => '')).split('\n')) {
                    const [status, ...files] = line.split('\t');
                    if (status && files.length) {
                        add(files[files.length - 1], status[0], false);
                    }
                }
            }
        }
    }
    return [...result.values()].sort((a, b) => a.objectType.localeCompare(b.objectType) || a.objectName.localeCompare(b.objectName));
}

const StatusText: Record<string, string> = { M: 'Modified', A: 'Added', D: 'Deleted', R: 'Renamed' };
const StatusColor: Record<string, string> = {
    M: 'gitDecoration.modifiedResourceForeground', A: 'gitDecoration.addedResourceForeground',
    D: 'gitDecoration.deletedResourceForeground', R: 'gitDecoration.renamedResourceForeground',
};

/**
 * Decorations of elements (project view items and code view tabs): the element type in the tooltip, and Git's change
 * letter and color for elements of your models that are changed.
 */
export class ChangeDecorations implements vscode.FileDecorationProvider, vscode.Disposable {
    private readonly changedEmitter = new vscode.EventEmitter<vscode.Uri[] | undefined>();
    readonly onDidChangeFileDecorations = this.changedEmitter.event;
    private changes = new Map<string, ChangedElement>();
    private readonly timer: NodeJS.Timeout;
    private readonly subscriptions: vscode.Disposable[];

    constructor() {
        this.subscriptions = [vscode.window.registerFileDecorationProvider(this), vscode.workspace.onDidSaveTextDocument(() => void this.refresh())];
        this.timer = setInterval(() => void this.refresh(), 30_000);
        void this.refresh();
    }

    async refresh(): Promise<void> {
        const base = vscode.workspace.getConfiguration('xpp').get<string>('git.baseBranch') || undefined;
        const next = new Map((await changedElements(base).catch(() => [])).map(c => [`${c.objectType}/${c.objectName}`.toLowerCase(), c]));
        const keys = new Set([...this.changes.keys(), ...next.keys()]);
        const differs = [...keys].filter(k => JSON.stringify(this.changes.get(k)) !== JSON.stringify(next.get(k)));
        const previous = this.changes;
        this.changes = next;
        if (differs.length) {
            this.changedEmitter.fire(differs.map(k => {
                const change = (next.get(k) ?? previous.get(k))!;
                return objectUri(change.objectType, change.objectName);
            }));
        }
    }

    provideFileDecoration(uri: vscode.Uri): vscode.FileDecoration | undefined {
        if (uri.scheme !== XppScheme) {
            return undefined;
        }
        const [, objectType, file] = uri.path.split('/');
        if (!objectType || !file) {
            return undefined;
        }
        const name = file.replace(/\.xpp$/i, '');
        const change = this.changes.get(`${objectType}/${name}`.toLowerCase());
        const type = typeLabel(objectType).replace(/s$/, '');
        if (!change) {
            return { tooltip: type };
        }
        return {
            badge: change.status,
            color: new vscode.ThemeColor(StatusColor[change.status] ?? StatusColor.M),
            tooltip: `${type} · ${StatusText[change.status] ?? 'Changed'}${change.uncommitted ? '' : ' on this branch'}`,
            propagate: false,
        };
    }

    dispose(): void {
        clearInterval(this.timer);
        this.subscriptions.forEach(s => s.dispose());
        this.changedEmitter.dispose();
    }
}
