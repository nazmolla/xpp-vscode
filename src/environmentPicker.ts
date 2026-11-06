import * as vscode from 'vscode';
import { ToolNode, ToolsContribution } from './d365Tools';
import {
    EnvironmentConfig, hasWorkspaceEnvironment, listEnvironments, onDidChangeEnvironment, orgName, readActiveEnvironment,
    selectedEnvironmentUrl, useDefaultEnvironment, useEnvironment, XppEnvironment,
} from './environment';
import { resolveDataverseUrl, validateDataverseUrl, normalizeDataverseUrl } from './ude';
import { onDidChangeSetup, PhaseState, setupStatus } from './setup';
import { VersionGuard } from './versionGuard';

type EnvironmentItem = vscode.QuickPickItem & { env?: EnvironmentConfig; action?: 'connect' | 'default' };

/** F&O Studio: Select Environment — switches this workspace to another environment connected on this machine. */
export async function selectEnvironment(): Promise<void> {
    const current = selectedEnvironmentUrl();
    const currentOrg = current ? orgName(current) : undefined;
    const environments = await listEnvironments();
    const items: EnvironmentItem[] = environments.map(env => ({
        label: `$(server-environment) ${env.org}`,
        description: env.org === currentOrg ? `${env.applicationVersion} (current)` : env.applicationVersion,
        detail: env.finOpsUrl,
        env,
    }));
    items.push({ label: '', kind: vscode.QuickPickItemKind.Separator },
        { label: '$(plug) Connect to Another Environment…', action: 'connect' });
    if (hasWorkspaceEnvironment()) {
        const fallback = vscode.workspace.getConfiguration('xpp').get<string>('environmentUrl');
        items.push({ label: '$(discard) Use the Default Environment', description: fallback ? orgName(fallback) : 'machine-wide configuration', action: 'default' });
    }
    const picked = await vscode.window.showQuickPick(items, {
        title: vscode.workspace.workspaceFolders?.length ? 'Environment for this workspace' : 'Default environment',
        placeHolder: environments.length ? `Current: ${currentOrg ?? 'machine-wide configuration'}` : 'No environment is connected on this machine yet.',
        matchOnDescription: true,
        matchOnDetail: true,
    });
    if (picked?.action === 'connect') {
        await vscode.commands.executeCommand('xpp.connectEnvironment');
        return;
    }
    if (picked?.action === 'default') {
        await useDefaultEnvironment();
        return;
    }
    if (!picked?.env || picked.env.org === currentOrg) {
        return;
    }
    const dataverseUrl = await dataverseUrlOf(picked.env);
    if (!dataverseUrl) {
        return;
    }
    await useEnvironment(dataverseUrl);
    vscode.window.setStatusBarMessage(`$(server-environment) X++ environment: ${picked.env.org} (${picked.env.applicationVersion})`, 5000);
}

/** A configuration only names the org; its Dataverse host is found by region, or asked for. */
async function dataverseUrlOf(env: EnvironmentConfig): Promise<string | undefined> {
    try {
        return await vscode.window.withProgress({ location: vscode.ProgressLocation.Window, title: `Finding ${env.org}…` },
            () => resolveDataverseUrl(`https://${env.org}.operations.dynamics.com`));
    } catch {
        const input = await vscode.window.showInputBox({
            title: `Dataverse URL of ${env.org}`,
            prompt: 'The Dataverse URL from the Power Platform admin center',
            value: `https://${env.org}.crm.dynamics.com`,
            ignoreFocusOut: true,
            validateInput: v => validateDataverseUrl(v) ?? (orgName(v) === env.org ? undefined : `The URL must start with https://${env.org}.`),
        });
        return input ? normalizeDataverseUrl(input) : undefined;
    }
}

/** The environment in the status bar and in the Dynamics 365 view; both switch environments. */
export class EnvironmentStatus implements ToolsContribution, vscode.Disposable {
    private readonly item = vscode.window.createStatusBarItem('xpp.environment', vscode.StatusBarAlignment.Left, 0);
    private readonly changed = new vscode.EventEmitter<void>();
    private readonly subscription: vscode.Disposable;
    private current: XppEnvironment | undefined;
    readonly onDidChange = this.changed.event;
    readonly slot = 'environment';

    constructor(private readonly guard: VersionGuard) {
        this.item.name = 'X++ Environment';
        this.item.command = 'xpp.environment.menu';
        this.subscription = vscode.Disposable.from(onDidChangeEnvironment(() => void this.update()), onDidChangeSetup(() => this.changed.fire()),
            guard.onDidChange(() => void this.update()));
        void this.update();
    }

