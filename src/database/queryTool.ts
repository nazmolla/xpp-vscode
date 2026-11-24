import * as vscode from 'vscode';
import { readOnlyQuery } from '../core';
import { withAbortSignal } from '../coreHost';
import { DatabaseAccess } from './connection';
import { DataService } from './dataService';

interface QueryDataInput {
    sql: string;
    maxRows?: number;
}

const NoAccess = 'NO DATABASE ACCESS: the environment\'s database needs temporary credentials. Ask the user to run "F&O Studio: Request Database Credentials", then try again.';

/**
 * xpp_query_data: read-only SQL against the environment's business database for the agent. SELECT only, rolled back,
 * row-limited, confirmed by the user; the connection and credentials stay in the extension.
 */
export function registerQueryTool(context: vscode.ExtensionContext, access: DatabaseAccess, data: DataService): void {
    context.subscriptions.push(vscode.lm.registerTool<QueryDataInput>('xpp_query_data', {
        prepareInvocation: options => ({
            invocationMessage: 'Querying the environment database',
            confirmationMessages: {
                title: 'Run a read-only query on the environment database',
                message: new vscode.MarkdownString(`The query runs read-only (SELECT only, rolled back).\n\n${codeBlock(options.input.sql ?? '')}`),
            },
        }),
        invoke: async (options, token) => {
            const text = await withAbortSignal(token, signal => readOnlyQuery(data.client, () => access.current(), options.input, { noAccessMessage: NoAccess, signal }));
            return new vscode.LanguageModelToolResult([new vscode.LanguageModelTextPart(text)]);
        },
    }));
}

/** A fenced SQL block whose fence is longer than any backtick run in the text, so the text can't end it early. */
function codeBlock(sql: string): string {
    const longest = Math.max(0, ...[...sql.matchAll(/`+/g)].map(m => m[0].length));
    const fence = '`'.repeat(Math.max(3, longest + 1));
    return `${fence}sql\n${sql}\n${fence}`;
}
