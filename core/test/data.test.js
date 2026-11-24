const assert = require('assert/strict');
const { test } = require('node:test');
const core = require('../dist');
const { fixture, replay } = require('./helpers');

const Url = 'https://contoso.crm.dynamics.com';
const dataverse = () => new core.Dataverse(Url, `${Url}/api/data/v9.2`, 't');

test('JIT credentials are requested as Reader unless Writer is asked for', async () => {
    const http = replay(fixture('sqljit.json'));
    let credentials;
    try {
        credentials = await core.requestSqlCredentials(dataverse(), { reason: '  Check CustGroup  ', addresses: ' 203.0.113.10 ' });
    } finally {
        http.restore();
    }
    assert.deepEqual(JSON.parse(http.requests[0].body), { sqljitclientipaddress: '203.0.113.10', sqljitreason: 'Check CustGroup', sqljitrole: 'Reader' });
    assert.equal(credentials.server, 'spartan-srv-nam-d365opsprod-1a2b3c4d5e6f.database.windows.net');
    assert.equal(credentials.role, 'Reader');
    assert.equal(credentials.user, 'JIT-a1b2c3d4-e5f6-4a7b-8c9d-0e1f2a3b4c5d');
    assert.equal(credentials.expiresAt, Date.parse('2026-11-25T02:43:53Z'));
    assert.equal(core.isExpired(credentials, Date.parse('2026-11-25T02:43:52Z')), false);
    assert.equal(core.isExpired(credentials, Date.parse('2026-11-25T02:43:53Z')), true);
});

test('a response without credentials explains the needed role', () => {
    assert.throws(() => core.toCredentials({ OperationHistoryId: 'op-1' }, 'Reader', '203.0.113.10'), {
        message: 'The environment did not return database credentials. Database access needs the System administrator role in a unified developer or sandbox environment. Operation: op-1.',
    });
});

test('address ranges', () => {
    assert.equal(core.validateAddressRange('203.0.113.1-203.0.113.50'), undefined);
    assert.equal(core.validateAddressRange('203.0.113.50-203.0.113.1'), 'The first address of the range must not be above the last.');
    assert.match(core.validateAddressRange('example.com'), /^Enter an IPv4 address/);
});

/** The data service's answers, recorded from the server's xpp/data responses. */
function transport(answers) {
    const calls = [];
    return {
        calls,
        async data(action, body, timeoutMs) {
            calls.push({ action, body, timeoutMs });
            const answer = answers[action];
            return typeof answer === 'function' ? answer(body) : answer;
        },
    };
}

const Cloud = {
    environment: 'contoso', server: 'sql.example.net', database: 'db', cloud: true,
    credentials: { server: 'sql.example.net', database: 'db', user: 'JIT-1', password: 'p', role: 'Reader', expiration: '', allowedAddresses: '', requestedAt: 0 },
};

const CustGroup = {
    success: true, committed: false, rowsAffected: -1, elapsedMs: 42, messages: [],
    resultSets: [{ columns: [{ name: 'CUSTGROUP', type: 'nvarchar' }, { name: 'NAME', type: 'nvarchar' }], rows: [['10', 'Wholesale\tcustomers'], ['20', null]], truncated: true }],
};

test('the agent query is read-only, row-limited and tab-separated', async () => {
    const t = transport({ query: CustGroup });
    const client = new core.DataClient(t);
    const text = await core.readOnlyQuery(client, async () => Cloud, { sql: 'SELECT TOP 5 CUSTGROUP, NAME FROM CUSTGROUP', maxRows: 5000 }, { noAccessMessage: 'none' });
    assert.equal(text, 'QUERY OK on db (42 ms, read-only).\n2+ row(s) (limit reached)\nCUSTGROUP\tNAME\n10\tWholesale customers\n20\tNULL');
    const call = t.calls[0];
    assert.equal(call.action, 'query');
    assert.equal(call.body.maxRows, 500);
    assert.equal('allowWrite' in call.body, false, 'never commits');
    assert.deepEqual(call.body.connection, { server: 'sql.example.net', database: 'db', user: 'JIT-1', password: 'p' });
    assert.equal(call.timeoutMs, 90_000);
});

test('the agent query without SQL or access', async () => {
    const client = new core.DataClient(transport({}));
    assert.equal(await core.readOnlyQuery(client, async () => Cloud, { sql: ' ' }, { noAccessMessage: 'none' }), 'QUERY FAILED: pass a SELECT statement in "sql".');
    assert.equal(await core.readOnlyQuery(client, async () => { throw new Error('no env'); }, { sql: 'SELECT 1' }, { noAccessMessage: 'NO ACCESS' }), 'NO ACCESS');
});

test('a refused sign-in drops expired cloud credentials', async () => {
    const dropped = [];
    const client = new core.DataClient(transport({ query: { success: false, sqlError: 18456, message: 'Login failed for user \'JIT-1\'.' } }), async target => { dropped.push(target.environment); });
    const text = await core.readOnlyQuery(client, async () => Cloud, { sql: 'SELECT 1' }, { noAccessMessage: 'none' });
    assert.equal(text, 'QUERY FAILED: Login failed for user \'JIT-1\'. The credentials expired, were replaced, or this address is not allowed. Request new database credentials.');
    assert.deepEqual(dropped, ['contoso']);
});

test('new credentials are given time to activate', async () => {
    const client = new core.DataClient(transport({ test: { success: false, sqlError: 40615, message: 'firewall' } }), async () => assert.fail('kept'));
    await assert.rejects(client.test({ ...Cloud, credentials: { ...Cloud.credentials, requestedAt: Date.now() } }), /not active yet/);
});

test('cancelling a query cancels it on the server', async () => {
    const controller = new AbortController();
    let release;
    const t = transport({ query: () => new Promise(r => { release = r; }), cancel: { success: true } });
    const running = new core.DataClient(t).query({ environment: 'local', server: 'localhost', database: 'AxDB', cloud: false }, 'SELECT 1', {}, controller.signal);
    controller.abort();
    release(CustGroup);
    await running;
    assert.deepEqual(t.calls.map(c => c.action), ['query', 'cancel']);
    assert.equal(t.calls[1].body.queryId, t.calls[0].body.queryId);
    assert.equal(t.calls[0].body.connection.windowsAuthentication, true);
});

test('insights are read-only queries', () => {
    assert.ok(core.Insights.length >= 4);
    assert.ok(core.Insights.every(i => /^SELECT\b/.test(i.sql)));
});