    nodes(): ToolNode[] {
        const setup = setupStatus();
        if (setup) {
            const icons: Record<PhaseState, string> = { pending: 'circle-outline', running: 'sync~spin', done: 'pass', failed: 'error' };
            return [{
                label: 'Environment', icon: 'server-environment', expanded: true,
                description: setup.running ? `Connecting ${orgName(setup.dataverseUrl)}…` : setup.error ? 'setup failed' : `${orgName(setup.dataverseUrl)} connected`,
                children: [
                    ...setup.phases.map(p => ({ label: p.label, icon: icons[p.state], description: p.detail, tooltip: `${p.label}: ${p.state}${p.detail ? `\n${p.detail}` : ''}` })),
                    ...setup.running ? [] : [
                        ...setup.error ? [{ label: 'Retry', icon: 'debug-restart', command: 'xpp.connectEnvironment', commandArgs: [setup.dataverseUrl] }] : [],
                        { label: 'Done', icon: 'check', command: 'xpp.environment.clearSetup' },
                    ],
                ],
            }];
        }
        const drift = this.guard.drift;
        return [{
            label: 'Environment', icon: drift ? 'warning' : 'server-environment', description: drift ? `${this.current?.name} · ${drift.remote} available` : this.current?.name,
            tooltip: this.tooltip(), expanded: !this.current || !!drift,
            children: [
                ...drift ? [{ label: `Update Metadata to ${drift.remote}…`, icon: 'cloud-download', command: 'xpp.connectEnvironment', commandArgs: [drift.dataverseUrl], description: `this machine: ${drift.local}` }] : [],
                { label: 'Select Environment…', icon: 'arrow-swap', command: 'xpp.selectEnvironment' },
                { label: 'Connect to Environment…', icon: 'plug', command: 'xpp.connectEnvironment' },
                { label: 'Check Environment Connection', icon: 'pulse', command: 'xpp.checkEnvironment' },
                { label: 'Show Active Configuration', icon: 'info', command: 'xpp.showEnvironment' },
                { label: 'Clean Up Old Versions…', icon: 'trash', command: 'xpp.cleanUpOldVersions', udeOnly: true },
                { label: 'Sign In (pac)', icon: 'account', command: 'xpp.pac.signIn', udeOnly: true },
            ],
        }];
    }

    hides(node: ToolNode): boolean {
        return !!node.udeOnly && !!this.current?.local;
    }

    /** Everything about the environment in one menu (the status bar item). */
    async menu(): Promise<void> {
        const drift = this.guard.drift;
        const items = [
            ...drift ? [{ label: `$(cloud-download) Update Metadata to ${drift.remote}…`, command: 'xpp.connectEnvironment', args: [drift.dataverseUrl] }] : [],
            { label: '$(arrow-swap) Select Environment…', command: 'xpp.selectEnvironment' },
            { label: '$(plug) Connect to Environment…', command: 'xpp.connectEnvironment' },
            { label: '$(pulse) Check Environment Connection', command: 'xpp.checkEnvironment' },
            ...this.current?.local ? [] : [
                { label: '$(versions) Check Application Version', command: 'xpp.checkVersion' },
                { label: '$(trash) Clean Up Old Versions…', command: 'xpp.cleanUpOldVersions' },
                { label: '$(account) Sign In (pac)', command: 'xpp.pac.signIn' },
            ],
            { label: '$(database) Database…', command: 'xpp.database.menu' },
            { label: '$(info) Show Active Configuration', command: 'xpp.showEnvironment' },
        ] as { label: string; command: string; args?: unknown[] }[];
        const picked = await vscode.window.showQuickPick(items, { title: this.current ? `Environment ${this.current.name}${this.current.applicationVersion ? ` (${this.current.applicationVersion})` : ''}` : 'Environment' });
        if (picked) {
            await vscode.commands.executeCommand(picked.command, ...(picked.args ?? []));
        }
    }

    dispose(): void {
        this.subscription.dispose();
        this.item.dispose();
        this.changed.dispose();
    }

    private async update(): Promise<void> {
        this.current = await readActiveEnvironment().catch(() => undefined);
        void vscode.commands.executeCommand('setContext', 'xpp.localEnvironment', !!this.current?.local);
        if (this.current) {
            this.item.text = `$(${this.mismatch() || this.guard.drift ? 'warning' : 'server-environment'}) ${this.current.name}`;
            this.item.tooltip = this.tooltip();
            this.item.show();
        } else {
            this.item.hide();
        }
        this.changed.fire();
    }

    /** The selected environment has no metadata on this machine, so the X++ tools use another environment's. */
    private mismatch(): string | undefined {
        const selected = selectedEnvironmentUrl();
        return selected && this.current && !this.current.dataverseUrl ? selected : undefined;
    }

    private tooltip(): string | undefined {
        const env = this.current;
        if (!env) {
            return undefined;
        }
        const mismatch = this.mismatch();
        const drift = this.guard.drift;
        return [
            `X++ environment: ${env.name}`,
            env.applicationVersion && `Application ${env.applicationVersion}`,
            drift && `The environment now runs ${drift.remote}: update the metadata (Dynamics 365 view > Environment).`,
            env.finOpsUrl,
            mismatch
                ? `${mismatch} has no metadata on this machine; this is the machine-wide configuration. Connect to Environment downloads it.`
                : env.local ? 'Cloud-hosted or VHD environment (local AOS and PackagesLocalDirectory)'
                : !env.dataverseUrl && 'Machine-wide configuration (shared with Visual Studio)',
            'Click for the environment menu.',
        ].filter(Boolean).join('\n');
    }
}
