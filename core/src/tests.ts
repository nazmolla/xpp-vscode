import { execFile } from 'child_process';
import { randomUUID } from 'crypto';
import { promises as fs } from 'fs';
import * as os from 'os';
import * as path from 'path';
import { Dataverse } from './dataverse';
import { Log, OperationOptions } from './host';

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

export interface DiscoveredTest {
    className: string;
    method: string;
}

export interface TestRunResult {
    summary: string;
    results: TestOutcome[];
}

/** Reads X++ classes: F&O Studio's metadata server (bridge action read_object), or anything that answers the same way. */
export interface ObjectReader {
    bridge(action: 'read_object', body: { objectType: 'AxClass'; objectName: string }): Promise<any>;
}

export interface DiscoverOptions {
    /** The classes to look in when the request names none (e.g. the open project's classes); undefined when there is no project. */
    projectClasses?: () => Promise<string[] | undefined>;
    log?: Log;
}

export interface RunTestsOptions extends OperationOptions, DiscoverOptions {
    reader: ObjectReader;
    /** Called once tests were found: the Dataverse org of the environment the tests run on. */
    dataverse: () => Promise<Dataverse>;
    /** The model whose package holds the tests. */
    model: string;
    /** The environment's runtime packages folder (RuntimePackagesDirectory, else FrameworkDirectory). */
    runtimeRoot: string;
    /** Time between status requests; 5 s by default. */
    pollIntervalMs?: number;
}

function escapeXml(text: string): string {
    return text.replace(/&/g, '&amp;').replace(/</g, '&lt;').replace(/>/g, '&gt;').replace(/"/g, '&quot;');
}

/** X++ test classes extend SysTestCase (directly or through a base); test methods start with "test" or carry [SysTestMethod]. */
export async function discoverTests(reader: ObjectReader, request: TestRunRequest, options: DiscoverOptions = {}): Promise<DiscoveredTest[]> {
    let classes = request.classes ?? [];
    if (classes.length === 0) {
        const projectClasses = await options.projectClasses?.();
        if (!projectClasses) {
            throw new Error('Pass test classes, or open a D365FO project whose test classes should run.');
        }
        classes = projectClasses;
    }

    const tests: DiscoveredTest[] = [];
    for (const className of classes) {
        const read = await reader.bridge('read_object', { objectType: 'AxClass', objectName: className });
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
    options.log?.line(`Discovered ${tests.length} test(s) in ${new Set(tests.map(t => t.className)).size} class(es).`);
    return tests;
}

/** Same XML the Test Explorer adapter serializes (Microsoft.Dynamics.TestTools.TestAdapter.Discovery.TestSuite). */
export function buildTestSuite(assemblyPath: string, tests: DiscoveredTest[]): string {
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

/** The .trx documents of a downloaded result: a zip (extracted into folder) or a single .trx. */
export async function extractTrx(zipBytes: Uint8Array, folder: string): Promise<string[]> {
    await fs.mkdir(folder, { recursive: true });
    const zipFile = path.join(folder, 'result.zip');
    await fs.writeFile(zipFile, zipBytes);
    const isZip = zipBytes[0] === 0x50 && zipBytes[1] === 0x4b;
    if (!isZip) {
        return [new TextDecoder().decode(zipBytes)];
    }
    // Expand-Archive is available on every Windows machine the D365 tools run on.
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

/** The text summary of a run: one header line, then a line per test with its message and, for failures, its Infolog. */
export function summarizeTests(results: TestOutcome[], requested: number, dataverseUrl: string): string {
    const passed = results.filter(r => r.outcome === 'Passed').length;
    const failed = results.filter(r => r.outcome !== 'Passed' && r.outcome !== 'NotExecuted' && r.outcome !== 'Skipped').length;
    const lines = [`TESTS ${failed === 0 && results.length > 0 ? 'PASSED' : 'FAILED'}: ${passed} passed, ${failed} failed, ${results.length - passed - failed} skipped (of ${requested} requested) on ${dataverseUrl}.`];
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
    return lines.join('\n');
}

export const NoTestsFound = 'No tests found. Test classes must extend SysTestCase (or a subclass) and test methods must be public and start with "test" or carry [SysTestMethod].';

/** Discovers the requested tests, runs them on the environment and returns the summary and every test's outcome. */
export async function runTests(request: TestRunRequest, options: RunTestsOptions): Promise<TestRunResult> {
    const log = (line: string) => options.log?.line(line);
    const tests = await discoverTests(options.reader, request, options);
    if (tests.length === 0) {
        return { summary: NoTestsFound, results: [] };
    }

    const dv = await options.dataverse();
    const dataverseUrl = dv.url;

    const model = options.model;
    const assemblyPath = path.join(options.runtimeRoot, model, 'bin', `Dynamics.AX.${model}.dll`);
    const suiteName = `${model}_0_done.tsx`;
    const suite = buildTestSuite(assemblyPath, tests);
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
        if (options.signal?.aborted) {
            throw new Error('Test run cancelled.');
        }
        if (Date.now() > deadline) {
            throw new Error(`Timed out after ${request.timeoutMinutes ?? 30} minutes waiting for test results (suite ${suiteId}).`);
        }
        await new Promise(r => setTimeout(r, options.pollIntervalMs ?? PollSeconds * 1000));
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
    return { summary: summarizeTests(results, tests.length, dataverseUrl), results };
}
