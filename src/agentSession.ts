import { existsSync, promises as fs } from 'fs';
import * as os from 'os';
import * as path from 'path';
import * as vscode from 'vscode';
import { listModels, typeName } from './aot';
import { findActiveProject, readProject } from './project';
import { XppServerClient } from './serverClient';

interface SessionState { checkpoint?: string; scope?: string[] }
interface Entry { objectType: string; objectName: string; model?: string; existed: boolean }

const Folder = path.join(process.env.LOCALAPPDATA ?? os.homedir(), 'XppVsCode');
const SessionFile = path.join(Folder, 'agentSession.json');

/**
 * Copilot checkpoints and scope. The MCP server (Copilot's process) reads agentSession.json before every write: it refuses
 * elements outside the scope and, while a checkpoint is open, keeps each element's state before its first change. Revert
 * restores them through this window's server (metadata API) and puts back the project file and label files copied here.
 */
export class AgentSession implements vscode.Disposable {
    private readonly item = vscode.window.createStatusBarItem('xpp.agentSession', vscode.StatusBarAlignment.Left, -4);
    private timer: NodeJS.Timeout | undefined;

    constructor(private readonly server: XppServerClient, private readonly refreshTrees: () => void) {
        this.item.name = 'Copilot checkpoint';
        this.item.command = 'xpp.agent.checkpointMenu';
        void this.render();
    }

    dispose(): void {
        clearInterval(this.timer);
        this.item.dispose();
    }

    register(command: (id: string, handler: (...args: any[]) => unknown) => void): void {
        command('xpp.agent.startCheckpoint', () => this.start());
        command('xpp.agent.revertCheckpoint', () => this.revert());
        command('xpp.agent.endCheckpoint', () => this.end(true));
        command('xpp.agent.checkpointMenu', () => this.menu());
        command('xpp.agent.setScope', () => this.setScope());
    }

    private async start(): Promise<void> {
        const state = await readState();
        if (state.checkpoint) {
            const choice = await vscode.window.showWarningMessage('A Copilot checkpoint is already open.', 'Keep Its Changes and Start a New One', 'Cancel');
            if (choice !== 'Keep Its Changes and Start a New One') {
                return;
            }
            await this.end(false);
        }
        const id = new Date().toISOString().replace(/[:.]/g, '-');
        const folder = path.join(Folder, 'checkpoints', id);
        await fs.mkdir(path.join(folder, 'files'), { recursive: true });
        // Project file and label texts are not metadata elements: they are copied as files.
        const files: { source: string; copy: string }[] = [];
        const copy = async (source: string) => {
            const target = path.join(folder, 'files', `${files.length}_${path.basename(source)}`);
            await fs.copyFile(source, target);
            files.push({ source, copy: target });
        };
        const project = await findActiveProject();
        if (project && existsSync(project)) {
            await copy(project);
        }
        for (const model of (await listModels()).filter(m => m.isCustom)) {
            for (const file of await labelTextFiles(path.join(model.folder, 'AxLabelFile', 'LabelResources'))) {
                await copy(file);
            }
        }
        await fs.writeFile(path.join(folder, 'files.json.txt'), JSON.stringify(files), 'utf8');
        await writeState({ ...state, checkpoint: id });
        await this.render();
        vscode.window.showInformationMessage('Copilot checkpoint started: every element Copilot changes from now on can be reverted.');
    }

    private async menu(): Promise<void> {
        const state = await readState();
        if (!state.checkpoint) {
            await this.start();
            return;
        }
        const entries = await this.entries(state.checkpoint);
        const pick = await vscode.window.showQuickPick<vscode.QuickPickItem & { entry?: Entry; action: string }>([
            ...entries.map(e => ({ label: `$(${e.existed ? 'edit' : 'add'}) ${e.objectName}`, description: `${typeName(e.objectType)}${e.existed ? '' : ' (new)'}`, entry: e, action: '' })),
            { label: '', kind: vscode.QuickPickItemKind.Separator, action: '' },
            { label: '$(discard) Revert Everything Copilot Changed\u2026', action: 'revert' },
            { label: '$(check) Keep the Changes (End Checkpoint)', action: 'end' },
        ], { title: `Copilot checkpoint: ${entries.length} element(s) changed` });
        if (pick?.entry) {
            await vscode.commands.executeCommand('xpp.openDesigner', pick.entry.objectType, pick.entry.objectName);
        } else if (pick?.action === 'revert') {
            await this.revert();
        } else if (pick?.action === 'end') {
            await this.end(true);
        }
    }

    private async revert(): Promise<void> {
        const state = await readState();
        if (!state.checkpoint) {
            vscode.window.showInformationMessage('No Copilot checkpoint is open.');
            return;
        }
        const id = state.checkpoint;
        const entries = await this.entries(id);
        const confirm = await vscode.window.showWarningMessage(`Revert ${entries.length} element(s) Copilot changed since the checkpoint?`, {
            modal: true,
            detail: [...entries.slice(0, 15).map(e => `${e.existed ? 'Restore' : 'Delete'} ${typeName(e.objectType)} ${e.objectName}`), entries.length > 15 ? '\u2026' : '',
                'The project file and label files go back to their state at the checkpoint, including your own changes to them since.'].filter(Boolean).join('\n'),
        }, 'Revert');
        if (confirm !== 'Revert') {
            return;
        }
        const result = await vscode.window.withProgress({ location: vscode.ProgressLocation.Notification, title: 'Reverting Copilot\'s changes\u2026' },
            () => this.server.bridge('revert_checkpoint', { id }));
        const folder = path.join(Folder, 'checkpoints', id);
        const files: { source: string; copy: string }[] = JSON.parse(await fs.readFile(path.join(folder, 'files.json.txt'), 'utf8').catch(() => '[]'));
        for (const f of files) {
            await fs.copyFile(f.copy, f.source).catch(() => undefined);
        }
        this.refreshTrees();
        if (result.success) {
            await this.end(false);
            vscode.window.showInformationMessage(String(result.message ?? 'Reverted.'));
        } else {
            vscode.window.showErrorMessage(`${result.message ?? 'Revert failed.'} The checkpoint stays open.`);
        }
    }

