import { randomUUID } from 'crypto';
import * as vscode from 'vscode';
import { httpFetch, resolveDataverseUrl, validateDataverseUrl } from './core';
import { orgName, selectedEnvironmentUrl, useEnvironment } from './environment';
import { showReportableError } from './reportProblem';

export { httpFetch, normalizeDataverseUrl, resolveDataverseUrl, validateDataverseUrl } from './core';

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

/** Access token for the Dataverse org, via VS Code's Microsoft account (optionally with a custom app registration). */
export async function getDataverseToken(dataverseUrl: string): Promise<string> {
    const scopes = [`${dataverseUrl}/.default`];
    const clientId = vscode.workspace.getConfiguration('xpp').get<string>('auth.clientId');
    if (clientId) {
        scopes.push(`VSCODE_CLIENT_ID:${clientId}`);
    }
    const session = await vscode.authentication.getSession('microsoft', scopes, { createIfNone: true });
    return session.accessToken;
}

/** The token when VS Code already has a session for the org; never shows a sign-in. */
export async function getDataverseTokenSilently(dataverseUrl: string): Promise<string | undefined> {
    const scopes = [`${dataverseUrl}/.default`];
    const clientId = vscode.workspace.getConfiguration('xpp').get<string>('auth.clientId');
    if (clientId) {
        scopes.push(`VSCODE_CLIENT_ID:${clientId}`);
    }
    const session = await vscode.authentication.getSession('microsoft', scopes, { silent: true }).then(s => s, () => undefined);
    return session?.accessToken;
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

async function blobSize(sasUrl: string): Promise<number | undefined> {
    const response = await httpFetch(sasUrl, { method: 'HEAD' });
    const length = response.headers.get('content-length');
    return response.ok && length ? Number(length) : undefined;
}

/** F&O Studio: Check Environment Connection — sign in and run every read-only step of the setup. */
export async function checkEnvironmentConnection(log: vscode.OutputChannel): Promise<void> {
    const input = await vscode.window.showInputBox({
        title: 'Dataverse environment URL',
        prompt: 'The Dataverse URL of your unified developer environment',
        placeHolder: 'https://yourorg.crm.dynamics.com',
        value: selectedEnvironmentUrl(),
        ignoreFocusOut: true,
        validateInput: validateDataverseUrl,
    });
    if (!input) {
        return;
    }
    log.show(true);
    let dataverseUrl: string;
    try {
        dataverseUrl = await resolveDataverseUrl(input);
    } catch (err) {
        vscode.window.showErrorMessage((err as Error).message);
        return;
    }
    log.appendLine(`--- Checking ${dataverseUrl}`);

    try {
        const token = await getDataverseToken(dataverseUrl);
        log.appendLine('1. Signed in.');

        const env = await readUdeEnvironment(dataverseUrl, token);
        log.appendLine(`2. Environment: type ${env.deploymentType}, application ${env.applicationVersion}, platform ${env.platformVersion}, F&O ${env.finOpsUrl}`);
        if (env.deploymentType?.toLowerCase() !== 'onlinedev') {
            log.appendLine('   Not a unified developer environment (OnlineDev); setup would stop here.');
            return;
        }

        for (const asset of ClientAssets) {
            const url = await getAssetDownloadUrl(env, token, asset.resource);
            const size = await blobSize(url);
            log.appendLine(`3. ${asset.file}: download link OK${size ? `, ${(size / 1024 ** 3).toFixed(2)} GB` : ''}`);
        }
        log.appendLine('All read-only setup steps succeeded.');
        // A check is not a switch: only the first environment is selected by it.
        const selected = selectedEnvironmentUrl();
        if (!selected) {
            await useEnvironment(dataverseUrl);
        } else if (orgName(selected) !== orgName(dataverseUrl)
            && await vscode.window.showInformationMessage(`${orgName(dataverseUrl)} is reachable. This window works with ${orgName(selected)}.`, 'Use This Environment') === 'Use This Environment') {
            await useEnvironment(dataverseUrl);
        }
    } catch (err) {
        log.appendLine(`FAILED: ${(err as Error).message}`);
        showReportableError('checkEnvironment', err, 'Environment check failed: ');
    }
}
