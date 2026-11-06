import * as vscode from 'vscode';
import { elementOf, pickElement, ToolNode, ToolsContribution, TreeArg } from '../d365Tools';
import { XppServerClient } from '../serverClient';
import { DatabaseAccess } from './connection';
import { CredentialStore } from './credentials';
import { DataPanel, PanelMode } from './dataPanel';
import { registerPanelSerializer } from '../webviewState';
import { DataService } from './dataService';
import { registerQueryTool } from './queryTool';
import { openInSsms } from './ssms';
import { Insights } from './insights';

type Command = (id: string, handler: (...args: any[]) => unknown) => void;

/** Database access: credentials, the status bar item, Browse Data, the query panel and the agent's query tool. */
export function registerDatabase(context: vscode.ExtensionContext, command: Command, server: XppServerClient): ToolsContribution {
    const access = new DatabaseAccess(new CredentialStore(context.secrets));
    const data = new DataService(server, access);
    context.subscriptions.push(access);
    registerQueryTool(context, access, data);
    registerPanelSerializer<PanelMode>(context, 'xppData', (panel, mode) => DataPanel.revive(context, server, data, access, panel, mode));

    command('xpp.database.requestCredentials', () => access.request());
    command('xpp.database.forgetCredentials', () => access.forget());
    command('xpp.database.newQuery', () => DataPanel.openQuery(context, server, data, access));
    command('xpp.database.insights', async () => {
        const pick = await vscode.window.showQuickPick(Insights.map(i => ({ label: i.label, detail: i.detail, sql: i.sql })),
            { title: 'Database performance insights (read-only)' });
        if (pick) {
            DataPanel.openQuery(context, server, data, access, pick.sql, true);
        }
    });
    command('xpp.database.browseTable', async (arg?: TreeArg) => {
        const element = elementOf(arg) ?? await pickElement(server, 'Browse table data', ['AxTable']);
        if (element?.objectType === 'AxTable' || element?.objectType === 'AxTableExtension') {
            DataPanel.openTable(context, server, data, access, element.objectName.split('.')[0]);
        }
    });
    command('xpp.database.testConnection', async () => {
        const target = await access.require();
        if (!target) {
            return;
        }
        const info = await vscode.window.withProgress({ location: vscode.ProgressLocation.Notification, title: `Connecting to ${target.database}…` },
            () => data.test(target));
        vscode.window.showInformationMessage(`Connected to ${info.database} on ${target.server} as ${info.user} (SQL Server ${info.version}).`);
    });
    command('xpp.database.copyConnection', async () => {
        const target = await access.require();
        if (!target) {
            return;
        }
        const items = [
            { label: 'Server', value: target.server },
            { label: 'Database', value: target.database },
            ...target.credentials ? [
                { label: 'User name', value: target.credentials.user },
                { label: 'Password', value: target.credentials.password, description: 'Copied to the clipboard; paste it, then clear the clipboard' },
            ] : [],
        ];
        const pick = await vscode.window.showQuickPick(items.map(i => ({ label: i.label, description: i.label === 'Password' ? i.description : i.value, value: i.value })),
            { title: 'Copy connection detail' });
        if (pick) {
            await vscode.env.clipboard.writeText(pick.value);
            vscode.window.setStatusBarMessage(`Copied ${pick.label.toLowerCase()}`, 3000);
        }
    });
    command('xpp.database.openSsms', async () => {
        const target = await access.require();
        if (target) {
            await openInSsms(target);
        }
    });
    command('xpp.database.menu', async () => {
        const target = await access.current().catch(() => undefined);
        const env = target?.environment;
        const actions: (vscode.QuickPickItem & { command: string })[] = [
            { label: '$(table) Browse Table Data…', command: 'xpp.database.browseTable' },
            { label: '$(terminal) New SQL Query', command: 'xpp.database.newQuery' },
            { label: '$(dashboard) Performance Insights…', command: 'xpp.database.insights' },
            { label: '$(plug) Test Connection', command: 'xpp.database.testConnection' },
            { label: '$(link-external) Open in SQL Server Management Studio', command: 'xpp.database.openSsms' },
            { label: '$(copy) Copy Connection Detail…', command: 'xpp.database.copyConnection' },
        ];
        if (!target || target.cloud) {
            actions.push({ label: target ? '$(key) Request New Credentials…' : '$(key) Request Database Credentials…', command: 'xpp.database.requestCredentials' });
        }
        if (target?.cloud) {
            actions.push({ label: '$(trash) Forget Credentials', command: 'xpp.database.forgetCredentials' });
        }
        const pick = await vscode.window.showQuickPick(actions, { title: env ? `Database of ${env}` : 'Database' });
        if (pick) {
            await vscode.commands.executeCommand(pick.command);
        }
    });
    return new DataTools(access);
}

/** The Data group of the Dynamics 365 view, with the database access state as its description. */
class DataTools implements ToolsContribution {
    private state: string | undefined;
    private readonly changed = new vscode.EventEmitter<void>();
    readonly onDidChange = this.changed.event;
    readonly slot = 'data';

    constructor(private readonly access: DatabaseAccess) {
        access.onDidChange(() => void this.refresh());
        void this.refresh();
    }

    private async refresh(): Promise<void> {
        const target = await this.access.current().catch(() => undefined);
        const expires = target?.credentials?.expiresAt;
        this.state = !target ? 'no access' : !target.credentials ? target.database
            : `${target.credentials.role}${expires ? ` · until ${new Date(expires).toLocaleTimeString([], { hour: '2-digit', minute: '2-digit' })}` : ''}`;
        this.changed.fire();
    }

    nodes(): ToolNode[] {
        return [{
            label: 'Data', icon: 'database', description: this.state, children: [
                { label: 'Browse Table Data…', icon: 'table', command: 'xpp.database.browseTable' },
                { label: 'New SQL Query', icon: 'terminal', command: 'xpp.database.newQuery' },
                { label: 'Performance Insights…', icon: 'dashboard', command: 'xpp.database.insights' },
                { label: 'Database…', icon: 'key', command: 'xpp.database.menu', description: 'credentials, connection, SSMS' },
            ],
        }];
    }
}
