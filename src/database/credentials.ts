import * as vscode from 'vscode';
import { DatabaseCredentials, isExpired, publicAddress, requestSqlCredentials, SqlRole, validateAddressRange } from '../core';
import { withAbortSignal } from '../coreHost';
import { selectedDataverse } from '../dataverse';

export { isExpired, toCredentials, validateAddressRange } from '../core';
export type { DatabaseCredentials, SqlRole } from '../core';

const SecretPrefix = 'xpp.database.';
/** Credentials per environment in VS Code's secret storage (Windows Credential Manager), never in settings or logs. */
export class CredentialStore {
    constructor(private readonly secrets: vscode.SecretStorage) {}

    async get(environment: string): Promise<DatabaseCredentials | undefined> {
        const text = await this.secrets.get(SecretPrefix + environment);
        if (!text) {
            return undefined;
        }
        try {
            const credentials = JSON.parse(text) as DatabaseCredentials;
            if (isExpired(credentials)) {
                await this.forget(environment);
                return undefined;
            }
            return credentials;
        } catch {
            await this.forget(environment);
            return undefined;
        }
    }

    set(environment: string, credentials: DatabaseCredentials): Thenable<void> {
        return this.secrets.store(SecretPrefix + environment, JSON.stringify(credentials));
    }

    forget(environment: string): Thenable<void> {
        return this.secrets.delete(SecretPrefix + environment);
    }

    onDidChange(listener: () => void): vscode.Disposable {
        return this.secrets.onDidChange(e => e.key.startsWith(SecretPrefix) ? listener() : undefined);
    }
}

/** Asks for the reason, the allowed addresses and the role, then requests credentials; undefined when cancelled. */
export async function requestCredentials(environment: string, hasValidCredentials: boolean): Promise<DatabaseCredentials | undefined> {
    const reason = await vscode.window.showInputBox({
        title: 'Database credentials (1/3): reason',
        prompt: 'Why do you need access to the database? The reason is recorded with the request.',
        placeHolder: 'Investigate why sales order SO-001234 does not post',
        ignoreFocusOut: true,
        validateInput: v => v.trim().length < 5 ? 'Describe the reason in a few words.' : undefined,
    });
    if (!reason) {
        return undefined;
    }
    const addresses = await vscode.window.showInputBox({
        title: 'Database credentials (2/3): allowed IPv4 addresses',
        prompt: 'Connections from other addresses are refused by the database firewall. Prefilled with this machine\'s public address.',
        value: await publicAddress() ?? '',
        ignoreFocusOut: true,
        validateInput: validateAddressRange,
    });
    if (!addresses) {
        return undefined;
    }
    const role = await vscode.window.showQuickPick([
        { label: 'Reader' as SqlRole, description: 'Read data only (recommended)' },
        { label: 'Writer' as SqlRole, description: 'Read and change data. Changes made in SQL skip X++ business logic.' },
    ], {
        title: 'Database credentials (3/3): access',
        placeHolder: hasValidCredentials ? 'New credentials replace the ones you have for this environment (also those Visual Studio requested).' : 'Access level',
        ignoreFocusOut: true,
    });
    if (!role) {
        return undefined;
    }

    return vscode.window.withProgress({ location: vscode.ProgressLocation.Notification, title: `Requesting database credentials for ${environment}…`, cancellable: true },
        async (_progress, token) => {
            const dataverse = await selectedDataverse();
            return withAbortSignal(token, signal => requestSqlCredentials(dataverse, { reason, addresses, role: role.label }, signal));
        });
}
