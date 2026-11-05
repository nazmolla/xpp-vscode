// Checks the server's data service (xpp/data) and table_data_shape against the local cross-reference database:
// node scripts/data-test.js <config.json> <sqlServer> <database> [table]
// The XRef database stands in for AxDB: same SQL Server, Windows authentication, real tables.
const { spawn } = require('child_process');
const path = require('path');

const [config, server, database, table = 'Names'] = process.argv.slice(2);
if (!config || !server || !database) {
    console.error('usage: node scripts/data-test.js <config.json> <sqlServer> <database> [table]');
    process.exit(2);
}
const exe = path.join(__dirname, '..', 'server', 'bin', 'XppVsCode.Server.exe');
const connection = { server, database, windowsAuthentication: true };

function session(args) {
    const child = spawn(exe, ['--config', config, ...args], { windowsHide: true });
    const pending = new Map();
    let buffer = '';
    let next = 1;
    child.stdout.on('data', d => {
        buffer += d.toString();
        let i;
        while ((i = buffer.indexOf('\n')) >= 0) {
            const line = buffer.slice(0, i).trim();
            buffer = buffer.slice(i + 1);
            if (!line) continue;
            const msg = JSON.parse(line);
            pending.get(msg.id)?.(msg);
            pending.delete(msg.id);
        }
    });
    const call = (method, params) => new Promise(resolve => {
        const id = next++;
        pending.set(id, resolve);
        child.stdin.write(JSON.stringify({ jsonrpc: '2.0', id, method, params }) + '\n');
    });
    const result = msg => msg.error ? { error: msg.error.message } : (typeof msg.result === 'string' ? JSON.parse(msg.result) : msg.result);
    return {
        init: () => call('initialize', { protocolVersion: '2025-06-18' }),
        data: async (action, body) => result(await call('xpp/data', { action, body })),
        bridge: async (action, body) => result(await call('xpp/bridge', { action, body })),
        close: () => child.kill(),
    };
}

const checks = [];
function check(name, ok, detail) {
    checks.push(ok);
    console.log(`${ok ? 'PASS' : 'FAIL'} ${name}${ok || !detail ? '' : ': ' + JSON.stringify(detail).slice(0, 400)}`);
}

(async () => {
    const timer = setTimeout(() => { console.log('TIMEOUT'); process.exit(1); }, 180000);

    const plain = session([]);
    await plain.init();
    const refused = await plain.data('test', { connection });
    check('xpp/data is off without --allow-data', /Method not found/.test(refused.error ?? ''), refused);
    plain.close();

    const s = session(['--allow-data']);
    await s.init();
    const test = await s.data('test', { connection });
    check('test connects with Windows authentication', test.success && test.database?.toLowerCase() === database.toLowerCase(), test);

    const browse = await s.data('browse', { connection, table, maxRows: 5 });
    const set = browse.resultSets?.[0];
    check('browse returns at most maxRows rows', browse.success && set && set.rows.length <= 5, browse);
    check('browse flags truncation', browse.success && (set.rows.length < 5 || set.truncated === true), set && { rows: set.rows.length, truncated: set.truncated });

    const missingColumn = await s.data('browse', { connection, table, columns: ['NoSuchColumn]; DROP TABLE x --'] });
    check('browse refuses unknown columns', missingColumn.success === false && /no column/i.test(missingColumn.message), missingColumn);

    const missingTable = await s.data('browse', { connection, table: 'NoSuchTable]; DROP TABLE x --' });
    check('browse refuses unknown tables', missingTable.success === false, missingTable);

    const filtered = await s.data('browse', { connection, table, filters: [{ column: set?.columns[0]?.name ?? 'Id', operator: 'contains', value: "'; DROP TABLE x --" }], maxRows: 5 });
    check('filter values are parameters', filtered.success === true && filtered.resultSets[0].rows.length === 0, filtered);

    const query = await s.data('query', { connection, sql: `SELECT TOP 3 * FROM [${table}]`, maxRows: 2 });
    check('read-only query runs and is cut at maxRows', query.success && query.resultSets[0].rows.length === 2 && query.resultSets[0].truncated && query.committed === false, query);

    const write = await s.data('query', { connection, sql: `DELETE FROM [${table}] WHERE 1 = 0` });
    check('read-only mode refuses DELETE', write.success === false && /SELECT|DELETE/.test(write.message), write);

    const sneaky = await s.data('query', { connection, sql: 'SELECT 1; DROP TABLE x' });
    check('read-only mode refuses a second statement', sneaky.success === false, sneaky);

    const badSql = await s.data('query', { connection, sql: 'SELECT * FROM NoSuchTable' });
    check('SQL errors come back as messages', badSql.success === false && /Invalid object name/i.test(badSql.message), badSql);

    const noConnection = await s.data('test', {});
    check('a request without a connection is refused', noConnection.success === false, noConnection);

    const plan = await s.data('plan', { connection, sql: `SELECT * FROM [${table}] WHERE Path LIKE '/Classes/Cust%'` });
    check('plan summarizes without running the query', plan.success && plan.operators > 0 && Array.isArray(plan.notes) && plan.notes.length > 0, plan.success ? plan.notes : plan);
    const planWrite = await s.data('plan', { connection, sql: `DELETE FROM [${table}]` });
    check('plan refuses anything but SELECT', planWrite.success === false, planWrite);

    // The live insight queries: the DMV one runs everywhere; the others need AxDB tables (verified on a UDE).
    const { Insights } = require(path.join(__dirname, '..', 'out', 'database', 'insights.js'));
    const dmv = await s.data('query', { connection, sql: Insights.find(i => /Missing indexes/.test(i.label)).sql, maxRows: 5 });
    check('missing-index insight runs read-only', dmv.success === true, dmv);
    for (const insight of Insights) {
        const refusal = await s.data('plan', { connection, sql: insight.sql });
        check(`insight passes the read-only guard: ${insight.label}`, refusal.success === true || !/read-only|not allowed|Only SELECT/.test(refusal.message), refusal);
    }

    const shape = await s.bridge('table_data_shape', { table: 'SalesTable' });
    const status = shape.fields?.find(f => f.name === 'SalesStatus');
    check('table_data_shape describes SalesTable', shape.success && shape.perCompany === true && status?.enumType === 'SalesStatus', shape.success ? status : shape);
    check('table_data_shape maps fixed enum values', shape.success && Object.keys(shape.enums?.SalesStatus?.values ?? {}).length > 0, shape.enums?.SalesStatus);

    const unknown = await s.bridge('table_data_shape', { table: 'NoSuchTable' });
    check('table_data_shape reports unknown tables', unknown.success === false, unknown);

    s.close();
    clearTimeout(timer);
    const failed = checks.filter(ok => !ok).length;
    console.log(failed ? `${failed} of ${checks.length} checks FAILED` : `All ${checks.length} checks passed.`);
    process.exit(failed ? 1 : 0);
})().catch(err => { console.error(err); process.exit(1); });
