import { randomUUID } from 'crypto';
import { Log, TokenProvider } from '../host';
import { httpFetch } from '../http';

const DevToolsService = 'https://developertools.powerplatform.microsoft.com';

/** Assets Visual Studio's Power Platform Tools download for a unified developer environment. */
export const ClientAssets = [
    { resource: 'systemmetadata', file: 'PackagesLocalDirectory.zip' },
    { resource: 'crossreference', file: 'DYNAMICSXREFDB.bak' },
    { resource: 'finopsvsix22', file: 'Microsoft.Dynamics.FinOps.ToolsVS2022.vsix' },
];

export interface UdeEnvironment {
    dataverseUrl: string;
    finOpsUrl?: string;
    tenantId?: string;
    deploymentType?: string;
    applicationVersion?: string;
    platformVersion?: string;
    environmentId?: string;
}

async function dataverseCall(url: string, token: string, method: 'GET' | 'POST'): Promise<any> {
    const response = await httpFetch(url, {
        method,
        headers: {
            Authorization: `Bearer ${token}`,
            Accept: 'application/json',
            'OData-MaxVersion': '4.0',
            'OData-Version': '4.0',
            ...(method === 'POST' ? { 'Content-Type': 'application/json' } : {}),
        },
        body: method === 'POST' ? '{}' : undefined,
    });
    const text = await response.text();
    if (!response.ok) {
        throw new Error(`${method} ${url} returned ${response.status}: ${text.slice(0, 500)}`);
    }
    return text ? JSON.parse(text) : {};
}

/** Calls an unbound Dataverse message: as an action (POST) first, then as a function (GET) if the org exposes it that way. */
async function invokeMessage(api: string, name: string, token: string): Promise<any> {
    try {
        return await dataverseCall(`${api}/${name}`, token, 'POST');
    } catch (err) {
        if (!/returned (404|405)/.test((err as Error).message)) {
            throw err;
        }
        return await dataverseCall(`${api}/${name}()`, token, 'GET');
    }
}

function field(obj: any, name: string): any {
    const key = Object.keys(obj ?? {}).find(k => k.toLowerCase() === name.toLowerCase());
    return key ? obj[key] : undefined;
}

/** Same lookups Power Platform Tools performs before downloading assets. */
export async function readUdeEnvironment(dataverseUrl: string, token: string): Promise<UdeEnvironment> {
    const api = `${dataverseUrl}/api/data/v9.2`;
    const integration = await invokeMessage(api, 'RetrieveFinanceAndOperationsIntegrationDetails', token);
    const details = await invokeMessage(api, 'msprov_getfinopsapplicationdetails', token);
    return {
        dataverseUrl,
        finOpsUrl: field(integration, 'Url'),
        tenantId: field(integration, 'TenantId'),
        deploymentType: field(details, 'applicationdeploymenttype'),
        applicationVersion: field(details, 'applicationversion'),
        platformVersion: field(details, 'platformversion'),
        environmentId: field(details, 'finopsenvironmentid'),
    };
}

/** Asks the developer tools service for a time-limited download link; nothing is downloaded. */
export async function getAssetDownloadUrl(env: UdeEnvironment, token: string, resource: string): Promise<string> {
    const version = (env.applicationVersion ?? '').replace(/\./g, '-');
    const response = await httpFetch(`${DevToolsService}/api/clientmetadata/${resource}?version=${version}`, {
        headers: {
            Authorization: `Bearer ${token}`,
            dataverseEnvironmentUri: `${env.dataverseUrl}/`,
            CorrelationId: randomUUID(),
            Accept: 'application/json',
        },
    });
    const text = await response.text();
    if (!response.ok || !text) {
        throw new Error(`DevTools service returned ${response.status} for ${resource}: ${text.slice(0, 300)}`);
    }
    return text.trim().replace(/^"|"$/g, '');
}

/** Size in bytes of a download link's file, when the storage reports it. */
export async function assetSize(sasUrl: string): Promise<number | undefined> {
    const response = await httpFetch(sasUrl, { method: 'HEAD' });
    const length = response.headers.get('content-length');
    return response.ok && length ? Number(length) : undefined;
}

/**
 * Every read-only step of the environment setup: sign in (interactively), read the environment, ask for each asset's
 * download link. True when all succeeded; false when the org is not a unified developer environment. Throws on errors.
 */
export async function checkUdeConnection(dataverseUrl: string, tokens: TokenProvider, log: Log): Promise<boolean> {
    const token = await tokens.getToken(dataverseUrl, { interactive: true });
    if (!token) {
        throw new Error(`Not signed in to ${dataverseUrl}.`);
    }
    log.line('1. Signed in.');

    const env = await readUdeEnvironment(dataverseUrl, token);
    log.line(`2. Environment: type ${env.deploymentType}, application ${env.applicationVersion}, platform ${env.platformVersion}, F&O ${env.finOpsUrl}`);
    if (env.deploymentType?.toLowerCase() !== 'onlinedev') {
        log.line('   Not a unified developer environment (OnlineDev); setup would stop here.');
        return false;
    }

    for (const asset of ClientAssets) {
        const url = await getAssetDownloadUrl(env, token, asset.resource);
        const size = await assetSize(url);
        log.line(`3. ${asset.file}: download link OK${size ? `, ${(size / 1024 ** 3).toFixed(2)} GB` : ''}`);
    }
    log.line('All read-only setup steps succeeded.');
    return true;
}
