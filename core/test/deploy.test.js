const assert = require('assert/strict');
const fs = require('fs');
const os = require('os');
const path = require('path');
const { test } = require('node:test');
const core = require('../dist');
const { fakeTokens, fixture, replay, recorder } = require('./helpers');

const Url = 'https://contoso.crm.dynamics.com';

test('connect asks the token provider interactively for the org URL', async () => {
    const tokens = fakeTokens('abc');
    const dv = await core.Dataverse.connect('contoso.crm.dynamics.com/', tokens);
    assert.equal(dv.url, Url);
    assert.deepEqual(tokens.calls, [{ resource: Url, interactive: true }]);

    const http = replay([{ request: { method: 'GET', url: '/api/data/v9\\.2/WhoAmI$' }, response: { body: { UserId: 'u' } } }]);
    try {
        assert.deepEqual(await dv.json('GET', 'WhoAmI'), { UserId: 'u' });
        assert.equal(http.requests[0].headers.Authorization, 'Bearer abc');
        assert.equal(http.requests[0].headers['OData-Version'], '4.0');
    } finally {
        http.restore();
    }
});

test('connect fails when the provider has no token', async () => {
    await assert.rejects(core.Dataverse.connect(Url, { getToken: async () => undefined }), /Not signed in to https:\/\/contoso\.crm\.dynamics\.com\./);
});

test('errors carry the Dataverse message and no query string', async () => {
    const dv = new core.Dataverse(Url, `${Url}/api/data/v9.2`, 't');
    const http = replay([{ request: { method: 'GET', url: 'accounts' }, response: { status: 403, body: { error: { code: '0x80040220', message: 'Principal user is missing prvReadAccount privilege.' } } } }]);
    try {
        await assert.rejects(dv.json('GET', 'accounts?$top=1'), { message: 'GET accounts returned 403: Principal user is missing prvReadAccount privilege.' });
    } finally {
        http.restore();
    }
});

test('network errors name the host, never the URL', async () => {
    const original = globalThis.fetch;
    globalThis.fetch = async () => { throw Object.assign(new TypeError('fetch failed'), { cause: { code: 'ENOTFOUND' } }); };
    try {
        await assert.rejects(core.httpFetch('https://blob.example.net/file?sig=secret'),
            { message: 'Cannot reach blob.example.net: the host name does not resolve; check the URL, DNS or proxy (ENOTFOUND).' });
    } finally {
        globalThis.fetch = original;
    }
});

test('URL helpers', async () => {
    assert.equal(core.normalizeDataverseUrl(' contoso.crm.dynamics.com// '), Url);
    assert.equal(core.validateDataverseUrl('https://make.powerapps.com/environments/x')?.startsWith('That is a portal page.'), true);
    assert.equal(core.validateDataverseUrl(Url), undefined);
    assert.equal(await core.resolveDataverseUrl(`${Url}/main.aspx`), Url);
});

