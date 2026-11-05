import { randomUUID } from 'crypto';
import * as vscode from 'vscode';
import { XppServerClient } from '../serverClient';
import { connectionOf, DatabaseAccess, DatabaseTarget } from './connection';

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

// Login failed, firewall refused the address, password expired: the credentials (or the allowed addresses) no longer work.
const SignInErrors = new Set([18456, 40615, 18487, 18488]);
// Azure SQL applies a new firewall rule and password within minutes (seen live: both fail right after the request).
const ActivationMs = 10 * 60_000;
const ServerSlackMs = 30_000;

/** Business data through the extension's server process (xpp/data); the connection travels with each request. */
export class DataService {
    constructor(private readonly server: XppServerClient, private readonly access: DatabaseAccess) {}

    browse(target: DatabaseTarget, request: BrowseRequest, token?: vscode.CancellationToken): Promise<QueryResult> {
        return this.run(target, 'browse', { ...request }, 60, token);
    }

    query(target: DatabaseTarget, sql: string, options: QueryOptions = {}, token?: vscode.CancellationToken): Promise<QueryResult> {
        return this.run(target, 'query', { sql, ...options }, options.timeoutSeconds ?? 60, token);
    }

    async test(target: DatabaseTarget): Promise<{ user: string; database: string; version: string }> {
        return this.call(target, 'test', {}, 30) as Promise<any>;
    }

    /** The estimated plan of a SELECT (compiled, not run), summarized by the server. */
    plan(target: DatabaseTarget, sql: string): Promise<QueryPlanSummary> {
        return this.call(target, 'plan', { sql }, 60) as Promise<QueryPlanSummary>;
    }

    private async run(target: DatabaseTarget, action: string, body: Record<string, unknown>, timeoutSeconds: number, token?: vscode.CancellationToken): Promise<QueryResult> {
        const queryId = randomUUID();
        const cancel = token?.onCancellationRequested(() => void this.server.data('cancel', { queryId }).catch(() => undefined));
        try {
            return await this.call(target, action, { ...body, queryId, timeoutSeconds }, timeoutSeconds) as QueryResult;
        } finally {
            cancel?.dispose();
        }
    }

    private async call(target: DatabaseTarget, action: string, body: Record<string, unknown>, timeoutSeconds: number): Promise<unknown> {
        const result = await this.server.data(action, { ...body, connection: connectionOf(target) }, timeoutSeconds * 1000 + ServerSlackMs);
        if (result?.success) {
            return result;
        }
        if (SignInErrors.has(result?.sqlError)) {
            const issued = target.credentials?.requestedAt;
            if (issued && Date.now() - issued < ActivationMs) {
                throw new Error('The new database credentials are not active yet: Azure SQL takes a few minutes to apply the firewall rule and the password. Try again in a minute.');
            }
            await this.access.signInFailed(target);
            throw new Error(`${result.message} ${target.cloud
                ? 'The credentials expired, were replaced, or this address is not allowed. Request new database credentials.'
                : 'Check that your Windows account can sign in to the database.'}`);
        }
        throw new Error(result?.message ?? 'The database request failed.');
    }
}
