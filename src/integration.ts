import { promises as fs } from 'fs';
import * as path from 'path';
import * as vscode from 'vscode';
import { readActiveEnvironment } from './environment';
import { httpFetch } from './ude';
import { XppServerClient } from './serverClient';

type Request = { method: 'GET' | 'POST'; path: string; body?: string; title: string };

/**
 * Integration Try It: query a data entity over OData, or call a custom service operation, on the active development
 * environment with the signed-in account, and save the request as an .http file (without the token). Only the active
 * environment's own URL is called; service calls ask first because a service may change data.
 */
export function registerIntegration(command: (id: string, handler: (...args: any[]) => unknown) => void, server: XppServerClient): void {
    command('xpp.tryEntity', async (arg?: { objectType?: string; entry?: { name: string }; item?: { objectName: string } }) => {
        const entity = arg?.item?.objectName ?? arg?.entry?.name ?? await vscode.window.showInputBox({ title: 'Data entity name', placeHolder: 'CustCustomerV3Entity' });
        if (!entity) {
            return;
        }
        const read = await server.bridge('read_object', { objectType: 'AxDataEntityView', objectName: entity });
        if (!read.success) {
            throw new Error(read.message ?? `Data entity ${entity} not found.`);
        }
        const props = (read.properties ?? {}) as Record<string, string>;
        if (!/^(yes|true)$/i.test(props.IsPublic ?? '') || !props.PublicCollectionName) {
            throw new Error(`${entity} is not public (IsPublic = Yes and a PublicCollectionName are needed for OData).`);
        }
        const filter = await vscode.window.showInputBox({ title: `${props.PublicCollectionName}: $filter (optional)`, placeHolder: "dataAreaId eq 'usmf'", ignoreFocusOut: true });
        if (filter === undefined) {
            return;
        }
        const top = await vscode.window.showQuickPick(['10', '50', '200'], { title: '$top' });
        if (!top) {
            return;
        }
        const crossCompany = await vscode.window.showQuickPick(['All companies (cross-company=true)', 'Default company only'], { title: 'Companies' });
        if (!crossCompany) {
            return;
        }
        const query = [`$top=${top}`, filter.trim() ? `$filter=${encodeURIComponent(filter.trim())}` : '', crossCompany.startsWith('All') ? 'cross-company=true' : ''].filter(Boolean).join('&');
        await send({ method: 'GET', path: `/data/${encodeURIComponent(props.PublicCollectionName)}?${query}`, title: `${entity} (OData)` });
    });

    command('xpp.tryService', async (arg?: { objectType?: string; entry?: { name: string }; item?: { objectName: string } }) => {
        const serviceName = arg?.item?.objectName ?? arg?.entry?.name ?? await vscode.window.showInputBox({ title: 'Service name', placeHolder: 'ABCOrderImportService' });
        if (!serviceName) {
            return;
        }
        const service = await server.bridge('read_object', { objectType: 'AxService', objectName: serviceName });
        if (!service.success) {
            throw new Error(service.message ?? `Service ${serviceName} not found.`);
        }
        const graph = parseGraph(service.typedMetadataJson ?? service.typedMetadata);
        const operations = (graph?.ServiceOperations ?? graph?.Operations ?? []) as { Name?: string; MethodName?: string }[];
        const className = String(graph?.Class ?? service.properties?.Class ?? '');
        const operation = await vscode.window.showQuickPick(operations.map(o => o.Name ?? o.MethodName ?? '').filter(Boolean), { title: `${serviceName}: operation` });
        if (!operation) {
            return;
        }
        const group = await vscode.window.showInputBox({ title: 'Service group', prompt: 'The service group that publishes this service', value: `${serviceName.replace(/Service$/, '')}ServiceGroup`, ignoreFocusOut: true,
            validateInput: v => /^\w+$/.test(v.trim()) ? undefined : 'A service group name.' });
        if (!group) {
            return;
        }
        const body = await requestBody(server, className, operations.find(o => (o.Name ?? o.MethodName) === operation)?.MethodName ?? operation);
        const doc = await vscode.workspace.openTextDocument({ language: 'json', content: JSON.stringify(body, null, 4) });
        await vscode.window.showTextDocument(doc, { preview: false });
        const choice = await vscode.window.showInformationMessage(`Edit the request body of ${serviceName}.${operation}, then send it.`, 'Send', 'Save as .http');
        const request: Request = { method: 'POST', path: `/api/services/${encodeURIComponent(group.trim())}/${encodeURIComponent(serviceName)}/${encodeURIComponent(operation)}`, body: doc.getText(), title: `${serviceName}.${operation}` };
        if (choice === 'Save as .http') {
            await saveHttp({ ...request, body: doc.getText() });
        } else if (choice === 'Send') {
            const ok = await vscode.window.showWarningMessage(`Call ${serviceName}.${operation} on the environment?`, { modal: true, detail: 'The service runs its code with your account and may change data.' }, 'Call');
            if (ok === 'Call') {
                await send({ ...request, body: doc.getText() });
            }
        }
    });
}

