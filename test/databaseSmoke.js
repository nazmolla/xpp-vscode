// Smoke test of database access and the server status in a real extension host (no credentials are requested).
// Run: node test/runInDownloadedCode.js test/databaseSmoke.js; results go to XPP_SMOKE_OUT (default %TEMP%\xpp-database.json).
const vscode = require('vscode');
const fs = require('fs');
const os = require('os');
const path = require('path');

const sleep = ms => new Promise(r => setTimeout(r, ms));

async function run() {
    const results = {};
    const out = process.env.XPP_SMOKE_OUT || path.join(os.tmpdir(), 'xpp-database.json');
    const save = () => fs.writeFileSync(out, JSON.stringify(results, null, 2));
    const check = async (name, fn) => {
        const t0 = Date.now();
        try { results[name] = { ok: true, value: await fn(), ms: Date.now() - t0 }; }
        catch (err) { results[name] = { ok: false, error: String(err && err.stack || err), ms: Date.now() - t0 }; }
        save();
    };
    const tabs = () => vscode.window.tabGroups.all.flatMap(g => g.tabs.map(t => t.label));
    const panelAfter = async (cmd, ...args) => {
        const before = new Set(tabs());
        const run = vscode.commands.executeCommand(cmd, ...args);
        for (let i = 0; i < 120; i++) {
            const added = tabs().filter(t => !before.has(t));
            if (added.length) { return added; }
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
        const missing = ['xpp.database.browseTable', 'xpp.database.newQuery', 'xpp.database.requestCredentials', 'xpp.database.forgetCredentials',
            'xpp.database.testConnection', 'xpp.database.copyConnection', 'xpp.database.menu', 'xpp.restartServer'].filter(c => !all.includes(c));
        if (missing.length) { throw new Error('missing: ' + missing.join(', ')); }
        return 'all registered';
    });
    await check('query tool registered', () => {
        const tool = vscode.lm.tools.find(t => t.name === 'xpp_query_data');
        if (!tool) { throw new Error('xpp_query_data is not registered'); }
        return tool.description.slice(0, 60);
    });
    await check('walkthrough contributed', () => ext.packageJSON.contributes.walkthroughs.map(w => `${w.id}: ${w.steps.length} steps`));
    await check('restart server', async () => {
        await vscode.commands.executeCommand('xpp.restartServer');
        return 'restarted';
    });
    await check('browse data panel opens for a table', () => panelAfter('xpp.database.browseTable', { item: { objectType: 'AxTable', objectName: 'CustGroup' } }));
    await check('query panel opens', () => panelAfter('xpp.database.newQuery'));

    results.finished = true;
    save();
}

module.exports = { run };
