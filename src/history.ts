import { execFile } from 'child_process';
import { promises as fs } from 'fs';
import * as path from 'path';
import * as vscode from 'vscode';
import { CodeTypes, listModels, typeLabel } from './aot';
import { parseObjectUri, XppScheme } from './codeView';
import { elementOf, pickElement, TreeArg } from './d365Tools';
import { XppServerClient } from './serverClient';
import { composeSource } from './xppSource';

/** A version of an element's metadata file: xpphistory:/<ref>/<path in the repository>?<repository root>#<code|xml>. */
const HistoryScheme = 'xpphistory';

/** Versions are commits, HEAD, the file on disk (working) or nothing (before the first commit); anything else is refused. */
const VersionRef = /^(HEAD|working|empty|[0-9a-f]{40})$/;

interface Commit {
    hash: string;
    parent?: string;
    author: string;
    date: Date;
    subject: string;
}

interface ElementFile {
    objectType: string;
    objectName: string;
    repo: string;
    relative: string;
}

/** Visual Studio's View History: the Git commits of an element's metadata file, each opening as a diff. */
export function registerHistory(context: vscode.ExtensionContext, server: XppServerClient,
    command: (id: string, handler: (...args: any[]) => unknown) => void): void {
    context.subscriptions.push(vscode.workspace.registerTextDocumentContentProvider(HistoryScheme, new HistoryDocuments()));
    command('xpp.viewHistory', (arg?: TreeArg | vscode.Uri) => viewHistory(server, arg));
}

class HistoryDocuments implements vscode.TextDocumentContentProvider {
    async provideTextDocumentContent(uri: vscode.Uri): Promise<string> {
        const [, ref, ...rest] = uri.path.split('/');
        const relative = rest.join('/');
        if (!VersionRef.test(ref) || ref === 'empty' || relative.split('/').includes('..')) {
            return '';
        }
        const xml = ref === 'working'
            ? await fs.readFile(path.join(uri.query, relative), 'utf8').catch(() => '')
            : await git(['show', `${ref}:${relative}`], uri.query).catch(() => '');
        return uri.fragment === 'code' ? codeOf(xml) : xml;
    }
}

async function viewHistory(server: XppServerClient, arg?: TreeArg | vscode.Uri): Promise<void> {
    const element = arg instanceof vscode.Uri
        ? (arg.scheme === XppScheme ? parseObjectUri(arg) : undefined)
        : elementOf(arg) ?? await pickElement(server, 'View history');
    if (!element) {
        return;
    }
    const target = await elementFile(element.objectType, element.objectName);
    if (!target) {
        return;
    }
    const format = ['%H', '%P', '%an', '%aI', '%s'].join('%x1f');
    const log = await git(['log', '-n', '500', `--format=${format}`, '--', target.relative], target.repo);
    const commits: Commit[] = log.split('\n').filter(l => l.trim()).map(line => {
        const [hash, parents, author, date, subject] = line.split('\x1f');
        return { hash, parent: parents.split(' ')[0] || undefined, author, date: new Date(date), subject };
    });
    const pending = (await git(['status', '--porcelain', '--', target.relative], target.repo)).trim().length > 0;
    if (commits.length === 0 && !pending) {
        vscode.window.showInformationMessage(`${target.objectName} has no Git history yet.`);
        return;
    }
    showCommits(target, commits, pending);
}

type CommitItem = vscode.QuickPickItem & { commit?: Commit };

const CompareWithCurrent: vscode.QuickInputButton = { iconPath: new vscode.ThemeIcon('compare-changes'), tooltip: 'Compare with the current version' };
const ShowMetadata: vscode.QuickInputButton = { iconPath: new vscode.ThemeIcon('file-code'), tooltip: 'Show the metadata (XML) changes' };

function showCommits(target: ElementFile, commits: Commit[], pending: boolean): void {
    const hasCode = CodeTypes.has(target.objectType);
    const pick = vscode.window.createQuickPick<CommitItem>();
    pick.title = `History of ${target.objectName} (${typeLabel(target.objectType).replace(/s$/, '')})`;
    pick.placeholder = hasCode ? 'Pick a commit to see its code changes' : 'Pick a commit to see its changes';
    pick.matchOnDescription = true;
    pick.matchOnDetail = true;
    pick.items = [
        ...(pending ? [{ label: '$(edit) Uncommitted changes', description: 'working tree', buttons: hasCode ? [ShowMetadata] : [] }] : []),
        ...commits.map(c => ({
            label: c.subject,
            description: `${c.author} · ${c.date.toLocaleString()}`,
            detail: c.hash.slice(0, 8),
            commit: c,
            buttons: hasCode ? [CompareWithCurrent, ShowMetadata] : [CompareWithCurrent],
        })),
    ];
    pick.onDidAccept(() => {
        const item = pick.selectedItems[0];
        if (item) {
            void showChange(target, item.commit, hasCode ? 'code' : 'xml', false);
        }
    });
    pick.onDidTriggerItemButton(e => {
        void showChange(target, e.item.commit, e.button === ShowMetadata || !hasCode ? 'xml' : 'code', e.button === CompareWithCurrent);
    });
    pick.onDidHide(() => pick.dispose());
    pick.show();
}

