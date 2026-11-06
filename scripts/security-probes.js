// Security probes (DAST) against the built server and the active D365FO configuration: node scripts/security-probes.js [--model testcust]
// Writes only throw-away objects (XvsDast*) in the given custom model and deletes them. Probes against standard objects carry
// a payload the handler rejects before saving, so even a missing ownership check cannot change standard metadata.
const { spawn, execSync } = require('child_process');
const fs = require('fs');
const path = require('path');

const argv = process.argv.slice(2);
const modelArg = argv.indexOf('--model');
const model = modelArg >= 0 ? argv[modelArg + 1] : 'testcust';
const reg = execSync('reg query "HKCU\\Software\\Microsoft\\Dynamics\\AX7\\Development\\Configurations" /v CurrentMetadataConfig', { encoding: 'utf8' });
const config = /CurrentMetadataConfig\s+REG_SZ\s+(.+)/.exec(reg)?.[1]?.trim();
if (!config) { console.log('No active D365FO configuration.'); process.exit(2); }

const child = spawn(path.resolve(__dirname, '..', 'server', 'bin', 'XppVsCode.Server.exe'), ['--config', config], { windowsHide: true });
const pending = new Map();
let out = '', nextId = 1, alive = true, invalidJson = 0;
child.stdout.on('data', d => {
    out += d.toString();
    const lines = out.split('\n');
    out = lines.pop();
    for (const line of lines) {
        if (!line.trim()) continue;
        let msg;
        try { msg = JSON.parse(line); } catch {
            console.log('   !! invalid JSON-RPC output: ' + JSON.stringify(line.slice(0, 160)));
            invalidJson++;
            try { msg = JSON.parse(line.replace(/[\u0000-\u001f]/g, ' ')); } catch { continue; }
        }
        const p = pending.get(msg.id);
        if (p) { pending.delete(msg.id); p(msg); }
    }
});
child.on('exit', () => { alive = false; });

function raw(text, id, ms = 120000) {
    return new Promise(resolve => {
        if (id === undefined) { child.stdin.write(text + '\n'); setTimeout(() => resolve({}), 500); return; }
        const t = setTimeout(() => { pending.delete(id); resolve({ timeout: true }); }, ms);
        pending.set(id, m => { clearTimeout(t); resolve(m); });
        child.stdin.write(text + '\n');
    });
}
const rpc = (method, params, ms) => { const id = nextId++; return raw(JSON.stringify({ jsonrpc: '2.0', id, method, params }), id, ms); };
const parse = r => { try { return typeof r.result === 'string' ? JSON.parse(r.result) : r.result; } catch { return r.result; } };
const bridge = async (action, body) => parse(await rpc('xpp/bridge', { action, body }));
const tool = async (name, args, ms) => {
    const r = await rpc('tools/call', { name, arguments: args }, ms);
    return { text: r.result?.content?.[0]?.text ?? JSON.stringify(r.error ?? r), timeout: r.timeout };
};
const results = [];
const verdict = (id, title, pass, evidence) => {
    results.push({ id, pass });
    console.log(`${pass ? 'PASS' : 'FAIL'} ${id} ${title}\n     ${String(evidence).replace(/\s+/g, ' ').slice(0, 300)}`);
};
const msgOf = r => (r && (r.message ?? r.Message ?? r.text)) || String(JSON.stringify(r)).slice(0, 300);
const ok = r => !!(r && (r.success ?? r.Success));
const guarded = r => !ok(r) && /can't change|not one of your custom models/i.test(msgOf(r));
const markup = '<?xml version="1.0"?>';

