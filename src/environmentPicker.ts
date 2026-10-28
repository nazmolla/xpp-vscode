import * as vscode from 'vscode';
import { ToolNode, ToolsContribution } from './d365Tools';
import {
    EnvironmentConfig, hasWorkspaceEnvironment, listEnvironments, onDidChangeEnvironment, orgName, readActiveEnvironment,
    selectedEnvironmentUrl, useDefaultEnvironment, useEnvironment, XppEnvironment,
} from './environment';
import { resolveDataverseUrl, validateDataverseUrl, normalizeDataverseUrl } from './ude';

type EnvironmentItem = vscode.QuickPickItem & { env?: EnvironmentConfig; action?: 'connect' | 'default' };

/** X++: Select Environment — switches this workspace to another environment connected on this machine. */
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
    void vscode.window.showInformationMessage(`X++ environment: ${picked.env.org} (${picked.env.applicationVersion}). Reload the window to refresh open designers and code views.`, 'Reload')
        .then(choice => choice === 'Reload' ? vscode.commands.executeCommand('workbench.action.reloadWindow') : undefined);
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

    constructor() {
        this.item.name = 'X++ Environment';
        this.item.command = 'xpp.selectEnvironment';
        this.subscription = onDidChangeEnvironment(() => void this.update());
        void this.update();
    }

    nodes(): ToolNode[] {
        return [{
            label: 'Environment', icon: 'server-environment', description: this.current?.name, tooltip: this.tooltip(),
            children: [
                { label: 'Select Environment…', icon: 'arrow-swap', command: 'xpp.selectEnvironment' },
                { label: 'Connect to Environment…', icon: 'plug', command: 'xpp.connectEnvironment' },
                { label: 'Check Environment Connection', icon: 'pulse', command: 'xpp.checkEnvironment' },
                { label: 'Show Active Configuration', icon: 'info', command: 'xpp.showEnvironment' },
                { label: 'Sign In (pac)', icon: 'account', command: 'xpp.pac.signIn' },
            ],
        }];
    }

    dispose(): void {
        this.subscription.dispose();
        this.item.dispose();
        this.changed.dispose();
    }

    private async update(): Promise<void> {
        this.current = await readActiveEnvironment().catch(() => undefined);
        if (this.current) {
            this.item.text = `$(${this.mismatch() ? 'warning' : 'server-environment'}) ${this.current.name}`;
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
        return [
            `X++ environment: ${env.name}`,
            env.applicationVersion && `Application ${env.applicationVersion}`,
            env.finOpsUrl,
            mismatch
                ? `${mismatch} has no metadata on this machine; this is the machine-wide configuration. Connect to Environment downloads it.`
                : !env.dataverseUrl && 'Machine-wide configuration (shared with Visual Studio)',
            'Click to switch.',
        ].filter(Boolean).join('\n');
    }
}