/** A commit's change (parent ↔ commit), the commit against the current version, or the uncommitted changes (no commit). */
async function showChange(target: ElementFile, commit: Commit | undefined, view: 'code' | 'xml', withCurrent: boolean): Promise<void> {
    const version = (ref: string) => vscode.Uri.from({ scheme: HistoryScheme, path: `/${ref}/${target.relative}`, query: target.repo, fragment: view });
    // The current version is read from disk like the others, so both sides of the diff have the same layout.
    const current = version('working');
    const short = commit?.hash.slice(0, 8);
    if (!commit) {
        await vscode.commands.executeCommand('vscode.diff', version('HEAD'), current, `${target.objectName} (HEAD ↔ working tree)`);
    } else if (withCurrent) {
        await vscode.commands.executeCommand('vscode.diff', version(commit.hash), current, `${target.objectName} (${short} ↔ current)`);
    } else {
        await vscode.commands.executeCommand('vscode.diff', version(commit.parent ?? 'empty'), version(commit.hash), `${target.objectName} (${short}: ${commit.subject})`);
    }
}

/** The element's metadata file (your own models first) and the Git repository it is in. */
async function elementFile(objectType: string, objectName: string): Promise<ElementFile | undefined> {
    const models = (await listModels()).sort((a, b) => Number(b.isCustom) - Number(a.isCustom));
    let file: string | undefined;
    for (const model of models) {
        const candidate = path.join(model.folder, objectType, `${objectName}.xml`);
        if (await fs.access(candidate).then(() => true, () => false)) {
            file = candidate;
            break;
        }
    }
    if (!file) {
        vscode.window.showInformationMessage(`No metadata file found for ${objectName}.`);
        return undefined;
    }
    // Model folders are often junctions into the repository: Git needs the real path.
    const real = await fs.realpath(file);
    const repo = (await git(['rev-parse', '--show-toplevel'], path.dirname(real)).catch(() => '')).trim();
    if (!repo) {
        vscode.window.showInformationMessage(`${objectName} is not under Git source control (${path.dirname(real)}).`);
        return undefined;
    }
    return { objectType, objectName, repo, relative: path.relative(repo, real).replace(/\\/g, '/') };
}

/**
 * The X++ code of a metadata file version: the declaration and the element's methods laid out like the code view, then
 * the methods of data sources, their fields and controls (forms), each group under a comment naming its node.
 */
function codeOf(xml: string): string {
    const source = /<SourceCode>([\s\S]*)<\/SourceCode>/.exec(xml)?.[1] ?? '';
    let declaration = '';
    const groups = new Map<string, { name: string; source: string }[]>();
    // Tokens: CDATA sections, tags and text; each open element remembers the <Name> (or <DataField>) given inside it.
    const stack: { tag: string; name?: string }[] = [];
    let method: { name?: string; source?: string } | undefined;
    let text = '';
    for (const m of source.matchAll(/<!\[CDATA\[([\s\S]*?)\]\]>|<(\/?)([A-Za-z][\w.:-]*)[^>]*?(\/?)>|([^<]+)/g)) {
        if (m[1] !== undefined || m[5] !== undefined) {
            text += m[1] ?? m[5];
            continue;
        }
        const [closing, tag, selfClosing] = [m[2] === '/', m[3], m[4] === '/'];
        if (!closing) {
            text = '';
            if (!selfClosing) {
                stack.push({ tag });
            }
            if (tag === 'Method') {
                method = {};
            }
            continue;
        }
        stack.pop();
        const parent = stack[stack.length - 1];
        if (tag === 'Declaration' && stack.length === 0) {
            declaration = text;
        } else if (method && tag === 'Name' && parent?.tag === 'Method') {
            method.name = text.trim();
        } else if (method && tag === 'Source' && parent?.tag === 'Method') {
            method.source = text;
        } else if ((tag === 'Name' || tag === 'DataField') && parent && !parent.name && parent.tag !== 'Method') {
            parent.name = text.trim();
        } else if (tag === 'Method' && method) {
            const owner = stack.filter(f => f.name).map(f => f.name).join('/');
            const list = groups.get(owner) ?? [];
            list.push({ name: method.name ?? '', source: method.source ?? '' });
            groups.set(owner, list);
            method = undefined;
        }
        text = '';
    }
    const own = groups.get('') ?? [];
    const parts = [declaration || own.length ? composeSource(declaration, own) : ''];
    for (const [owner, methods] of groups) {
        if (owner) {
            parts.push(`// ${owner}\n` + methods.map(x => x.source.replace(/^\s*\n/, '').replace(/\s+$/, '')).join('\n\n') + '\n');
        }
    }
    return parts.filter(Boolean).join('\n');
}

function git(args: string[], cwd: string): Promise<string> {
    const exe = vscode.workspace.getConfiguration('git').get<string | string[]>('path');
    const gitPath = (Array.isArray(exe) ? exe[0] : exe) || 'git';
    return new Promise((resolve, reject) => execFile(gitPath, args, { cwd, maxBuffer: 256 * 1024 * 1024, windowsHide: true },
        (err, stdout, stderr) => err ? reject(new Error(stderr || err.message)) : resolve(stdout)));
}
