import { existsSync, promises as fs } from 'fs';
import * as path from 'path';
import * as vscode from 'vscode';
import { dynamics365Folder, onDidChangeEnvironment, readActiveEnvironment, selectedEnvironmentUrl } from './environment';
import { getDataverseTokenSilently, readUdeEnvironment, resolveDataverseUrl } from './ude';

export interface VersionDrift { local: string; remote: string; dataverseUrl: string }

/**
 * Microsoft services unified environments on its own schedule. When the environment runs a newer application version than
 * the metadata on this machine, builds compile against the old one and deploys fail: this compares the two (without
 * signing in) and offers Connect to Environment for the new version.
 */
export class VersionGuard implements vscode.Disposable {
    private readonly changed = new vscode.EventEmitter<VersionDrift | undefined>();
    readonly onDidChange = this.changed.event;
    private readonly subscription: vscode.Disposable;
    private warned = new Set<string>();
    drift: VersionDrift | undefined;

    constructor() {
        this.subscription = onDidChangeEnvironment(() => void this.check());
        // After startup: the check needs the network and must not slow down activation.
        setTimeout(() => void this.check(), 15_000);
    }

    dispose(): void {
        this.subscription.dispose();
        this.changed.dispose();
    }

    async check(): Promise<VersionDrift | undefined> {
        this.drift = await this.compare().catch(() => undefined);
        this.changed.fire(this.drift);
        const drift = this.drift;
        if (drift && !this.warned.has(drift.remote)) {
            this.warned.add(drift.remote);
            const choice = await vscode.window.showWarningMessage(
                `The environment now runs application ${drift.remote}; this machine has the metadata of ${drift.local}. Builds compile against the old version and deploys can fail.`,
                'Update Metadata', 'Later');
            if (choice === 'Update Metadata') {
                await vscode.commands.executeCommand('xpp.connectEnvironment', drift.dataverseUrl);
            }
        }
        return drift;
    }

    private async compare(): Promise<VersionDrift | undefined> {
        const env = await readActiveEnvironment();
        const selected = selectedEnvironmentUrl();
        if (env.local || !env.applicationVersion || !selected) {
            return undefined;
        }
        const dataverseUrl = env.dataverseUrl ?? await resolveDataverseUrl(selected);
        const token = await getDataverseTokenSilently(dataverseUrl);
        if (!token) {
            return undefined;
        }
        const remote = (await readUdeEnvironment(dataverseUrl, token)).applicationVersion;
        return remote && remote !== env.applicationVersion ? { local: env.applicationVersion, remote, dataverseUrl } : undefined;
    }
}

interface VersionFolder { version: string; folder: string; bytes: number; configs: string[]; active: boolean }

/**
 * F&O Studio: Clean Up Old Versions: each application version leaves PackagesLocalDirectory (about 16 GB) under
 * %LOCALAPPDATA%\Microsoft\Dynamics365. Lists the folders with their size and the configurations that use them, and
 * deletes the picked ones after a confirmation.
 */
export async function cleanUpOldVersions(): Promise<void> {
    const root = dynamics365Folder();
    const env = await readActiveEnvironment().catch(() => undefined);
    const activeVersion = env?.applicationVersion;
    const folders = await vscode.window.withProgress({ location: vscode.ProgressLocation.Notification, title: 'Measuring application version folders…' }, async () => {
        const configs = await fs.readdir(path.join(root, 'XPPConfig')).catch(() => [] as string[]);
        const result: VersionFolder[] = [];
        for (const name of await fs.readdir(root).catch(() => [] as string[])) {
            if (!/^\d+(\.\d+){3}$/.test(name)) {
                continue;
            }
            result.push({
                version: name, folder: path.join(root, name), bytes: await folderSize(path.join(root, name)),
                configs: configs.filter(c => c.endsWith(`___${name}.json`)).map(c => c.split('___')[0]),
                active: name === activeVersion,
            });
        }
        return result.sort((a, b) => compare(b.version, a.version));
    });
    const candidates = folders.filter(f => !f.active);
    if (!candidates.length) {
        vscode.window.showInformationMessage('No other application versions are on this machine.');
        return;
    }
    // The newest older version is kept by default: Upgrade Readiness can compare against it.
    const newestOld = candidates[0];
    const picked = await vscode.window.showQuickPick(candidates.map(f => ({
        label: f.version, description: `${(f.bytes / 1024 ** 3).toFixed(1)} GB`, picked: f !== newestOld,
        detail: f.configs.length ? `Used by the configuration of ${f.configs.join(', ')} (it stops working)` : 'No configuration uses it',
        folder: f,
    })), { title: `Delete application versions (active: ${activeVersion ?? 'unknown'})`, canPickMany: true, ignoreFocusOut: true });
    if (!picked?.length) {
        return;
    }
    const total = picked.reduce((sum, p) => sum + p.folder.bytes, 0);
    const confirm = await vscode.window.showWarningMessage(`Delete ${picked.map(p => p.label).join(', ')} (${(total / 1024 ** 3).toFixed(1)} GB)?`,
        { modal: true, detail: 'The metadata and tools of these versions are removed from this disk. Configurations that use them stop working until you connect to that version again. Cross-reference databases are not removed.' },
        'Delete');
    if (confirm !== 'Delete') {
        return;
    }
    const failed: string[] = [];
    await vscode.window.withProgress({ location: vscode.ProgressLocation.Notification, title: 'Deleting old versions…' }, async progress => {
        for (const p of picked) {
            progress.report({ message: p.label });
            // Long-path prefix: PackagesLocalDirectory has paths beyond 260 characters.
            await fs.rm(`\\\\?\\${p.folder.folder}`, { recursive: true, force: true }).catch(() => failed.push(p.label));
        }
    });
    const freed = picked.filter(p => !failed.includes(p.label) && !existsSync(p.folder.folder)).reduce((sum, p) => sum + p.folder.bytes, 0);
    vscode.window.showInformationMessage(`Freed ${(freed / 1024 ** 3).toFixed(1)} GB.${failed.length ? ` Not fully deleted (files in use?): ${failed.join(', ')}.` : ''}`);
}

async function folderSize(folder: string): Promise<number> {
    let total = 0;
    const stack = [folder];
    while (stack.length) {
        const current = stack.pop()!;
        for (const entry of await fs.readdir(current, { withFileTypes: true }).catch(() => [])) {
            const full = path.join(current, entry.name);
            if (entry.isDirectory()) {
                stack.push(full);
            } else if (entry.isFile()) {
                total += (await fs.stat(full).catch(() => undefined))?.size ?? 0;
            }
        }
    }
    return total;
}

function compare(a: string, b: string): number {
    const pa = a.split('.').map(Number), pb = b.split('.').map(Number);
    for (let i = 0; i < Math.max(pa.length, pb.length); i++) {
        if ((pa[i] ?? 0) !== (pb[i] ?? 0)) {
            return (pa[i] ?? 0) - (pb[i] ?? 0);
        }
    }
    return 0;
}
