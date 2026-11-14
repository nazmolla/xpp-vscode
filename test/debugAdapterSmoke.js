// Smoke test of the X++ debug adapter's detach safety, against a fake DebuggerProxy (no environment needed): a stopped
// AXHost must be resumed before the debugger detaches, detaching never ends AXHost, and a proxy left behind is stopped.
// Run: node scripts/run-smoke.js debugAdapterSmoke
const vscode = require('vscode');
const fs = require('fs');
const os = require('os');
const path = require('path');
const { execFileSync } = require('child_process');

// Speaks DAP on stdio and logs every request. 'pause' answers with a stopped event; 'disconnect' ends the process unless
// the mode is 'stubborn' (a proxy that never exits).
const fakeProxy = `
const fs = require('fs');
const [log, mode] = process.argv.slice(2);
let buffer = Buffer.alloc(0), seq = 1;
const send = m => { const j = JSON.stringify({ seq: seq++, ...m }); process.stdout.write('Content-Length: ' + Buffer.byteLength(j) + '\\r\\n\\r\\n' + j); };
process.stdin.on('data', chunk => {
    buffer = Buffer.concat([buffer, chunk]);
    for (;;) {
        const end = buffer.indexOf('\\r\\n\\r\\n'); if (end < 0) return;
        const len = Number(/Content-Length: (\\d+)/.exec(buffer.subarray(0, end).toString())[1]);
        if (buffer.length < end + 4 + len) return;
        const req = JSON.parse(buffer.subarray(end + 4, end + 4 + len).toString()); buffer = buffer.subarray(end + 4 + len);
        fs.appendFileSync(log, JSON.stringify({ command: req.command, seq: req.seq, arguments: req.arguments }) + '\\n');
        send({ type: 'response', request_seq: req.seq, success: true, command: req.command });
        if (req.command === 'pause') send({ type: 'event', event: 'stopped', body: { reason: 'pause', threadId: 7, allThreadsStopped: true } });
        if (req.command === 'disconnect' && mode !== 'stubborn') setTimeout(() => process.exit(0), 50);
    }
});
setInterval(() => { }, 1000);
`;

