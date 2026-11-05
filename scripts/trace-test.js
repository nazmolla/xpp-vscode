// Checks the trace parser against a real .etl: node scripts/trace-test.js <exe> <config> <file.etl>
// Runs xpp/trace (the extension's JSON) and the xpp_analyze_trace tool, prints timing and PASS/FAIL per check.
const { spawn } = require('child_process');
const [exe, config, etl] = process.argv.slice(2);
const child = spawn(exe, ['--config', config], { windowsHide: true });
const pending = new Map();
let buffer = '';
child.stdout.on('data', d => {
    buffer += d.toString();
    const lines = buffer.split('\n');
    buffer = lines.pop();
    for (const line of lines.filter(l => l.trim())) {
        const msg = JSON.parse(line);
        pending.get(msg.id)?.(msg);
    }
});
let id = 0;
const call = (method, params) => new Promise(resolve => {
    pending.set(++id, resolve);
    child.stdin.write(JSON.stringify({ jsonrpc: '2.0', id, method, params }) + '\n');
});
let failed = 0;
const check = (name, ok, detail) => { if (!ok) failed++; console.log(`${ok ? 'PASS' : 'FAIL'} ${name}${detail ? ': ' + detail : ''}`); };
setTimeout(() => { console.log('TIMEOUT'); child.kill(); process.exit(1); }, 600000);

(async () => {
    await call('initialize', { protocolVersion: '2025-06-18' });
    let t0 = Date.now();
    const raw = (await call('xpp/trace', { action: 'analyze', body: { path: etl } })).result;
    const report = typeof raw === 'string' ? JSON.parse(raw) : raw;
    check('xpp/trace succeeds', report.success, report.message ?? `${Date.now() - t0} ms, ${report.events} events, ${report.xppCalls} X++ calls`);
    if (report.success) {
        check('requests found', report.requests.length > 0, `${report.requests.length} requests`);
        check('every request has a tree', report.requests.every(r => r.tree && r.tree.m === r.method));
        check('inclusive >= exclusive', report.methods.every(m => m.incl + 0.2 >= m.excl || m.n > 1));
        check('SQL attributed to X++ methods', report.sql.length > 0 && report.sql.some(s => s.callers.some(c => !c.m.startsWith('('))), `${report.sqlTotal.n} statements, ${report.sqlTotal.ms} ms`);
        check('JSON stays bounded', JSON.stringify(report).length < 20_000_000, `${JSON.stringify(report).length} chars`);
        const slowest = [...report.requests].sort((a, b) => b.ms - a.ms).slice(0, 3);
        console.log('slowest: ' + slowest.map(r => `${r.method} ${r.ms} ms`).join(' | '));
    }
    t0 = Date.now();
    const tool = (await call('tools/call', { name: 'xpp_analyze_trace', arguments: { path: etl } })).result;
    const text = tool?.content?.[0]?.text ?? '';
    check('xpp_analyze_trace returns text', !tool.isError && /Slowest requests/.test(text), `${Date.now() - t0} ms, ${text.length} chars`);
    if (process.env.SHOW_TRACE_TEXT) console.log(text);
    const bad = (await call('tools/call', { name: 'xpp_analyze_trace', arguments: { path: 'C:\\Windows\\win.ini' } })).result;
    check('non-.etl files are refused', bad.isError === true);
    console.log(failed ? `${failed} failure(s)` : 'All trace checks passed.');
    child.stdin.end();
    process.exit(failed ? 1 : 0);
})();
