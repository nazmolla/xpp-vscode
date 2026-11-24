import { Dataverse } from '../dataverse';
import { httpFetch } from '../http';

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

export interface SqlCredentialRequest {
    /** Why access is needed; recorded with the request. */
    reason: string;
    /** IPv4 address or range ("first-last") the database firewall lets in. */
    addresses: string;
    /** Reader unless Writer is asked for explicitly. */
    role?: SqlRole;
}

const RequestAction = 'msprov_getfinopssqljitaccessasync';
const PublicIpService = 'https://checkip.amazonaws.com/';

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
export async function publicAddress(): Promise<string | undefined> {
    try {
        const response = await httpFetch(PublicIpService, { signal: AbortSignal.timeout(5000) });
        const text = (await response.text()).trim();
        return response.ok && Ipv4.test(text) ? text : undefined;
    } catch {
        return undefined;
    }
}

/** Requests just-in-time SQL credentials for the environment's database (msprov_getfinopssqljitaccessasync); Reader by default. */
export async function requestSqlCredentials(dataverse: Dataverse, request: SqlCredentialRequest, signal?: AbortSignal): Promise<DatabaseCredentials> {
    const role = request.role ?? 'Reader';
    const result = await dataverse.action(RequestAction, {
        sqljitclientipaddress: request.addresses.trim(),
        sqljitreason: request.reason.trim(),
        sqljitrole: role,
    }, signal);
    return toCredentials(result, role, request.addresses.trim());
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