function parseGraph(value: unknown): any {
    if (typeof value === 'string') {
        try {
            return JSON.parse(value);
        } catch {
            return undefined;
        }
    }
    return value;
}

/** A JSON body from the operation's parameters: data contracts become objects of their DataMember parm methods. */
async function requestBody(server: XppServerClient, className: string, method: string): Promise<Record<string, unknown>> {
    const cls = className ? await server.bridge('read_object', { objectType: 'AxClass', objectName: className }).catch(() => undefined) : undefined;
    const source: string = (cls?.methods ?? []).find((m: { name: string }) => m.name.toLowerCase() === method.toLowerCase())?.source ?? '';
    const params = /\(([^)]*)\)/.exec(source.replace(/\/\/.*$/gm, ''))?.[1] ?? '';
    const body: Record<string, unknown> = {};
    for (const param of params.split(',').map(p => p.trim()).filter(Boolean)) {
        const [type, name] = param.replace(/=.*$/, '').trim().split(/\s+/);
        if (name) {
            body[name] = await sampleValue(server, type, 0);
        }
    }
    return body;
}

async function sampleValue(server: XppServerClient, type: string, depth: number): Promise<unknown> {
    switch (type.toLowerCase()) {
        case 'str': return '';
        case 'int': case 'int64': return 0;
        case 'real': return 0.0;
        case 'boolean': return false;
        case 'date': return '1900-01-01';
        case 'utcdatetime': return '1900-01-01T00:00:00Z';
    }
    if (depth > 2) {
        return null;
    }
    const contract = await server.bridge('read_object', { objectType: 'AxClass', objectName: type }).catch(() => undefined);
    if (!contract?.success) {
        return '';
    }
    const result: Record<string, unknown> = {};
    for (const m of (contract.methods ?? []) as { name: string; source?: string }[]) {
        const source = m.source ?? '';
        if (!/DataMember(Attribute)?/i.test(source)) {
            continue;
        }
        const name = /DataMember(?:Attribute)?\s*\(\s*['"]([^'"]+)['"]/i.exec(source)?.[1] ?? m.name.replace(/^parm/, '');
        const returnType = /(?:public|private|protected)?\s*(\w+)\s+\w+\s*\(/.exec(source.replace(/\[[^\]]*\]/g, ''))?.[1] ?? 'str';
        result[name] = await sampleValue(server, returnType, depth + 1);
    }
    return result;
}

async function send(request: Request): Promise<void> {
    const env = await readActiveEnvironment();
    if (!env.finOpsUrl) {
        throw new Error('The active environment has no web client URL (CloudInstanceURL).');
    }
    const base = new URL(env.finOpsUrl).origin;
    const session = await vscode.authentication.getSession('microsoft', [`${base}/.default`], { createIfNone: true });
    const response = await vscode.window.withProgress({ location: vscode.ProgressLocation.Notification, title: `${request.method} ${request.title}…` }, () =>
        httpFetch(base + request.path, {
            method: request.method,
            headers: { Authorization: `Bearer ${session.accessToken}`, Accept: 'application/json', ...request.body ? { 'Content-Type': 'application/json' } : {} },
            body: request.method === 'POST' ? request.body : undefined,
        }));
    const text = await response.text();
    let content = text;
    try {
        content = JSON.stringify(JSON.parse(text), null, 2);
    } catch {
        // Not JSON: shown as it came.
    }
    const header = `// ${request.method} ${request.path}\n// ${response.status} ${response.statusText}\n`;
    await vscode.window.showTextDocument(await vscode.workspace.openTextDocument({ language: 'jsonc', content: header + content }), { viewColumn: vscode.ViewColumn.Beside, preview: true });
    const choice = await vscode.window.showInformationMessage(`${request.title}: ${response.status} ${response.statusText}`, 'Save as .http');
    if (choice) {
        await saveHttp(request);
    }
}

/** .xpp/requests/<name>.http in the workspace (REST Client format); the token is a variable, never written. */
async function saveHttp(request: Request): Promise<void> {
    const root = vscode.workspace.workspaceFolders?.[0]?.uri.fsPath;
    if (!root) {
        throw new Error('Open a workspace folder to save requests.');
    }
    const env = await readActiveEnvironment();
    const folder = path.join(root, '.xpp', 'requests');
    await fs.mkdir(folder, { recursive: true });
    const file = path.join(folder, `${request.title.replace(/[^\w.-]+/g, '_')}.http`);
    const content = [
        `@baseUrl = ${env.finOpsUrl ? new URL(env.finOpsUrl).origin : 'https://your-environment.operations.dynamics.com'}`,
        '# @token: an access token for @baseUrl (for example from az account get-access-token --resource @baseUrl)', '',
        `### ${request.title}`,
        `${request.method} {{baseUrl}}${request.path}`,
        'Authorization: Bearer {{token}}',
        'Accept: application/json',
        ...request.body ? ['Content-Type: application/json', '', request.body] : [], '',
    ].join('\n');
    await fs.writeFile(file, content, 'utf8');
    await vscode.window.showTextDocument(vscode.Uri.file(file));
}
