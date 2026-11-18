// The first request after the server starts must not race the background metadata warm-up:
// node scripts/cold-start-test.js <exe> <config> [form] [runs]
// Starts the server several times and opens a form as its very first request (before the fix in MetadataHost.EnsureLoaded
// this failed every time with "An item with the same key has already been added").
const { spawn } = require('child_process');

const [exe, config, form = 'CustTable', runs = '3'] = process.argv.slice(2);
if (!exe || !config) {
    console.log('usage: node scripts/cold-start-test.js <exe> <config> [form] [runs]');
    process.exit(2);
}

function coldStart() {
    return new Promise(resolve => {
        const child = spawn(exe, ['--config', config], { windowsHide: true });
        const timer = setTimeout(() => { child.kill(); resolve('timeout'); }, 180_000);
        let buffer = '';
        const pending = new Map();
        let id = 1;
        child.stdout.on('data', d => {
            buffer += d;
            let i;
            while ((i = buffer.indexOf('\n')) >= 0) {
                const line = buffer.slice(0, i);
                buffer = buffer.slice(i + 1);
                if (line.trim()) {
                    const msg = JSON.parse(line);
                    pending.get(msg.id)?.(msg.result);
                }
            }
        });
        const call = (method, params) => new Promise(r => { const n = id++; pending.set(n, r); child.stdin.write(JSON.stringify({ jsonrpc: '2.0', id: n, method, params }) + '\n'); });
        (async () => {
            await call('initialize', {});
            const raw = await call('xpp/bridge', { action: 'get_form_tree', body: { formName: form } });
            const result = typeof raw === 'string' ? JSON.parse(raw) : raw;
            clearTimeout(timer);
            // Let the background warm-up finish (and log) before stopping the server.
            await new Promise(r => setTimeout(r, 4000));
            child.kill();
            resolve(result && result.success ? 'ok' : (result && result.message) || 'no result');
        })();
    });
}

(async () => {
    // The race also shows on the background warm-up, which only logs it; read what the runs add to the bridge log.
    const fs = require('fs');
    const logFile = require('path').join(require('os').tmpdir(), 'XppVsCodeBridge.log');
    const logSize = () => (fs.existsSync(logFile) ? fs.statSync(logFile).size : 0);
    let failures = 0;
    for (let i = 1; i <= Number(runs); i++) {
        const before = logSize();
        const outcome = await coldStart();
        const added = logSize() > before ? fs.readFileSync(logFile, 'utf8').slice(before) : '';
        const warmUp = /warm-up failed: An item with the same key/.test(added);
        const ok = outcome === 'ok' && !warmUp;
        if (!ok) failures++;
        console.log(`${ok ? 'PASS' : 'FAIL'} cold start ${i}: ${form} as the first request${outcome === 'ok' ? '' : ' — ' + outcome}${warmUp ? ' — the warm-up raced the first load' : ''}`);
    }
    console.log(failures ? `${failures} failed` : 'all passed');
    process.exit(failures ? 1 : 0);
})();
