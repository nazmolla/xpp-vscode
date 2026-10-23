// Smoke test of the security and license UI in a real extension host (standard objects only).
// Run: node test/runInDownloadedCode.js test/securitySmoke.js; results go to XPP_SMOKE_OUT (default %TEMP%\xpp-security.json).
const vscode = require('vscode');
const fs = require('fs');
const os = require('os');
const path = require('path');

const sleep = ms => new Promise(r => setTimeout(r, ms));

async function run() {
    const results = {};
    const out = process.env.XPP_SMOKE_OUT || path.join(os.tmpdir(), 'xpp-security.json');
    const save = () => fs.writeFileSync(out, JSON.stringify(results, null, 2));
    const check = async (name, fn) => {
        const t0 = Date.now();
        try { results[name] = { ok: true, value: await fn(), ms: Date.now() - t0 }; }
        catch (err) { results[name] = { ok: false, error: String(err && err.stack || err), ms: Date.now() - t0 }; }
        save();
    };
    const tabs = () => vscode.window.tabGroups.all.flatMap(g => g.tabs.map(t => t.label));
    // Commands report errors in a message instead of throwing: a new panel tab is the proof that one worked.
    const panelAfter = async (cmd, ...args) => {
        const before = new Set(tabs());
        const run = vscode.commands.executeCommand(cmd, ...args);
        for (let i = 0; i < 400; i++) {
            const added = tabs().filter(t => !before.has(t));
            if (added.length) { await run; return added; }
            await sleep(500);
        }
        await run;
        return 'no panel opened';
    };

    const ext = vscode.extensions.getExtension('MNazmi.xpp-vscode');
    await ext.activate();
    await sleep(3000);

    await check('commands registered', async () => {
        const all = await vscode.commands.getCommands(true);
        return ['xpp.security.analyzeLicense', 'xpp.security.whoCanAccess', 'xpp.security.findGaps', 'xpp.security.checkSod',
            'xpp.security.report', 'xpp.security.editSodRules', 'xpp.security.refresh'].map(c => c + ':' + all.includes(c));
    });
    await check('security view contributed', async () => {
        const views = ext.packageJSON.contributes.views.xpp.map(v => v.id);
        return views;
    });
    await check('license SystemUser', () => panelAfter('xpp.security.analyzeLicense', { item: { objectType: 'AxSecurityRole', objectName: 'SystemUser' } }));
    await check('who can access CustGroup table', () => panelAfter('xpp.security.whoCanAccess', { item: { objectType: 'AxTable', objectName: 'CustGroup' } }));
    await check('sod on a role', () => panelAfter('xpp.security.checkSod', { item: { objectType: 'AxSecurityRole', objectName: 'VendInvoiceAccountsPayableManager' } }));
    await check('license of a menu item is refused', async () => {
        const r = await vscode.commands.executeCommand('xpp.security.analyzeLicense', { item: { objectType: 'AxTable', objectName: 'CustGroup' } }).then(() => 'no error', e => String(e));
        return r;
    });

    results.finished = true;
    save();
}

module.exports = { run };
