import * as vscode from 'vscode';
import { onDidChangeEnvironment, readActiveEnvironment, XppEnvironment } from '../environment';
import { CredentialStore, DatabaseCredentials, requestCredentials } from './credentials';

/** Where business data comes from: the local AxDB (Windows authentication) or a unified environment's Azure SQL database. */
export interface DatabaseTarget {
    environment: string;
    server: string;
    database: string;
    cloud: boolean;
    /** Unified environments only. */
    credentials?: DatabaseCredentials;
}

/** What the data service needs to connect; sent with each request, never stored by the server. */
export function connectionOf(target: DatabaseTarget): object {
    return target.credentials
        ? { server: target.server, database: target.database, user: target.credentials.user, password: target.credentials.password }
        : { server: target.server, database: target.database, windowsAuthentication: true };
}

/**
 * The business database of the active environment, for the data browser, the query panel and the agent tool. Cloud-hosted
 * and VHD environments have AxDB on this machine; unified environments need just-in-time credentials, requested here.
 */
export class DatabaseAccess implements vscode.Disposable {
    private readonly changed = new vscode.EventEmitter<void>();
    private readonly status = vscode.window.createStatusBarItem('xpp.database', vscode.StatusBarAlignment.Left, -1);
    private readonly disposables: vscode.Disposable[] = [];
    private expiryTimer: NodeJS.Timeout | undefined;
    readonly onDidChange = this.changed.event;

    constructor(private readonly store: CredentialStore) {
        this.status.name = 'F&O Studio database';
        this.status.command = 'xpp.database.menu';
        this.disposables.push(this.changed, this.status,
            onDidChangeEnvironment(() => this.changed.fire()),
            store.onDidChange(() => this.changed.fire()),
            this.changed.event(() => void this.refreshStatus()));
        void this.refreshStatus();
    }

    /** The database, or undefined when a unified environment has no valid credentials yet. */
    async current(): Promise<DatabaseTarget | undefined> {
        const env = await readActiveEnvironment();
        if (env.local) {
            return { environment: env.name, server: env.databaseServer || 'localhost', database: env.businessDatabaseName || 'AxDB', cloud: false };
        }
        const credentials = await this.store.get(env.name);
        return credentials && { environment: env.name, server: credentials.server, database: credentials.database, cloud: true, credentials };
    }

    /** The database; in a unified environment without credentials, offers to request them. */
    async require(): Promise<DatabaseTarget | undefined> {
        const target = await this.current();
        if (target) {
            return target;
        }
        // Modal: a toast can slip into the notification center while the caller (e.g. the data panel) waits on it.
        const choice = await vscode.window.showInformationMessage('The database of a unified environment needs temporary credentials.', {
            modal: true,
            detail: 'F&O Studio requests them from the environment (just-in-time SQL access) and keeps them in VS Code\'s secret storage until they expire.',
        }, 'Request Credentials');
        return choice ? this.request() : undefined;
    }

    /** Requests new credentials for the active unified environment. */
    async request(): Promise<DatabaseTarget | undefined> {
        const env = await this.unifiedEnvironment();
        const existing = await this.store.get(env.name);
        const credentials = await requestCredentials(env.name, !!existing);
        if (!credentials) {
            return undefined;
        }
        await this.store.set(env.name, credentials);
        const until = credentials.expiresAt ? ` until ${new Date(credentials.expiresAt).toLocaleString()}` : '';
        vscode.window.showInformationMessage(`Database credentials (${credentials.role}) for ${env.name}${until}. They are kept in VS Code's secret storage.`);
        return { environment: env.name, server: credentials.server, database: credentials.database, cloud: true, credentials };
    }

    async forget(): Promise<void> {
        const env = await this.unifiedEnvironment();
        await this.store.forget(env.name);
        vscode.window.setStatusBarMessage(`Forgot the database credentials for ${env.name}.`, 4000);
    }

    /** Called when the server could not sign in: expired or revoked credentials are dropped so the next use asks again. */
    async signInFailed(target: DatabaseTarget): Promise<void> {
        if (target.cloud) {
            await this.store.forget(target.environment);
        }
    }

    private async unifiedEnvironment(): Promise<XppEnvironment> {
        const env = await readActiveEnvironment();
        if (env.local) {
            throw new Error('This environment\'s database is on this machine and uses Windows authentication; it needs no credentials.');
        }
        return env;
    }

    private async refreshStatus(): Promise<void> {
        let target: DatabaseTarget | undefined;
        try {
            target = await this.current();
        } catch {
            this.status.hide();
            return;
        }
        // Cleared after the await: of overlapping refreshes, only the last one keeps a timer.
        clearTimeout(this.expiryTimer);
        if (!target) {
            // Requested from the Dynamics 365 view or on first use; no status bar item until there is access.
            this.status.hide();
            return;
        } else if (!target.credentials) {
            this.status.text = `$(database) ${target.database}`;
            this.status.tooltip = `${target.database} on ${target.server} (Windows authentication)`;
        } else {
            const expiresAt = target.credentials.expiresAt;
            this.status.text = `$(database) ${target.credentials.role}${expiresAt ? ` until ${new Date(expiresAt).toLocaleTimeString([], { hour: '2-digit', minute: '2-digit' })}` : ''}`;
            this.status.tooltip = `${target.database} on ${target.server} as ${target.credentials.user} (${target.credentials.role})\nExpires: ${target.credentials.expiration || 'unknown'}`;
            if (expiresAt) {
                // Timers longer than ~24.8 days overflow; credentials last hours.
                this.expiryTimer = setTimeout(() => this.changed.fire(), Math.min(Math.max(expiresAt - Date.now(), 0) + 1000, 2 ** 31 - 1));
            }
        }
        this.status.show();
    }

    dispose(): void {
        clearTimeout(this.expiryTimer);
        this.disposables.forEach(d => d.dispose());
    }
}
