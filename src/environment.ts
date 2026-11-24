import * as vscode from 'vscode';
import { loadEnvironment, XppEnvironment } from './core';

export { dynamics365Folder, findEnvironment, listEnvironments, orgName } from './core';
export type { EnvironmentConfig, XppEnvironment } from './core';

const WorkspaceEnvironmentKey = 'xpp.environmentUrl';

let workspaceState: vscode.Memento | undefined;
let storageFolder: string | undefined;
const changed = new vscode.EventEmitter<void>();

/** Fires when this window switches to another environment. */
export const onDidChangeEnvironment = changed.event;

/** The active configuration was rewritten (Connect to Environment): reload everything that shows metadata. */
export function notifyEnvironmentChanged(): void {
    changed.fire();
}

/** Remembers the environment per workspace; the user setting xpp.environmentUrl is the default of every window. */
export function initEnvironmentSelection(context: vscode.ExtensionContext): void {
    workspaceState = context.workspaceState;
    storageFolder = context.globalStorageUri.fsPath;
    context.subscriptions.push(changed, changed.event(() => active = undefined), vscode.workspace.onDidChangeConfiguration(e => {
        if (e.affectsConfiguration('xpp.environmentUrl') && !workspaceState?.get<string>(WorkspaceEnvironmentKey)) {
            changed.fire();
        }
    }));
}

/** Dataverse URL of the environment this window works with. */
export function selectedEnvironmentUrl(): string | undefined {
    return workspaceState?.get<string>(WorkspaceEnvironmentKey)
        || vscode.workspace.getConfiguration('xpp').get<string>('environmentUrl')?.trim()
        || undefined;
}

/** True when this workspace chose its own environment instead of the default (xpp.environmentUrl). */
export function hasWorkspaceEnvironment(): boolean {
    return !!workspaceState?.get<string>(WorkspaceEnvironmentKey);
}

/**
 * Makes an environment the one this window works with. With a folder open the choice belongs to the workspace and is
 * kept out of its settings files, since every developer has an environment of their own. The first environment also
 * becomes the default.
 */
export async function useEnvironment(dataverseUrl: string): Promise<void> {
    const config = vscode.workspace.getConfiguration('xpp');
    const before = selectedEnvironmentUrl();
    const perWorkspace = !!workspaceState && !!vscode.workspace.workspaceFolders?.length;
    if (perWorkspace) {
        await workspaceState!.update(WorkspaceEnvironmentKey, dataverseUrl);
    }
    if (!perWorkspace || !config.inspect<string>('environmentUrl')?.globalValue) {
        await config.update('environmentUrl', dataverseUrl, vscode.ConfigurationTarget.Global);
    }
    // A setting change fires through onDidChangeConfiguration; a workspace choice does not.
    if (perWorkspace && before !== dataverseUrl) {
        changed.fire();
    }
}

/** Returns this workspace to the default environment (xpp.environmentUrl). */
export async function useDefaultEnvironment(): Promise<void> {
    const before = selectedEnvironmentUrl();
    await workspaceState?.update(WorkspaceEnvironmentKey, undefined);
    if (before !== selectedEnvironmentUrl()) {
        changed.fire();
    }
}

// Many callers ask at once (status bar, trees, MCP); a short cache spares rereading every configuration.
const ActiveEnvironmentTtl = 5000;
let active: { url: string | undefined; time: number; environment: Promise<XppEnvironment> } | undefined;

/**
 * The metadata configuration of the selected environment. Without a selection, or when the selected environment has
 * no configuration on this machine, the machine-wide one shared with Visual Studio, or else the local AOS of a
 * cloud-hosted or VHD environment.
 */
export function readActiveEnvironment(): Promise<XppEnvironment> {
    const url = selectedEnvironmentUrl();
    if (!active || active.url !== url || Date.now() - active.time > ActiveEnvironmentTtl) {
        const entry = { url, time: Date.now(), environment: loadEnvironment(url, { localConfigFolder: storageFolder }) };
        entry.environment.catch(() => active === entry ? active = undefined : undefined);
        active = entry;
    }
    return active.environment;
}
