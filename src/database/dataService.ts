import * as vscode from 'vscode';
import * as core from '../core';
import { withAbortSignal } from '../coreHost';
import { XppServerClient } from '../serverClient';
import { DatabaseAccess, DatabaseTarget } from './connection';

export type { BrowseFilter, BrowseRequest, QueryOptions, QueryPlanSummary, QueryResult, ResultColumn, ResultSet } from '../core';

/** Business data through the extension's server process (xpp/data, @fno-studio/core's DataClient); the connection travels with each request. */
export class DataService {
    readonly client: core.DataClient;

    constructor(server: XppServerClient, access: DatabaseAccess) {
        this.client = new core.DataClient(server, target => access.signInFailed(target));
    }

    browse(target: DatabaseTarget, request: core.BrowseRequest, token?: vscode.CancellationToken): Promise<core.QueryResult> {
        return withAbortSignal(token, signal => this.client.browse(target, request, signal));
    }

    query(target: DatabaseTarget, sql: string, options: core.QueryOptions = {}, token?: vscode.CancellationToken): Promise<core.QueryResult> {
        return withAbortSignal(token, signal => this.client.query(target, sql, options, signal));
    }

    test(target: DatabaseTarget): Promise<{ user: string; database: string; version: string }> {
        return this.client.test(target);
    }

    /** The estimated plan of a SELECT (compiled, not run), summarized by the server. */
    plan(target: DatabaseTarget, sql: string): Promise<core.QueryPlanSummary> {
        return this.client.plan(target, sql);
    }
}
