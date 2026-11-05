// Runs every extension-host smoke test and prints one line per check (no VS Code logs).
// Usage: node scripts/run-smoke.js [name ...]   (XPP_TEST_WORKSPACE optional)
const { spawnSync } = require('child_process');
const fs = require('fs');
const os = require('os');
const path = require('path');

const all = ['workflowSmoke', 'databaseSmoke', 'securitySmoke', 'productivitySmoke', 'projectSmoke', 'goToSmoke', 'codeIntelSmoke', 'historySmoke', 'dragDropSmoke'];
const names = process.argv.slice(2).length ? process.argv.slice(2) : all;
let failed = 0;
for (const name of names) {
    const out = path.join(os.tmpdir(), `xpp-run-${name}.json`);
    fs.rmSync(out, { force: true });
    const run = spawnSync(process.execPath, [path.join(__dirname, '..', 'test', 'runInDownloadedCode.js'), path.join('test', `${name}.js`)],
        { cwd: path.join(__dirname, '..'), env: { ...process.env, XPP_SMOKE_OUT: out }, encoding: 'utf8', timeout: 20 * 60 * 1000, maxBuffer: 256 * 1024 * 1024 });
    const results = fs.existsSync(out) ? JSON.parse(fs.readFileSync(out, 'utf8')) : {};
    const checks = Object.entries(results).filter(([k]) => k !== 'finished' && k !== 'fatal');
    const broken = run.status !== 0 || results.fatal || checks.length === 0;
    console.log(`== ${name} (exit ${run.status})${results.fatal ? ' FATAL: ' + String(results.fatal).split('\n')[0] : ''}${checks.length ? '' : ' no results'}`);
    for (const [check, r] of checks) {
        if (!r.ok) { failed++; }
        console.log(`  ${r.ok ? 'PASS' : 'FAIL'} ${check}${r.ok ? '' : ': ' + String(r.error).split('\n')[0]}`);
    }
    if (broken) { failed++; }
}
console.log(failed ? `${failed} failure(s)` : 'All smoke checks passed.');
process.exit(failed ? 1 : 0);
