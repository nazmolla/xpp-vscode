import * as vscode from 'vscode';
import { Dataverse } from '../dataverse';
import { httpFetch } from '../ude';

/** Just-in-time SQL credentials of a unified environment's database, as Visual Studio's Power Platform Tools request them. */
export interface DatabaseCredentials {
    server: string;
    database: string;
    user: string;
    password: string;
    role: SqlRole;
    /** Expiry as the service wrote it. */
    expiration: string;
    /** Expiry in ms since 1970, when the service's text is a date. */
    expiresAt?: number;
    allowedAddresses: string;
    /** When they were issued (ms since 1970). */
    requestedAt?: number;
}

export type SqlRole = 'Reader' | 'Writer';

const RequestAction = 'msprov_getfinopssqljitaccessasync';
const PublicIpService = 'https://checkip.amazonaws.com/';
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

export function isExpired(credentials: DatabaseCredentials, now = Date.now()): boolean {
    return credentials.expiresAt !== undefined && credentials.expiresAt <= now;
}

const Octet = '(25[0-5]|2[0-4]\\d|1\\d\\d|[1-9]?\\d)';
const Ipv4 = new RegExp(`^${Octet}(\\.${Octet}){3}$`);

/** An IPv4 address or a range "first-last" (first not above last); undefined when valid. */
export function validateAddressRange(value: string): string | undefined {
    const parts = value.trim().split('-').map(p => p.trim());
    if (parts.length > 2 || parts.some(p => !Ipv4.test(p))) {
        return 'Enter an IPv4 address (203.0.113.10) or a range (203.0.113.1-203.0.113.50).';
    }
    if (parts.length === 2 && toNumber(parts[0]) > toNumber(parts[1])) {
        return 'The first address of the range must not be above the last.';
    }
    return undefined;
}

function toNumber(address: string): number {
    return address.split('.').reduce((n, octet) => n * 256 + Number(octet), 0);
}

/** This machine's public address as Azure SQL's firewall sees it, from the service Visual Studio uses; undefined offline. */
async function publicAddress(): Promise<string | undefined> {
    try {
        const response = await httpFetch(PublicIpService, { signal: AbortSignal.timeout(5000) });
        const text = (await response.text()).trim();
        return response.ok && Ipv4.test(text) ? text : undefined;
    } catch {
        return undefined;
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
            const dataverse = await Dataverse.forSelectedEnvironment();
            const result = await dataverse.action(RequestAction, {
                sqljitclientipaddress: addresses.trim(),
                sqljitreason: reason.trim(),
                sqljitrole: role.label,
            }, token);
            return toCredentials(result, role.label, addresses.trim());
        });
}

/** Maps the action's output parameters (names compared without case, as Dataverse returns them). */
export function toCredentials(result: Record<string, unknown>, role: SqlRole, addresses: string): DatabaseCredentials {
    const value = (name: string) => {
        const key = Object.keys(result ?? {}).find(k => k.toLowerCase() === name);
        const v = key ? result[key] : undefined;
        return typeof v === 'string' ? v.trim() : v === undefined || v === null ? '' : String(v);
    };
    const credentials: DatabaseCredentials = {
        server: value('servername'),
        database: value('databasename'),
        user: value('sqljitusername'),
        password: value('sqljitpassword'),
        role: (value('sqljitrole') as SqlRole) || role,
        expiration: value('sqljitexpiration'),
        allowedAddresses: value('ipaddress') || addresses,
        requestedAt: Date.now(),
    };
    if (!credentials.server || !credentials.database || !credentials.user || !credentials.password) {
        const operation = value('operationhistoryid');
        throw new Error('The environment did not return database credentials. Database access needs the System administrator role in a unified developer or sandbox environment.'
            + (operation ? ` Operation: ${operation}.` : ''));
    }
    const expiresAt = Date.parse(credentials.expiration);
    if (!Number.isNaN(expiresAt)) {
        credentials.expiresAt = expiresAt;
    }
    return credentials;
}