async function run() {
    const results = {};
    const out = process.env.XPP_SMOKE_OUT || path.join(os.tmpdir(), 'xpp-debug-adapter.json');
    const save = () => fs.writeFileSync(out, JSON.stringify(results, null, 2));
    const check = async (name, fn) => {
        try { results[name] = { ok: true, value: await fn() }; }
        catch (e) { results[name] = { ok: false, error: String(e && e.stack || e) }; }
        save();
    };
    const dir = fs.mkdtempSync(path.join(os.tmpdir(), 'xpp-fakeproxy-'));
    const script = path.join(dir, 'proxy.js');
    fs.writeFileSync(script, fakeProxy);
    const node = execFileSync('where', ['node'], { encoding: 'utf8' }).split(/\r?\n/)[0].trim();
    const ext = vscode.extensions.all.find(e => e.packageJSON.name === 'xpp-vscode');
    const debug = require(path.join(ext.extensionPath, 'out', 'debug.js'));
    const output = vscode.window.createOutputChannel('X++ debug adapter test');
    const sleep = ms => new Promise(r => setTimeout(r, ms));

    let n = 0;
    const session = (mode = 'normal') => {
        const log = path.join(dir, `requests-${++n}.log`);
        const adapter = new debug.ProxyDebugAdapter(node, [script, log, mode], output);
        const received = [];
        adapter.onDidSendMessage(m => received.push(m));
        let seq = 1;
        const send = (command, args = {}) => adapter.handleMessage({ type: 'request', seq: seq++, command, arguments: args });
        const requests = () => fs.existsSync(log) ? fs.readFileSync(log, 'utf8').trim().split('\n').filter(Boolean).map(l => JSON.parse(l)) : [];
        return { adapter, received, send, requests };
    };
    const commands = s => s.requests().map(r => r.command).join(',');

    await check('stopped, then disconnect: resumes first, never ends AXHost', async () => {
        const s = session();
        s.send('pause');
        await sleep(500);
        s.send('disconnect', { restart: false, terminateDebuggee: true });
        await sleep(800);
        const reqs = s.requests();
        const cont = reqs.find(r => r.command === 'continue');
        const disc = reqs.find(r => r.command === 'disconnect');
        if (commands(s) !== 'pause,continue,disconnect') throw new Error(commands(s));
        if (cont.arguments.threadId !== 7) throw new Error('continue thread ' + JSON.stringify(cont.arguments));
        if (disc.arguments.terminateDebuggee !== false) throw new Error('disconnect ' + JSON.stringify(disc.arguments));
        if (s.received.some(m => m.type === 'response' && m.command === 'continue')) throw new Error('own continue response reached VS Code');
        s.adapter.dispose();
        return commands(s);
    });

    await check('running, then disconnect: no extra continue', async () => {
        const s = session();
        s.send('threads');
        s.send('disconnect', { restart: false });
        await sleep(800);
        if (commands(s) !== 'threads,disconnect') throw new Error(commands(s));
        s.adapter.dispose();
        return commands(s);
    });

    await check('stopped, continued by you, then disconnect: no extra continue', async () => {
        const s = session();
        s.send('pause');
        await sleep(500);
        s.send('continue', { threadId: 7 });
        s.send('disconnect', {});
        await sleep(800);
        if (commands(s) !== 'pause,continue,disconnect') throw new Error(commands(s));
        s.adapter.dispose();
        return commands(s);
    });

    await check('window goes away while stopped: resume and detach', async () => {
        const s = session();
        s.send('pause');
        await sleep(500);
        s.adapter.dispose();
        await sleep(800);
        const reqs = s.requests();
        if (commands(s) !== 'pause,continue,disconnect') throw new Error(commands(s));
        if (reqs[2].arguments.terminateDebuggee !== false) throw new Error(JSON.stringify(reqs[2].arguments));
        return commands(s);
    });

    await check('deactivation stops a proxy that does not exit', async () => {
        const s = session('stubborn');
        s.send('pause');
        await sleep(500);
        const started = Date.now();
        await debug.releaseDebuggers();
        const took = Date.now() - started;
        await sleep(300);
        const pid = s.adapter.process.pid;
        let alive = true;
        try { process.kill(pid, 0); } catch { alive = false; }
        if (alive) throw new Error(`proxy ${pid} still running`);
        if (commands(s) !== 'pause,continue,disconnect') throw new Error(commands(s));
        return `${took} ms`;
    });

    await check('a proxy left by a closed window is stopped; one of an open window is not', async () => {
        const exe = path.join(dir, 'Microsoft.Dynamics.AX.DebuggerProxy.exe');
        fs.copyFileSync(node, exe);
        const { spawn } = require('child_process');
        const orphan = spawn(exe, [script, path.join(dir, 'orphan.log')], { stdio: 'ignore' });
        const kept = spawn(exe, [script, path.join(dir, 'kept.log')], { stdio: 'ignore' });
        const gone = spawn(node, ['-e', '0']);
        await new Promise(r => gone.on('exit', r));
        await sleep(500);
        const store = { 'xpp.debug.proxies': [{ pid: orphan.pid, host: gone.pid }, { pid: kept.pid, host: process.pid }] };
        const memento = { get: (k, d) => store[k] ?? d, update: async (k, v) => { store[k] = v; } };
        const d = Object.create(debug.XppDebugger.prototype);
        d.global = memento;
        d.log = output;
        await d.stopOrphanedProxies();
        await sleep(500);
        const alive = pid => { try { process.kill(pid, 0); return true; } catch { return false; } };
        const result = { orphanAlive: alive(orphan.pid), keptAlive: alive(kept.pid), remembered: store['xpp.debug.proxies'].map(p => p.pid) };
        kept.kill();
        if (result.orphanAlive || !result.keptAlive || result.remembered.join() !== String(kept.pid)) throw new Error(JSON.stringify(result));
        return result;
    });

    output.dispose();
    try { fs.rmSync(dir, { recursive: true, force: true }); } catch { }
    results.finished = true;
    save();
}

module.exports = { run };
