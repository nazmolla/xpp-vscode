import { randomUUID } from 'crypto';
import { promises as fs } from 'fs';
import * as os from 'os';
import * as path from 'path';
import * as vscode from 'vscode';
import { readActiveEnvironment, selectedEnvironmentUrl } from './environment';
import { findActiveProject, readProject } from './project';
import { XppServerClient } from './serverClient';
import { getDataverseToken, resolveDataverseUrl } from './ude';

/**
 * Runs X++ SysTest tests on the unified developer environment's cloud runtime, the way Visual Studio's Test Explorer
 * does: a test suite (.tsx) is uploaded to Dataverse (msprov_unittestsuite), executed with
 * msprov_unittestexecutiononfinopsasync, and the .trx result is downloaded. The tests run against the code that is
 * deployed to the environment, so build and deploy first.
 */

const ExecutorUri = 'executor://SysTestExecutor/v1';
const PollSeconds = 5;

export interface TestRunRequest {
    classes?: string[];
    methods?: string[];
    timeoutMinutes?: number;
}

export interface TestOutcome {
    name: string;
    outcome: string;
    message?: string;
    infolog?: string;
    duration?: string;
}

interface DiscoveredTest {
    className: string;
    method: string;
}

function escapeXml(text: string): string {
    return text.replace(/&/g, '&amp;').replace(/</g, '&lt;').replace(/>/g, '&gt;').replace(/"/g, '&quot;');
}

/** X++ test classes extend SysTestCase (directly or through a base); test methods start with "test" or carry [SysTestMethod]. */
async function discoverTests(server: XppServerClient, request: TestRunRequest, log: (line: string) => void): Promise<DiscoveredTest[]> {
    let classes = request.classes ?? [];
    if (classes.length === 0) {
        const project = await readProject();
        if (!project) {
            throw new Error('Pass test classes, or open a D365FO project whose test classes should run.');
        }
        classes = project.items.filter(i => i.objectType === 'AxClass').map(i => i.objectName);
    }

    const tests: DiscoveredTest[] = [];
    for (const className of classes) {
        const read = await server.bridge('read_object', { objectType: 'AxClass', objectName: className });
        if (!read?.success) {
            if (request.classes?.length) {
                throw new Error(`Class ${className} not found.`);
            }
            continue;
        }
        const declaration: string = read.declaration ?? '';
        if (!/\bextends\s+\w*Test\w*/i.test(declaration)) {
            continue;
        }
        for (const method of read.methods ?? []) {
            const source: string = method.source ?? '';
            const isTest = /^test/i.test(method.name) || /\[\s*SysTestMethod/i.test(source);
            if (!isTest || /\b(private|protected)\b[^(]*\(/.test(source.split('{')[0])) {
                continue;
            }
            if (request.methods?.length && !request.methods.some(m => m.toLowerCase() === method.name.toLowerCase())) {
                continue;
            }
            tests.push({ className: read.objectName ?? className, method: method.name });
        }
    }
    log(`Discovered ${tests.length} test(s) in ${new Set(tests.map(t => t.className)).size} class(es).`);
    return tests;
}

/** Same XML the Test Explorer adapter serializes (Microsoft.Dynamics.TestTools.TestAdapter.Discovery.TestSuite). */
function buildSuite(assemblyPath: string, tests: DiscoveredTest[]): string {
    const cases = tests.map(t => [
        '    <TestCaseInformation>',
        `      <TestKey>${randomUUID()}</TestKey>`,
        `      <TestCaseName>${escapeXml(t.className + '.' + t.method)}</TestCaseName>`,
        `      <FullyQualifiedName>${escapeXml('Dynamics.AX.Application.' + t.className + '.' + t.method)}</FullyQualifiedName>`,
        '      <ExpectedRuntime>0</ExpectedRuntime>',
        `      <TestSource>${escapeXml(assemblyPath)}</TestSource>`,
        `      <ExecutorURI>${ExecutorUri}</ExecutorURI>`,
        '    </TestCaseInformation>',
    ].join('\r\n')).join('\r\n');
    return [
        '<?xml version="1.0" encoding="utf-8"?>',
        '<TestSuite xmlns:xsi="http://www.w3.org/2001/XMLSchema-instance" xmlns:xsd="http://www.w3.org/2001/XMLSchema">',
        `  <AssemblyPath>${escapeXml(assemblyPath)}</AssemblyPath>`,
        '  <RunAll>false</RunAll>',
        '  <TestCases>',
        cases,
        '  </TestCases>',
        '  <TestFramework>xpptest</TestFramework>',
        '</TestSuite>',
    ].join('\r\n');
}

class Dataverse {
    constructor(private readonly api: string, private readonly token: string) {}

    async send(method: string, url: string, body?: any, headers: Record<string, string> = {}): Promise<Response> {
        const response = await fetch(url.startsWith('http') ? url : `${this.api}/${url}`, {
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
        });
        if (!response.ok) {
            throw new Error(`${method} ${url} returned ${response.status}: ${(await response.text()).slice(0, 600)}`);
        }
        return response;
    }

    async json(method: string, url: string, body?: any, headers: Record<string, string> = {}): Promise<any> {
        const text = await (await this.send(method, url, body, headers)).text();
        return text ? JSON.parse(text) : {};
    }
}

function formatted(record: any, field: string): string {
    return String(record?.[`${field}@OData.Community.Display.V1.FormattedValue`] ?? record?.[field] ?? '');
}

/** Reads UnitTestResult entries of a .trx (TRX schema used by the cloud runtime). */
export function parseTrx(xml: string): TestOutcome[] {
    const results: TestOutcome[] = [];
    const decode = (s: string) => s.replace(/&lt;/g, '<').replace(/&gt;/g, '>').replace(/&quot;/g, '"').replace(/&apos;/g, '\'').replace(/&amp;/g, '&');
    for (const m of xml.matchAll(/<UnitTestResult\b([^>]*?)(\/>|>([\s\S]*?)<\/UnitTestResult>)/g)) {
        const attrs = m[1];
        const body = m[3] ?? '';
        const attr = (name: string) => decode(new RegExp(`\\b${name}="([^"]*)"`).exec(attrs)?.[1] ?? '');
        const message = /<Message>([\s\S]*?)<\/Message>/.exec(body)?.[1];
        const infolog = /<infolog>([\s\S]*?)<\/infolog>/i.exec(body)?.[1];
        results.push({
            name: attr('testName'),
            outcome: attr('outcome') || 'Failed',
            duration: attr('duration'),
            message: message ? decode(message.replace(/<!\[CDATA\[|\]\]>/g, '')).trim() : undefined,
            infolog: infolog ? decode(infolog.replace(/<[^>]+>/g, ' ').replace(/\s+/g, ' ')).trim() : undefined,
        });
    }
    return results;
}

async function extractTrx(zipBytes: Uint8Array, folder: string): Promise<string[]> {
    await fs.mkdir(folder, { recursive: true });
    const zipFile = path.join(folder, 'result.zip');
    await fs.writeFile(zipFile, zipBytes);
    const isZip = zipBytes[0] === 0x50 && zipBytes[1] === 0x4b;
    if (!isZip) {
        return [new TextDecoder().decode(zipBytes)];
    }
    // Expand-Archive is available on every Windows machine the D365 tools run on.
    const { execFile } = await import('child_process');
    const target = path.join(folder, 'result');
    await new Promise<void>((resolve, reject) => execFile('powershell', ['-NoProfile', '-Command',
        `Expand-Archive -LiteralPath '${zipFile.replace(/'/g, "''")}' -DestinationPath '${target.replace(/'/g, "''")}' -Force`],
    { windowsHide: true }, err => err ? reject(err) : resolve()));
    const trx: string[] = [];
    const walk = async (dir: string) => {
        for (const entry of await fs.readdir(dir, { withFileTypes: true })) {
            const full = path.join(dir, entry.name);
            if (!path.resolve(full).startsWith(path.resolve(target))) {
                continue;
            }
            if (entry.isDirectory()) {
                await walk(full);
            } else if (entry.name.toLowerCase().endsWith('.trx')) {
                trx.push(await fs.readFile(full, 'utf8'));
            }
        }
    };
    await walk(target);
    return trx;
}

export async function runTests(server: XppServerClient, request: TestRunRequest, log: (line: string) => void, token?: vscode.CancellationToken): Promise<{ summary: string; results: TestOutcome[] }> {
    const env = await readActiveEnvironment();
    const project = await readProject(await findActiveProject());
    const model = project?.model;
    if (!model) {
        throw new Error('Open a D365FO project first: tests run from the package of the project\'s model.');
    }

    const tests = await discoverTests(server, request, log);
    if (tests.length === 0) {
        return { summary: 'No tests found. Test classes must extend SysTestCase (or a subclass) and test methods must be public and start with "test" or carry [SysTestMethod].', results: [] };
    }

    const environmentUrl = selectedEnvironmentUrl();
    if (!environmentUrl) {
        throw new Error('No environment selected. Run "X++: Connect to Environment" or "X++: Select Environment" first.');
    }
    const dataverseUrl = await resolveDataverseUrl(environmentUrl);
    const dv = new Dataverse(`${dataverseUrl}/api/data/v9.2`, await getDataverseToken(dataverseUrl));

    const runtimeRoot = env.runtimePackagesDirectory ?? env.frameworkDirectory;
    const assemblyPath = path.join(runtimeRoot, model, 'bin', `Dynamics.AX.${model}.dll`);
    const suiteName = `${model}_0_done.tsx`;
    const suite = buildSuite(assemblyPath, tests);
    const workFolder = path.join(os.tmpdir(), 'xpp-vscode-tests', String(Date.now()));
    await fs.mkdir(workFolder, { recursive: true });
    await fs.writeFile(path.join(workFolder, suiteName), suite, 'utf8');
    log(`Suite: ${path.join(workFolder, suiteName)}`);

    log('Uploading test suite to Dataverse...');
    const created = await dv.send('POST', 'msprov_unittestsuites', { msprov_unittestsuitename: suiteName });
    const suiteId = /\(([0-9a-f-]{36})\)/i.exec(created.headers.get('OData-EntityId') ?? '')?.[1];
    if (!suiteId) {
        throw new Error('Dataverse did not return the test suite record id.');
    }
    await dv.send('PATCH', `msprov_unittestsuites(${suiteId})/msprov_unittestsuiteconfig`, new TextEncoder().encode(suite),
        { 'Content-Type': 'application/octet-stream', 'x-ms-file-name': suiteName });

    log('Starting execution on the cloud runtime...');
    const started = await dv.json('POST', 'msprov_unittestexecutiononfinopsasync', {
        unittestsuiteentitycollection: [{ '@odata.type': 'Microsoft.Dynamics.CRM.msprov_unittestsuite', msprov_unittestsuiteid: suiteId }],
        verbose: false,
        waitfordebugger: false,
    });
    const asyncId: string | undefined = started.asyncoperationid?.asyncoperationid ?? started.asyncoperationid;

    const deadline = Date.now() + (request.timeoutMinutes ?? 30) * 60_000;
    const annotations = { Prefer: 'odata.include-annotations="OData.Community.Display.V1.FormattedValue"' };
    for (;;) {
        if (token?.isCancellationRequested) {
            throw new Error('Test run cancelled.');
        }
        if (Date.now() > deadline) {
            throw new Error(`Timed out after ${request.timeoutMinutes ?? 30} minutes waiting for test results (suite ${suiteId}).`);
        }
        await new Promise(r => setTimeout(r, PollSeconds * 1000));
        if (asyncId) {
            const job = await dv.json('GET', `asyncoperations(${asyncId})?$select=statecode,statuscode,message,friendlymessage`, undefined, annotations);
            if (job.statecode === 3 && job.statuscode !== 30) {
                throw new Error(`Test execution job failed (${formatted(job, 'statuscode')}): ${job.friendlymessage || job.message || ''}`);
            }
        }
        const record = await dv.json('GET', `msprov_unittestsuites(${suiteId})?$select=statecode,statuscode`, undefined, annotations);
        const state = formatted(record, 'statecode');
        const status = formatted(record, 'statuscode');
        log(`  status: ${state} / ${status}`);
        if (/completed|inactive/i.test(state)) {
            if (!/succeeded|completed/i.test(status)) {
                throw new Error(`Test suite ended with status ${status}.`);
            }
            break;
        }
    }

    log('Downloading results...');
    const download = await dv.send('GET', `msprov_unittestsuites(${suiteId})/msprov_unittestresult/$value`, undefined, { Accept: 'application/octet-stream' });
    const trxFiles = await extractTrx(new Uint8Array(await download.arrayBuffer()), workFolder);
    const results = trxFiles.flatMap(parseTrx);

    const passed = results.filter(r => r.outcome === 'Passed').length;
    const failed = results.filter(r => r.outcome !== 'Passed' && r.outcome !== 'NotExecuted' && r.outcome !== 'Skipped').length;
    const lines = [`TESTS ${failed === 0 && results.length > 0 ? 'PASSED' : 'FAILED'}: ${passed} passed, ${failed} failed, ${results.length - passed - failed} skipped (of ${tests.length} requested) on ${dataverseUrl}.`];
    for (const r of results) {
        lines.push(`  ${r.outcome.toUpperCase()} ${r.name}${r.duration ? ` (${r.duration})` : ''}`);
        if (r.message) {
            lines.push(`    ${r.message.replace(/\s+/g, ' ')}`);
        }
        if (r.infolog && r.outcome !== 'Passed') {
            lines.push(`    Infolog: ${r.infolog}`);
        }
    }
    if (results.length === 0) {
        lines.push('  No results were returned. Check that the package is deployed and the test classes exist on the environment.');
    }
    return { summary: lines.join('\n'), results };
}

interface RunTestsInput {
    classes?: string[];
    methods?: string[];
    timeoutMinutes?: number;
}

/** Registers the agent tool and the command; both use the same runner. */
export function registerTests(context: vscode.ExtensionContext, server: XppServerClient): void {
    const output = vscode.window.createOutputChannel('X++ Tests');
    context.subscriptions.push(output);

    context.subscriptions.push(vscode.lm.registerTool<RunTestsInput>('xpp_run_tests', {
        prepareInvocation: options => ({
            invocationMessage: `Running X++ tests${options.input.classes?.length ? ' in ' + options.input.classes.join(', ') : ''} on the cloud runtime`,
            confirmationMessages: {
                title: 'Run X++ tests on the environment',
                message: 'The tests run on the connected environment against the deployed code and may create test data there.',
            },
        }),
        invoke: async (options, token) => {
            const lines: string[] = [];
            try {
                const result = await runTests(server, options.input, line => { lines.push(line); output.appendLine(line); }, token);
                output.appendLine(result.summary);
                return new vscode.LanguageModelToolResult([new vscode.LanguageModelTextPart(result.summary)]);
            } catch (err) {
                const message = `TEST RUN FAILED: ${(err as Error).message}\n${lines.slice(-5).join('\n')}`;
                output.appendLine(message);
                return new vscode.LanguageModelToolResult([new vscode.LanguageModelTextPart(message)]);
            }
        },
    }));

    context.subscriptions.push(vscode.commands.registerCommand('xpp.runTests', async (arg?: { objectName?: string; objectType?: string }) => {
        const classes = arg?.objectType === 'AxClass' && arg.objectName ? [arg.objectName] : undefined;
        output.show(true);
        await vscode.window.withProgress({ location: vscode.ProgressLocation.Notification, title: 'Running X++ tests', cancellable: true }, async (_progress, token) => {
            try {
                const result = await runTests(server, { classes }, line => output.appendLine(line), token);
                output.appendLine(result.summary);
                const failed = result.results.some(r => r.outcome !== 'Passed');
                void (failed ? vscode.window.showErrorMessage : vscode.window.showInformationMessage)(result.summary.split('\n')[0]);
            } catch (err) {
                output.appendLine(`Test run failed: ${(err as Error).message}`);
                void vscode.window.showErrorMessage(`Test run failed: ${(err as Error).message}`);
            }
        });
    }));
}
