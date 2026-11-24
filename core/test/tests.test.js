const assert = require('assert/strict');
const { test } = require('node:test');
const core = require('../dist');
const { fixture, replay } = require('./helpers');

const Url = 'https://contoso.crm.dynamics.com';

/** A metadata reader that knows a few classes, as the server's read_object answers. */
function reader(classes) {
    return {
        read: [],
        async bridge(action, body) {
            this.read.push(body.objectName);
            const cls = classes[body.objectName];
            return cls ? { success: true, objectName: body.objectName, ...cls } : { success: false };
        },
    };
}

const Classes = {
    XvsCoreLiveTest: {
        declaration: 'class XvsCoreLiveTest extends SysTestCase\n{\n}',
        methods: [
            { name: 'testAddition', source: 'public void testAddition()\n{\n}' },
            { name: 'testFails', source: '[SysTestMethod]\npublic void testFails()\n{\n}' },
            { name: 'testHidden', source: 'private void testHidden()\n{\n}' },
            { name: 'setUp', source: 'public void setUp()\n{\n}' },
        ],
    },
    CustTable: { declaration: 'public class CustTable extends common', methods: [{ name: 'testValue', source: 'public void testValue() {}' }] },
};

test('discovery finds public test methods of test classes', async () => {
    const lines = [];
    const tests = await core.discoverTests(reader(Classes), { classes: ['XvsCoreLiveTest'] }, { log: { line: l => lines.push(l) } });
    assert.deepEqual(tests, [{ className: 'XvsCoreLiveTest', method: 'testAddition' }, { className: 'XvsCoreLiveTest', method: 'testFails' }]);
    assert.deepEqual(lines, ['Discovered 2 test(s) in 1 class(es).']);
});

test('discovery falls back to the project classes, and needs classes or a project', async () => {
    const r = reader(Classes);
    const tests = await core.discoverTests(r, {}, { projectClasses: async () => ['CustTable', 'Missing', 'XvsCoreLiveTest'] });
    assert.equal(tests.length, 2);
    assert.deepEqual(r.read, ['CustTable', 'Missing', 'XvsCoreLiveTest']);
    await assert.rejects(core.discoverTests(r, {}, { projectClasses: async () => undefined }), { message: 'Pass test classes, or open a D365FO project whose test classes should run.' });
    await assert.rejects(core.discoverTests(r, { classes: ['Missing'] }), { message: 'Class Missing not found.' });
});

test('runs a suite on the cloud runtime and summarizes the .trx', async () => {
    const http = replay(fixture('runtests.json'));
    const lines = [];
    const tokens = [];
    let result;
    try {
        result = await core.runTests({ classes: ['XvsCoreLiveTest'] }, {
            reader: reader(Classes),
            dataverse: async () => {
                tokens.push('asked');
                return new core.Dataverse(Url, `${Url}/api/data/v9.2`, 't');
            },
            model: 'testcust',
            runtimeRoot: 'C:\\Runtime',
            log: { line: l => lines.push(l) },
            pollIntervalMs: 1,
        });
    } finally {
        http.restore();
    }
    assert.equal(http.remaining(), 0);
    assert.deepEqual(tokens, ['asked']);
    assert.deepEqual(JSON.parse(http.requests[0].body), { msprov_unittestsuitename: 'testcust_0_done.tsx' });

    const suite = http.requests[1].body.toString('utf8');
    assert.match(suite, /<AssemblyPath>C:\\Runtime\\testcust\\bin\\Dynamics\.AX\.testcust\.dll<\/AssemblyPath>/);
    assert.match(suite, /<FullyQualifiedName>Dynamics\.AX\.Application\.XvsCoreLiveTest\.testFails<\/FullyQualifiedName>/);
    assert.equal(http.requests[1].headers['x-ms-file-name'], 'testcust_0_done.tsx');

    assert.deepEqual(JSON.parse(http.requests[2].body), {
        unittestsuiteentitycollection: [{ '@odata.type': 'Microsoft.Dynamics.CRM.msprov_unittestsuite', msprov_unittestsuiteid: '3e2d1c0b-9a8f-4e7d-a6c5-b4a392817f6e' }],
        verbose: false,
        waitfordebugger: false,
    });

    assert.equal(lines[0], 'Discovered 2 test(s) in 1 class(es).');
    assert.match(lines[1], /^Suite: .*testcust_0_done\.tsx$/);
    assert.deepEqual(lines.slice(2), ['Uploading test suite to Dataverse...', 'Starting execution on the cloud runtime...',
        '  status: Active / Running', '  status: Inactive / Completed', 'Downloading results...']);

    assert.equal(result.summary, [
        `TESTS FAILED: 1 passed, 1 failed, 0 skipped (of 2 requested) on ${Url}.`,
        '  PASSED XvsCoreLiveTest.testAddition (00:00:00.0310000)',
        '  FAILED XvsCoreLiveTest.testFails (00:00:00.0120000)',
        '    assertEquals failed: 3 <> 2',
        '    Infolog: Error: Value is wrong',
    ].join('\n'));
});

test('no tests: no environment call', async () => {
    const result = await core.runTests({ classes: ['CustTable'] }, {
        reader: reader(Classes),
        dataverse: async () => { throw new Error('must not sign in'); },
        model: 'testcust',
        runtimeRoot: 'C:\\Runtime',
    });
    assert.deepEqual(result, { summary: core.NoTestsFound, results: [] });
});

test('a cancelled run stops polling', async () => {
    const http = replay(fixture('runtests.json').slice(0, 3));
    const controller = new AbortController();
    controller.abort();
    try {
        await assert.rejects(core.runTests({ classes: ['XvsCoreLiveTest'] }, {
            reader: reader(Classes),
            dataverse: async () => new core.Dataverse(Url, `${Url}/api/data/v9.2`, 't'),
            model: 'testcust',
            runtimeRoot: 'C:\\Runtime',
            signal: controller.signal,
        }), { message: 'Test run cancelled.' });
    } finally {
        http.restore();
    }
});

test('summary of an empty result', () => {
    assert.equal(core.summarizeTests([], 3, Url), `TESTS FAILED: 0 passed, 0 failed, 0 skipped (of 3 requested) on ${Url}.\n`
        + '  No results were returned. Check that the package is deployed and the test classes exist on the environment.');
});
