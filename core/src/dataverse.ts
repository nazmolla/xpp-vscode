import { promises as dns } from 'dns';
import { TokenProvider } from './host';
import { httpFetch } from './http';

export function normalizeDataverseUrl(input: string): string {
    const trimmed = input.trim().replace(/\/+$/, '');
    return /^https?:\/\//i.test(trimmed) ? trimmed : `https://${trimmed}`;
}

/** Undefined when input can be a Dataverse environment URL; otherwise what is wrong with it. */
export function validateDataverseUrl(input: string): string | undefined {
    let host: string;
    try {
        host = new URL(normalizeDataverseUrl(input)).hostname.toLowerCase();
    } catch {
        return 'Enter a URL such as https://yourorg.crm.dynamics.com';
    }
    if (/(^|\.)(powerplatform\.microsoft\.com|powerapps\.com)$/.test(host)) {
        return 'That is a portal page. Copy the environment\'s "Environment URL" (https://yourorg.crm.dynamics.com) from the Power Platform admin center.';
    }
    return undefined;
}

const DataverseRegions = ['crm', 'crm2', 'crm3', 'crm4', 'crm5', 'crm6', 'crm7', 'crm8', 'crm9', 'crm11', 'crm12', 'crm14', 'crm15', 'crm16', 'crm17', 'crm19', 'crm20', 'crm21'];

/** A unified developer environment's F&O host shares its org name with Dataverse; finds the regional Dataverse host. */
export async function resolveDataverseUrl(input: string): Promise<string> {
    const url = new URL(normalizeDataverseUrl(input));
    const match = /^([^.]+)\.operations\.dynamics\.com$/i.exec(url.hostname);
    if (!match) {
        return url.origin;
    }
    const found = await Promise.all(DataverseRegions.map(async region => {
        const host = `${match[1]}.${region}.dynamics.com`;
        try {
            await dns.lookup(host);
            return host;
        } catch {
            return undefined;
        }
    }));
    const host = found.find(h => h);
    if (!host) {
        throw new Error(`No Dataverse environment found for ${url.hostname}. Enter the Dataverse URL (https://<org>.crm.dynamics.com) from the Power Platform admin center.`);
    }
    return `https://${host}`;
}

/** Web API client of one Dataverse org. */
export class Dataverse {
    constructor(readonly url: string, private readonly api: string, private readonly token: string) {}

    /**
     * Signs in to the Dataverse org of an environment URL (Dataverse, or a unified environment's F&O URL). The token is
     * requested for the org URL, interactively.
     */
    static async connect(environmentUrl: string, tokens: TokenProvider): Promise<Dataverse> {
        const url = await resolveDataverseUrl(environmentUrl);
        const token = await tokens.getToken(url, { interactive: true });
        if (!token) {
            throw new Error(`Not signed in to ${url}.`);
        }
        return new Dataverse(url, `${url}/api/data/v9.2`, token);
    }

    async send(method: string, url: string, body?: unknown, headers: Record<string, string> = {}, signal?: AbortSignal): Promise<Response> {
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
            signal,
        });
        if (!response.ok) {
            throw new Error(`${method} ${url.split('?')[0]} returned ${response.status}: ${dataverseError(await response.text())}`);
        }
        return response;
    }

    async json(method: string, url: string, body?: unknown, headers: Record<string, string> = {}, signal?: AbortSignal): Promise<any> {
        const text = await (await this.send(method, url, body, headers, signal)).text();
        return text ? JSON.parse(text) : {};
    }

    /** Runs an unbound Dataverse action and returns its output parameters. */
    action(name: string, parameters: Record<string, unknown>, signal?: AbortSignal): Promise<any> {
        return this.json('POST', name, parameters, {}, signal);
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
