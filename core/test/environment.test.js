const assert = require('assert/strict');
const fs = require('fs');
const os = require('os');
const path = require('path');
const { test } = require('node:test');
const core = require('../dist');
const { fakeTokens, fixture, replay, recorder } = require('./helpers');

const Url = 'https://contoso.crm.dynamics.com';

test('the connection check signs in interactively and runs every read-only setup step', async () => {
    const tokens = fakeTokens('abc');
    const http = replay(fixture('ude-check.json'));
    const out = recorder();
    let ok;
    try {
        ok = await core.checkUdeConnection(Url, tokens, out.log);
    } finally {
        http.restore();
    }
    assert.equal(ok, true);
    assert.equal(http.remaining(), 0);
    assert.deepEqual(tokens.calls, [{ resource: Url, interactive: true }]);
    const assets = http.requests.filter(r => r.url.startsWith('https://developertools'));
    assert.ok(assets.every(r => r.headers.Authorization === 'Bearer abc' && r.headers.dataverseEnvironmentUri === `${Url}/`));
    assert.deepEqual(out.lines, [
        '1. Signed in.',
        '2. Environment: type OnlineDev, application 10.0.2790.108, platform 7.0.7996.141, F&O https://contoso.operations.dynamics.com',
        '3. PackagesLocalDirectory.zip: download link OK, 8.50 GB',
        '3. DYNAMICSXREFDB.bak: download link OK',
        '3. Microsoft.Dynamics.FinOps.ToolsVS2022.vsix: download link OK, 0.50 GB',
        'All read-only setup steps succeeded.',
    ]);
});

test('the connection check stops at an environment that is not a unified developer environment', async () => {
    const recordings = fixture('ude-check.json').slice(0, 3);
    recordings[2].response.body.applicationdeploymenttype = 'Sandbox';
    const http = replay(recordings);
    const out = recorder();
    try {
        assert.equal(await core.checkUdeConnection(Url, fakeTokens(), out.log), false);
    } finally {
        http.restore();
    }
    assert.equal(out.lines.at(-1), '   Not a unified developer environment (OnlineDev); setup would stop here.');
});

test('environments come from XPPConfig, the newest configuration of each org', async () => {
    const appData = fs.mkdtempSync(path.join(os.tmpdir(), 'fno-core-env-'));
    const saved = process.env.LOCALAPPDATA;
    process.env.LOCALAPPDATA = appData;
    try {
        const folder = path.join(appData, 'Microsoft', 'Dynamics365', 'XPPConfig');
        fs.mkdirSync(folder, { recursive: true });
        const write = (name, json) => fs.writeFileSync(path.join(folder, name), '\uFEFF' + JSON.stringify(json));
        write('contoso___10.0.2000.1.json', { FrameworkDirectory: 'C:\\old', CloudInstanceURL: 'https://old.example.com' });
        write('contoso___10.0.2790.108.json', {
            FrameworkDirectory: 'C:\\PLD', ModelStoreFolder: 'C:\\Custom', ReferencePackagesPaths: ['C:\\PLD'], RuntimePackagesDirectory: 'C:\\Links',
            CrossReferencesDbServerName: '(LocalDB)\\MSSQLLocalDB', CrossReferencesDatabaseName: 'XRef_contoso', CloudInstanceURL: 'https://contoso.operations.dynamics.com/',
        });
        write('fabrikam___10.0.2790.108.json', { FrameworkDirectory: 'C:\\F' });
        fs.writeFileSync(path.join(folder, 'notes.txt'), '');

        const all = await core.listEnvironments();
        assert.deepEqual(all.map(e => `${e.org} ${e.applicationVersion} ${e.finOpsUrl ?? '-'}`), ['contoso 10.0.2790.108 https://contoso.operations.dynamics.com', 'fabrikam 10.0.2790.108 -']);

        const env = await core.loadEnvironment('contoso.crm3.dynamics.com');
        assert.equal(env.name, 'contoso');
        assert.equal(env.binDirectory, path.join('C:\\PLD', 'bin'));
        assert.equal(env.modelStoreFolder, 'C:\\Custom');
        assert.equal(env.applicationVersion, '10.0.2790.108');
        assert.equal(env.dataverseUrl, 'contoso.crm3.dynamics.com');
        assert.equal(env.local, false);
        assert.equal(core.orgName('https://Contoso.crm3.dynamics.com/main.aspx'), 'contoso');
    } finally {
        process.env.LOCALAPPDATA = saved;
        fs.rmSync(appData, { recursive: true, force: true });
    }
});

test('deployed versus built', () => {
    assert.equal(core.deployStaleness(undefined), undefined);
    assert.equal(core.deployStaleness({ deployedAt: 5 }), undefined);
    assert.equal(core.deployStaleness({ builtAt: 5, deployedAt: 6 }).stale, false);
    const stale = core.deployStaleness({ builtAt: 7, deployedAt: 6 });
    assert.equal(stale.stale, true);
    assert.equal(core.staleDeployMessage('testcust', stale), `testcust was built (${new Date(7).toLocaleString()}) after its last deploy (${new Date(6).toLocaleString()}).`);
    assert.equal(core.staleDeployMessage('testcust', core.deployStaleness({ builtAt: 7 })), `testcust was built (${new Date(7).toLocaleString()}) but not deployed from this machine yet.`);
    assert.equal(core.deployStateKey('Contoso', 'TestCust'), 'contoso/testcust');
});
