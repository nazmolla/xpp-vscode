// Smoke test of the report designer in a real extension host: commands, menus, and the designer panel on a standard report
// (read-only) and on a report that doesn't exist. Run: node test/runInDownloadedCode.js test/reportSmoke.js; results go to
// XPP_SMOKE_OUT (default %TEMP%\xpp-report.json).
const vscode = require('vscode');
const fs = require('fs');
const os = require('os');
const path = require('path');

const sleep = ms => new Promise(r => setTimeout(r, ms));

async function run() {
    const results = {};
    const out = process.env.XPP_SMOKE_OUT || path.join(os.tmpdir(), 'xpp-report.json');
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

    await check('report commands registered', async () => {
        const all = await vscode.commands.getCommands(true);
        const missing = ['xpp.reports.openDesigner', 'xpp.reports.installRuntime', 'xpp.reports.createPrecisionDesign', 'xpp.reports.addDataRegion'].filter(c => !all.includes(c));
        if (missing.length) { throw new Error('missing: ' + missing.join(', ')); }
        return 'all registered';
    });
    await check('report designer is on report nodes and in the element designer', () => {
        const menus = ext.packageJSON.contributes.menus;
        const tree = menus['view/item/context'].some(m => m.command === 'xpp.reports.openDesigner' && /report/.test(m.when));
        const designer = menus['webview/context'].some(m => m.command === 'xpp.reports.openDesigner' && /xppElementDesigner/.test(m.when));
        const precision = menus['webview/context'].some(m => m.command === 'xpp.reports.openDesigner' && /ReportPrecisionDesign/.test(m.when));
        const auto = menus['webview/context'].some(m => m.command === 'xpp.reports.createPrecisionDesign' && /ReportAutoDesign/.test(m.when))
            && menus['webview/context'].some(m => m.command === 'xpp.reports.addDataRegion' && /ReportAutoDesign/.test(m.when));
        if (!tree || !designer || !precision || !auto) { throw new Error(`tree ${tree}, element designer ${designer}, precision design ${precision}, auto design ${auto}`); }
        return 'contributed';
    });
    await check('report builder agent contributed', () => {
        const agents = (ext.packageJSON.contributes.chatAgents || []).map(a => a.path || a.name);
        if (!agents.some(a => /report-builder/.test(a))) { throw new Error('report-builder agent missing: ' + agents.join(', ')); }
        return 'contributed';
    });
    await check('designer opens on a standard report', async () => {
        const opened = await panelAfter('xpp.reports.openDesigner', { item: { objectType: 'AxReport', objectName: 'Cheque_US' } });
        if (!Array.isArray(opened) || !opened.some(t => /Cheque_US/.test(t))) { throw new Error(JSON.stringify(opened)); }
        return opened;
    });
    await check('a design passed from the element designer opens that design', async () => {
        const opened = await panelAfter('xpp.reports.openDesigner', { xppReportName: 'PurchPackingSlip', xppReportDesign: 'ReportCZ' });
        if (!Array.isArray(opened) || !opened.some(t => /PurchPackingSlip\.ReportCZ/.test(t))) { throw new Error(JSON.stringify(opened)); }
        return opened;
    });
    await check('a missing report opens nothing harmful', async () => {
        await vscode.commands.executeCommand('xpp.reports.openDesigner', 'NoSuchReportXvs').catch(() => undefined);
        await sleep(3000);
        return 'no crash';
    });

    results.finished = true;
    save();
}

module.exports = { run };
