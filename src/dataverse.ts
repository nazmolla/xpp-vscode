import * as vscode from 'vscode';
import { selectedEnvironmentUrl } from './environment';
import { getDataverseToken, httpFetch, resolveDataverseUrl } from './ude';

/** Web API client of one Dataverse org, signed in through VS Code's Microsoft account. */
export class Dataverse {
    constructor(readonly url: string, private readonly api: string, private readonly token: string) {}

    /** The environment this window works with; fails with what to do when none is selected. */
    static async forSelectedEnvironment(): Promise<Dataverse> {
        const environmentUrl = selectedEnvironmentUrl();
        if (!environmentUrl) {
            throw new Error('No environment selected. Run "F&O Studio: Connect to Environment" or "F&O Studio: Select Environment" first.');
        }
        const url = await resolveDataverseUrl(environmentUrl);
        return new Dataverse(url, `${url}/api/data/v9.2`, await getDataverseToken(url));
    }

    async send(method: string, url: string, body?: unknown, headers: Record<string, string> = {}, token?: vscode.CancellationToken): Promise<Response> {
        const abort = new AbortController();
        const cancel = token?.onCancellationRequested(() => abort.abort());
        try {
            const response = await httpFetch(url.startsWith('http') ? url : `${this.api}/${url}`, {
                method,
                headers: {
                    Authorization: `Bearer ${this.token}`,
                    Accept: 'application/json',
                    'OData-MaxVersion': '4.0',
                    'OData-Version': '4.0',
                    ...(body !== undefined && !(body instanceof Uint8Array) ? { 'Content-Type': 'application/json' } : {}),
                    ...headers,
                },
                body: body === undefined ? undefined : body instanceof Uint8Array ? body : JSON.stringify(body),
                signal: abort.signal,
            });
            if (!response.ok) {
                throw new Error(`${method} ${url.split('?')[0]} returned ${response.status}: ${dataverseError(await response.text())}`);
            }
            return response;
        } finally {
            cancel?.dispose();
        }
    }

    async json(method: string, url: string, body?: unknown, headers: Record<string, string> = {}, token?: vscode.CancellationToken): Promise<any> {
        const text = await (await this.send(method, url, body, headers, token)).text();
        return text ? JSON.parse(text) : {};
    }

    /** Runs an unbound Dataverse action and returns its output parameters. */
    action(name: string, parameters: Record<string, unknown>, token?: vscode.CancellationToken): Promise<any> {
        return this.json('POST', name, parameters, {}, token);
    }
}

/** The message of a Dataverse error response, or the start of the body when it isn't one. */
function dataverseError(body: string): string {
    try {
        const message = JSON.parse(body)?.error?.message;
        if (typeof message === 'string' && message) {
            return message;
        }
    } catch {
        // Not JSON.
    }
    return body.slice(0, 600);
}