(async () => {
    await rpc('initialize', { protocolVersion: '2025-06-18' });
    const env = await bridge('get_environment', {});
    const custom = JSON.stringify(env).match(/"CustomMetadataFolder"\s*:\s*"([^"]+)"/i)?.[1]?.replace(/\\\\/g, '\\');

    // Object names: traversal, markup, control characters, size.
    for (const [id, name] of [
        ['D01', '..\\..\\..\\XvsPwnTrav'], ['D02', '../../XvsPwnTrav2'], ['D03', 'C:\\Windows\\Temp\\XvsPwnAbs'],
        ['D04', 'Xvs<script>alert(1)</script>'], ['D05', 'Xvs"]]><Evil/>'], ['D06', 'Xvs\u0000Nul'], ['D07', 'X'.repeat(5000)], ['D08', ''],
    ]) {
        const r = await bridge('create_object', { objectType: 'AxClass', objectName: name, modelName: model });
        verdict(id, `create_object rejects hostile name ${JSON.stringify(name).slice(0, 40)}`, !ok(r), msgOf(r));
        if (ok(r)) await bridge('delete_object', { objectType: 'AxClass', objectName: name });
    }
    const stray = [];
    if (custom && fs.existsSync(custom)) {
        for (let dir = custom, i = 0; i < 4; i++, dir = path.dirname(dir))
            for (const f of fs.readdirSync(dir)) if (/XvsPwn/i.test(f)) stray.push(path.join(dir, f));
    }
    if (fs.existsSync('C:\\Windows\\Temp\\XvsPwnAbs.xml')) stray.push('C:\\Windows\\Temp\\XvsPwnAbs.xml');
    verdict('D09', 'no file written outside the metadata tree', stray.length === 0, stray.join(', ') || 'none found');

    // Ownership: standard models and objects are never written.
    let r = await bridge('create_object', { objectType: 'AxClass', objectName: 'XvsDastStd', modelName: 'Foundation', declaration: markup });
    verdict('D10', 'create_object refuses a standard model', guarded(r), msgOf(r));
    r = await bridge('update_object', { objectType: 'AxClass', objectName: 'Global', declaration: markup });
    verdict('D11', 'update_object refuses a standard object', guarded(r), msgOf(r));
    let t = await tool('xpp_update_object', { objectType: 'AxClass', objectName: 'Global', declaration: markup });
    verdict('D12', 'MCP xpp_update_object refuses a standard object', /can't change/i.test(t.text), t.text);
    // The node and the property don't exist, so nothing could be saved even without the check.
    for (const [id, action, extra] of [
        ['D13', 'delete_element_node', {}], ['D14', 'rename_element_node', { newName: 'XvsPwn' }],
        ['D15', 'set_element_property', { property: 'XvsNoSuchProperty', value: 'x' }], ['D16', 'drop_on_node', { sourceType: 'AxNoSuchType', sourceName: 'X' }],
    ]) {
        r = await bridge(action, { objectType: 'AxTable', objectName: 'CustGroup', path: ['Fields', 'XvsNoSuchField'], ...extra });
        verdict(id, `${action} refuses a standard table`, guarded(r), msgOf(r));
    }

    // Method source: CDATA break-out stays literal.
    await bridge('create_object', { objectType: 'AxClass', objectName: 'XvsDastCls', modelName: model });
    r = await bridge('update_object', { objectType: 'AxClass', objectName: 'XvsDastCls',
        methods: ['public void m()\n{\n    str s = "]]></Source></Method><Method><Name>pwn</Name>";\n}'] });
    const text = JSON.stringify(await bridge('read_object', { objectType: 'AxClass', objectName: 'XvsDastCls' }));
    verdict('D17', 'CDATA break-out in method source is stored literally', text.includes(']]></Source>') && !/"name"\s*:\s*"pwn"/i.test(text), msgOf(r));

    // Designer node paths.
    for (const [id, p] of [['D18', ['..', '..', 'x']], ['D19', ['Methods', '..\\..\\x']], ['D20', Array(200).fill('Fields')]]) {
        r = await bridge('delete_element_node', { objectType: 'AxClass', objectName: 'XvsDastCls', path: p });
        verdict(id, `hostile designer path ${JSON.stringify(p).slice(0, 40)} is refused`, !ok(r) && alive, msgOf(r));
    }
    r = await bridge('rename_element_node', { objectType: 'AxClass', objectName: 'XvsDastCls', path: ['Methods', 'm'], newName: 'a"; DROP' });
    verdict('D21', 'rename to an invalid identifier is refused', !ok(r), msgOf(r));

    // Labels.
    r = await bridge('label_file_entries', { labelFile: '..\\..\\..\\..\\..\\Windows\\win', search: '' });
    verdict('D22', 'label file ID with traversal reads nothing', !ok(r) || !(r.entries ?? []).length, msgOf(r));
    r = await bridge('label_file_entries', { labelFile: 'SYS_..\\..\\x', search: '' });
    verdict('D23', 'label language with traversal reads nothing', !ok(r) || !(r.entries ?? []).length, msgOf(r));
    r = await bridge('save_label', { labelFile: 'SYS', labelId: 'Pwn\r\nX', texts: { 'en-US': 'x' } });
    verdict('D24', 'save_label refuses a CRLF label ID / standard label file', !ok(r), msgOf(r));

    // Copilot checkpoints and version comparison: ids and folders from the caller are not paths to wander through.
    r = await bridge('revert_checkpoint', { id: '..\\..\\..\\Windows' });
    verdict('D35', 'revert_checkpoint refuses a traversal id (or is unavailable in this process)', !ok(r), msgOf(r));
    r = await bridge('wrapper_drift', { modelName: model, previousFolder: 'C:\\Windows' });
    verdict('D36', 'wrapper_drift reads only a PackagesLocalDirectory', !ok(r), msgOf(r));
    r = await bridge('extension_collisions', { modelName: 'ApplicationSuite' });
    verdict('D37', 'extension_collisions refuses a standard model', !ok(r), msgOf(r));

    // Agent tools: SQL injection, regex time-outs, URL allow-list.
    t = await tool('xpp_find_references', { objectType: 'AxTable', objectName: "CustTable' OR 1=1 --" });
    verdict('D25', 'xpp_find_references treats SQL as data', !/syntax|SqlException|incorrect/i.test(t.text), t.text);
    t = await tool('xpp_find_object', { objectName: "x'; DROP TABLE Names; --", objectType: 'AxTable' });
    verdict('D26', 'xpp_find_object treats SQL as data', !/syntax|SqlException/i.test(t.text), t.text);
    const t0 = Date.now();
    t = await tool('xpp_search_code', { pattern: '^(\\w+\\s?)+#$', regex: true, objectTypes: ['AxClass'], model: 'ApplicationFoundation', maxResults: 5 }, 300000);
    verdict('D27', 'catastrophic regex is bounded', !t.timeout && alive, `${Date.now() - t0} ms; ${t.text}`);
    for (const [id, url] of [
        ['D28', 'https://learn.microsoft.com.invalid/en-us/x'], ['D29', 'https://learn.microsoft.com@127.0.0.1:9/x'],
        ['D30', 'file:///C:/Windows/win.ini'], ['D31', 'http://169.254.169.254/latest/meta-data/'], ['D32', 'https://learn.microsoft.com:8443/x'],
    ]) {
        t = await tool('xpp_search_docs', { url }, 60000);
        verdict(id, `xpp_search_docs refuses ${url}`, /Only Microsoft Learn URLs are allowed/i.test(t.text), t.text);
    }

    // Protocol robustness.
    await raw('{ this is not json', undefined);
    await raw(JSON.stringify({ jsonrpc: '2.0', id: 9001, method: 'no/such/method' }), 9001, 10000);
    await raw('['.repeat(20000) + ']'.repeat(20000), undefined);
    await raw(JSON.stringify({ jsonrpc: '2.0', id: 9002, method: 'xpp/bridge', params: { action: 'read_object', body: { objectType: 'AxClass', objectName: 'A'.repeat(5_000_000) } } }), 9002, 60000);
    await raw(JSON.stringify({ jsonrpc: '2.0', id: 9003, method: 'xpp/bridge', params: { action: 'no_such_action', body: {} } }), 9003, 10000);
    const ping = await rpc('ping', {}, 15000);
    verdict('D33', 'server survives malformed JSON, deep nesting, a 5 MB payload, unknown methods', alive && !ping.timeout, JSON.stringify(ping).slice(0, 120));
    verdict('D34', 'every response is valid JSON', invalidJson === 0, `${invalidJson} invalid line(s)`);

    await bridge('delete_object', { objectType: 'AxClass', objectName: 'XvsDastCls' });
    const failed = results.filter(x => !x.pass).map(x => x.id);
    console.log(`\nSUMMARY ${results.length - failed.length}/${results.length} passed${failed.length ? '; failed: ' + failed.join(', ') : ''}`);
    child.stdin.end();
    setTimeout(() => process.exit(failed.length ? 1 : 0), 1000);
})().catch(e => { console.log('HARNESS ERROR ' + e.stack); child.kill(); process.exit(1); });
setTimeout(() => { console.log('GLOBAL TIMEOUT'); child.kill(); process.exit(1); }, 900000);
