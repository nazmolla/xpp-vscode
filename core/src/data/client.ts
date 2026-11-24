import { randomUUID } from 'crypto';
import { onAbort } from '../host';
import { DatabaseCredentials } from './credentials';

/** Where business data comes from: the local AxDB (Windows authentication) or a unified environment's Azure SQL database. */
export interface DatabaseTarget {
    environment: string;
    server: string;
    database: string;
    cloud: boolean;
    /** Unified environments only. */
    credentials?: DatabaseCredentials;
}

export interface ResultColumn { name: string; type: string }
export interface ResultSet { columns: ResultColumn[]; rows: unknown[][]; truncated: boolean }
export interface QueryResult { resultSets: ResultSet[]; rowsAffected: number; messages: string[]; elapsedMs: number; committed: boolean }
export interface QueryPlanSummary { estimatedCost: number; operators: number; notes: string[]; missingIndexes: string[]; warnings: string[]; xml?: string }

export interface BrowseFilter { column: string; operator: '=' | '<>' | '<' | '<=' | '>' | '>=' | 'contains' | 'startsWith'; value: string }

export interface BrowseRequest {
    table: string;
    columns?: string[];
    filters?: BrowseFilter[];
    company?: string;
    /** yyyy-mm-dd: rows of a date-effective table valid on that date. */
    asOf?: string;
    orderBy?: string;
    descending?: boolean;
    maxRows?: number;
}

export interface QueryOptions {
    maxRows?: number;
    timeoutSeconds?: number;
    /** Commits the batch; without it every batch is SELECT-only and rolled back. */
    allowWrite?: boolean;
}

/**
 * F&O Studio's data service (XppVsCode.Server, JSON-RPC method xpp/data), which exists only in a server process started
 * with --allow-data (never the MCP server). It runs the SQL; the connection travels with each request.
 */
export interface DataTransport {
    data(action: string, body: object, timeoutMs?: number): Promise<any>;
}

/** What the data service needs to connect; sent with each request, never stored by the server. */
export function connectionOf(target: DatabaseTarget): object {
    return target.credentials
        ? { server: target.server, database: target.database, user: target.credentials.user, password: target.credentials.password }
        : { server: target.server, database: target.database, windowsAuthentication: true };
}

// Login failed, firewall refused the address, password expired: the credentials (or the allowed addresses) no longer work.
const SignInErrors = new Set([18456, 40615, 18487, 18488]);
// Azure SQL applies a new firewall rule and password within minutes (seen live: both fail right after the request).
const ActivationMs = 10 * 60_000;
const ServerSlackMs = 30_000;

/** Business data through the data service. Queries are SELECT-only and rolled back unless allowWrite is set. */
export class DataClient {
    /** onSignInFailed: the credentials of a cloud target stopped working (drop them so the next use asks again). */
    constructor(private readonly transport: DataTransport, private readonly onSignInFailed?: (target: DatabaseTarget) => Promise<void>) {}

    browse(target: DatabaseTarget, request: BrowseRequest, signal?: AbortSignal): Promise<QueryResult> {
        return this.run(target, 'browse', { ...request }, 60, signal);
    }

    query(target: DatabaseTarget, sql: string, options: QueryOptions = {}, signal?: AbortSignal): Promise<QueryResult> {
        return this.run(target, 'query', { sql, ...options }, options.timeoutSeconds ?? 60, signal);
    }

    async test(target: DatabaseTarget): Promise<{ user: string; database: string; version: string }> {
        return this.call(target, 'test', {}, 30) as Promise<any>;
    }

    /** The estimated plan of a SELECT (compiled, not run), summarized by the server. */
    plan(target: DatabaseTarget, sql: string): Promise<QueryPlanSummary> {
        return this.call(target, 'plan', { sql }, 60) as Promise<QueryPlanSummary>;
    }

    private async run(target: DatabaseTarget, action: string, body: Record<string, unknown>, timeoutSeconds: number, signal?: AbortSignal): Promise<QueryResult> {
        const queryId = randomUUID();
        const stop = onAbort(signal, () => void this.transport.data('cancel', { queryId }).catch(() => undefined));
        try {
            return await this.call(target, action, { ...body, queryId, timeoutSeconds }, timeoutSeconds) as QueryResult;
        } finally {
            stop();
        }
    }

    private async call(target: DatabaseTarget, action: string, body: Record<string, unknown>, timeoutSeconds: number): Promise<unknown> {
        const result = await this.transport.data(action, { ...body, connection: connectionOf(target) }, timeoutSeconds * 1000 + ServerSlackMs);
        if (result?.success) {
            return result;
        }
        if (SignInErrors.has(result?.sqlError)) {
            const issued = target.credentials?.requestedAt;
            if (issued && Date.now() - issued < ActivationMs) {
                throw new Error('The new database credentials are not active yet: Azure SQL takes a few minutes to apply the firewall rule and the password. Try again in a minute.');
            }
            await this.onSignInFailed?.(target);
            throw new Error(`${result.message} ${target.cloud
                ? 'The credentials expired, were replaced, or this address is not allowed. Request new database credentials.'
                : 'Check that your Windows account can sign in to the database.'}`);
        }
        throw new Error(result?.message ?? 'The database request failed.');
    }
}

export interface ReadOnlyQueryInput {
    sql: string;
    maxRows?: number;
}

export interface ReadOnlyQueryOptions {
    /** The text returned when there is no database access (what to do differs per host). */
    noAccessMessage: string;
    signal?: AbortSignal;
}

const DefaultRows = 50;
const MaxRows = 500;
const MaxOutputChars = 20_000;

/**
 * Read-only SQL for an agent: SELECT only and rolled back (never allowWrite), at most 500 rows, output cut at 20,000
 * characters, as tab-separated text. Errors come back as text too ("QUERY FAILED: ...").
 */
export async function readOnlyQuery(client: DataClient, target: () => Promise<DatabaseTarget | undefined>, input: ReadOnlyQueryInput, options: ReadOnlyQueryOptions): Promise<string> {
    if (!input?.sql?.trim()) {
        return 'QUERY FAILED: pass a SELECT statement in "sql".';
    }
    const database = await target().catch(() => undefined);
    if (!database) {
        return options.noAccessMessage;
    }
    const maxRows = Math.max(1, Math.min(MaxRows, Math.floor(input.maxRows ?? DefaultRows)));
    try {
        const result = await client.query(database, input.sql, { maxRows }, options.signal);
        const sets = result.resultSets.map((set, i) => formatResultSet(set, result.resultSets.length > 1 ? i + 1 : undefined));
        let text = `QUERY OK on ${database.database} (${result.elapsedMs} ms, read-only).\n${sets.join('\n\n') || 'No result set.'}`;
        if (text.length > MaxOutputChars) {
            text = text.slice(0, MaxOutputChars) + '\n… output cut; select fewer columns or rows.';
        }
        return text;
    } catch (err) {
        return `QUERY FAILED: ${(err as Error).message}`;
    }
}

/** Tab-separated rows: compact for the model's context, exact for RecIds (they arrive as text). */
export function formatResultSet(set: ResultSet, number?: number): string {
    const clean = (v: unknown) => v === null || v === undefined ? 'NULL' : String(v).replace(/[\t\r\n]+/g, ' ');
    const lines = [
        `${number ? `Result ${number}: ` : ''}${set.rows.length}${set.truncated ? '+' : ''} row(s)${set.truncated ? ' (limit reached)' : ''}`,
        set.columns.map(c => c.name).join('\t'),
        ...set.rows.map(r => r.map(clean).join('\t')),
    ];
    return lines.join('\n');
}