test('deploy packages: zip, row, upload, deploy action, job polling', async () => {
    const store = fs.mkdtempSync(path.join(os.tmpdir(), 'fno-core-deploy-'));
    try {
        fs.mkdirSync(path.join(store, 'TestPkg', 'bin'), { recursive: true });
        fs.mkdirSync(path.join(store, 'TestPkg', 'BuildLogs'), { recursive: true });
        fs.writeFileSync(path.join(store, 'TestPkg', 'bin', 'Dynamics.AX.TestPkg.dll'), 'dll');
        fs.writeFileSync(path.join(store, 'TestPkg', 'BuildLogs', 'build.log'), 'log');
        fs.writeFileSync(path.join(store, 'TestPkg', 'bin', 'BuildModelResult.xml'), 'x');
        fs.mkdirSync(path.join(store, 'NotBuilt'));

        const packages = await core.builtPackages(store);
        assert.deepEqual(packages, [{ name: 'TestPkg', folder: path.join(store, 'TestPkg') }]);

        const http = replay(fixture('deploy.json'));
        const out = recorder();
        try {
            const dv = new core.Dataverse(Url, `${Url}/api/data/v9.2`, 't');
            await core.deployPackages(dv, packages, 'Module', { log: out.log, progress: out.report, pollIntervalMs: 1 });
        } finally {
            http.restore();
        }
        assert.equal(http.remaining(), 0);

        const row = JSON.parse(http.requests[1].body);
        assert.deepEqual(row, { msprov_name: 'TestPkg_1_0_0_1_managed.zip', msprov_buildtype: 0, msprov_packagetype: 0, msprov_dbsyncoptions: 2, msprov_dbsyncvalue: '{"Modules":["TestPkg"]}' });

        const upload = http.requests[2];
        assert.equal(upload.headers['x-ms-file-name'], 'TestPkg_1_0_0_1_managed.zip');
        assert.equal(upload.headers['Content-Type'], 'application/octet-stream');
        const zip = upload.body.toString('latin1');
        assert.ok(zip.startsWith('PK'));
        assert.ok(zip.includes('TestPkg/bin/Dynamics.AX.TestPkg.dll'));
        assert.ok(zip.includes('fnomoduledefinition.json'));
        assert.ok(!zip.includes('BuildLogs/build.log'), 'BuildLogs is not deployed');
        assert.ok(!zip.includes('BuildModelResult.xml'), 'build results are not deployed');

        assert.deepEqual(JSON.parse(http.requests[3].body), {
            finopspackages: [{ '@odata.type': 'Microsoft.Dynamics.CRM.msprov_fnopackage', msprov_fnopackageid: '0b7e8a52-3c1d-4f6e-9a8b-7c6d5e4f3a21' }],
            buildtype: 0,
            packagetype: 0,
        });

        assert.deepEqual(out.lines, [
            `Packaging TestPkg from ${path.join(store, 'TestPkg')}…`,
            out.lines[1],
            'Deployment started (system job 9c8b7a65-4d3e-2f1a-0b9c-8d7e6f5a4b3c). This usually takes several minutes, longer with the database sync.',
            '  deployment: Waiting',
            '  deployment: In Progress',
            '  deployment: Succeeded',
        ]);
        assert.match(out.lines[1], /^Uploading TestPkg_1_0_0_1_managed\.zip \(\d+ KB\)…$/);
        assert.deepEqual(out.progress, out.lines.map(l => l.trim()));
    } finally {
        fs.rmSync(store, { recursive: true, force: true });
    }
});

test('a failed database sync reports the job message', async () => {
    const http = replay(fixture('dbsync-failed.json'));
    const out = recorder();
    try {
        const dv = new core.Dataverse(Url, `${Url}/api/data/v9.2`, 't');
        await assert.rejects(core.syncDatabase(dv, 'Module', ['TestPkg', 'Other'], { log: out.log, pollIntervalMs: 1 }),
            { message: 'The database sync ended with status Failed: Table XvsTest: field Foo has no extended data type.' });
        assert.deepEqual(JSON.parse(http.requests[1].body), { dbsynckind: 2, dbsyncargument: '{"Modules":["TestPkg","Other"]}' });
        assert.deepEqual(out.lines, ['Database sync started (system job 5a4b3c2d-1e0f-4a9b-8c7d-6e5f4a3b2c1d).', '  database sync: In Progress', '  database sync: Failed']);
    } finally {
        http.restore();
    }
});

test('an environment without deployment support is refused', async () => {
    const http = replay([{ request: { method: 'GET', url: 'solutions' }, response: { body: { value: [] } } }]);
    try {
        const dv = new core.Dataverse(Url, `${Url}/api/data/v9.2`, 't');
        await assert.rejects(core.syncDatabase(dv, 'Full', []),
            { message: `${Url} has no Finance and Operations deployment support (solution msprov_fnouploadersolution). Is it a unified developer environment?` });
    } finally {
        http.restore();
    }
});

test('cancelling stops waiting for the job', async () => {
    const controller = new AbortController();
    controller.abort();
    const dv = new core.Dataverse(Url, `${Url}/api/data/v9.2`, 't');
    await assert.rejects(core.waitForJob(dv, 'job-1', 'deployment', { signal: controller.signal }),
        { message: 'Stopped waiting. The deployment keeps running on the environment (system job job-1).' });
});

test('module definition and package version', () => {
    const now = new Date(Date.UTC(2026, 10, 24, 18, 30, 5));
    assert.equal(core.packageVersion(now), '13440.1860.50000.0');
    const definition = JSON.parse(core.moduleDefinition('TestPkg', 'Full', now));
    assert.equal(definition.TimestampUtc, '11/24/2016 18:30:05');
    assert.deepEqual(definition.DBSync, { SyncKind: 'Full', Arguments: '' });
    assert.equal(definition.Versions.PackageVersion, '13440.1860.50000.0');
    assert.equal(core.dbSyncArgument('Full', ['A']), '');
    assert.equal(core.asyncOperationId({ AsyncOperationId: 'x' }), 'x');
});