    private async end(ask: boolean): Promise<void> {
        const state = await readState();
        if (!state.checkpoint) {
            return;
        }
        if (ask) {
            const ok = await vscode.window.showInformationMessage('Keep Copilot\'s changes and end the checkpoint? They can no longer be reverted here (only through Git).', 'End Checkpoint');
            if (ok !== 'End Checkpoint') {
                return;
            }
        }
        await fs.rm(path.join(Folder, 'checkpoints', state.checkpoint), { recursive: true, force: true }).catch(() => undefined);
        await writeState({ ...state, checkpoint: undefined });
        await this.render();
    }

    private async setScope(): Promise<void> {
        const state = await readState();
        const project = await readProject().catch(() => undefined);
        const choice = await vscode.window.showQuickPick([
            { label: '$(project) The Project\'s Elements', detail: project ? `${project.items.length} element(s) of ${project.name}, plus new elements Copilot creates` : 'Open a project first', id: 'project' },
            { label: '$(checklist) Pick Elements\u2026', detail: 'Some of the project\'s elements, plus new elements Copilot creates', id: 'pick' },
            { label: '$(circle-slash) No Limit', detail: 'Copilot may change any element of your models (the default)', id: 'none' },
        ], { title: `Elements Copilot may change${state.scope ? ` (now: ${state.scope.length})` : ' (now: no limit)'}` });
        if (!choice) {
            return;
        }
        let scope: string[] | undefined;
        if (choice.id === 'project' || choice.id === 'pick') {
            if (!project) {
                throw new Error('Open a D365FO project first.');
            }
            const all = project.items.map(i => `${i.objectType}/${i.objectName}`);
            if (choice.id === 'pick') {
                const picked = await vscode.window.showQuickPick(project.items.map(i => ({
                    label: i.objectName, description: typeName(i.objectType), picked: state.scope?.includes(`${i.objectType}/${i.objectName}`) ?? false, key: `${i.objectType}/${i.objectName}`,
                })), { title: 'Elements Copilot may change', canPickMany: true });
                if (!picked) {
                    return;
                }
                scope = picked.map(p => p.key);
            } else {
                scope = all;
            }
        }
        await writeState({ ...state, scope });
        await this.render();
        vscode.window.setStatusBarMessage(scope ? `$(lock) Copilot may change ${scope.length} element(s) and the ones it creates` : '$(unlock) Copilot may change any element of your models', 5000);
    }

    private async entries(id: string): Promise<Entry[]> {
        const result = await this.server.bridge('checkpoint_entries', { id }).catch(() => undefined);
        return (result?.entries ?? []) as Entry[];
    }

    private async render(): Promise<void> {
        const state = await readState();
        clearInterval(this.timer);
        if (!state.checkpoint && !state.scope) {
            this.item.hide();
            return;
        }
        const update = async () => {
            const count = state.checkpoint ? (await this.entries(state.checkpoint)).length : 0;
            this.item.text = `${state.scope ? '$(lock)' : ''}${state.checkpoint ? `$(history) ${count}` : ''}`;
            this.item.tooltip = [state.checkpoint ? `Copilot checkpoint: ${count} element(s) changed. Click to review or revert.` : '',
                state.scope ? `Copilot may change ${state.scope.length} element(s) and the ones it creates (F&O Studio: Set Copilot Scope).` : ''].filter(Boolean).join('\n');
        };
        await update();
        this.item.show();
        if (state.checkpoint) {
            this.timer = setInterval(() => void update(), 10_000);
        }
    }
}

async function readState(): Promise<SessionState> {
    try {
        return JSON.parse(await fs.readFile(SessionFile, 'utf8')) as SessionState;
    } catch {
        return {};
    }
}

async function writeState(state: SessionState): Promise<void> {
    await fs.mkdir(Folder, { recursive: true });
    const clean: SessionState = {};
    if (state.checkpoint) {
        clean.checkpoint = state.checkpoint;
    }
    if (state.scope) {
        clean.scope = state.scope;
    }
    await fs.writeFile(SessionFile, JSON.stringify(clean), 'utf8');
}

async function labelTextFiles(folder: string): Promise<string[]> {
    const result: string[] = [];
    for (const language of await fs.readdir(folder, { withFileTypes: true }).catch(() => [])) {
        if (language.isDirectory()) {
            const dir = path.join(folder, language.name);
            result.push(...(await fs.readdir(dir).catch(() => [] as string[])).filter(f => f.endsWith('.label.txt')).map(f => path.join(dir, f)));
        }
    }
    return result;
}
