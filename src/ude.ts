import * as vscode from 'vscode';
import { checkUdeConnection, resolveDataverseUrl, TokenProvider, validateDataverseUrl } from './core';
import { orgName, selectedEnvironmentUrl, useEnvironment } from './environment';
import { showReportableError } from './reportProblem';

export { ClientAssets, getAssetDownloadUrl, httpFetch, normalizeDataverseUrl, readUdeEnvironment, resolveDataverseUrl, validateDataverseUrl } from './core';
export type { UdeEnvironment } from './core';

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

/** The core's sign-in: VS Code's Microsoft account, interactive (may show a sign-in) or silent. */
export const dataverseTokens: TokenProvider = {
    getToken: (resource, { interactive }) => interactive ? getDataverseToken(resource) : getDataverseTokenSilently(resource),
};

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
        if (!await checkUdeConnection(dataverseUrl, dataverseTokens, { line: line => log.appendLine(line) })) {
            return;
        }
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
