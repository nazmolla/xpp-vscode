import * as vscode from 'vscode';
import { DatabaseAccess } from './connection';
import { DataService, ResultSet } from './dataService';

interface QueryDataInput {
    sql: string;
    maxRows?: number;
}

const DefaultRows = 50;
const MaxRows = 500;
const MaxOutputChars = 20_000;

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
            const text = await run(access, data, options.input, token);
            return new vscode.LanguageModelToolResult([new vscode.LanguageModelTextPart(text)]);
        },
    }));
}

async function run(access: DatabaseAccess, data: DataService, input: QueryDataInput, token: vscode.CancellationToken): Promise<string> {
    if (!input?.sql?.trim()) {
        return 'QUERY FAILED: pass a SELECT statement in "sql".';
    }
    const target = await access.current().catch(() => undefined);
    if (!target) {
        return 'NO DATABASE ACCESS: the environment\'s database needs temporary credentials. Ask the user to run "F&O Studio: Request Database Credentials", then try again.';
    }
    const maxRows = Math.max(1, Math.min(MaxRows, Math.floor(input.maxRows ?? DefaultRows)));
    try {
        const result = await data.query(target, input.sql, { maxRows }, token);
        const sets = result.resultSets.map((set, i) => formatSet(set, result.resultSets.length > 1 ? i + 1 : undefined));
        let text = `QUERY OK on ${target.database} (${result.elapsedMs} ms, read-only).\n${sets.join('\n\n') || 'No result set.'}`;
        if (text.length > MaxOutputChars) {
            text = text.slice(0, MaxOutputChars) + '\n… output cut; select fewer columns or rows.';
        }
        return text;
    } catch (err) {
        return `QUERY FAILED: ${(err as Error).message}`;
    }
}

/** A fenced SQL block whose fence is longer than any backtick run in the text, so the text can't end it early. */
function codeBlock(sql: string): string {
    const longest = Math.max(0, ...[...sql.matchAll(/`+/g)].map(m => m[0].length));
    const fence = '`'.repeat(Math.max(3, longest + 1));
    return `${fence}sql\n${sql}\n${fence}`;
}

/** Tab-separated rows: compact for the model's context, exact for RecIds (they arrive as text). */
function formatSet(set: ResultSet, number?: number): string {
    const clean = (v: unknown) => v === null || v === undefined ? 'NULL' : String(v).replace(/[\t\r\n]+/g, ' ');
    const lines = [
        `${number ? `Result ${number}: ` : ''}${set.rows.length}${set.truncated ? '+' : ''} row(s)${set.truncated ? ' (limit reached)' : ''}`,
        set.columns.map(c => c.name).join('\t'),
        ...set.rows.map(r => r.map(clean).join('\t')),
    ];
    return lines.join('\n');
}
