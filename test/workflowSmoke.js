// Smoke test of build tasks, Analyze Performance and the new commands in a real extension host.
// Tasks need a workspace: set XPP_TEST_WORKSPACE to a project folder (e.g. the TestProj folder), then run
// node test/runInDownloadedCode.js test/workflowSmoke.js; results go to XPP_SMOKE_OUT (default %TEMP%\xpp-workflow.json).
const vscode = require('vscode');
const fs = require('fs');
const os = require('os');
const path = require('path');

const sleep = ms => new Promise(r => setTimeout(r, ms));

async function run() {
    const results = {};
    const out = process.env.XPP_SMOKE_OUT || path.join(os.tmpdir(), 'xpp-workflow.json');
    const save = () => fs.writeFileSync(out, JSON.stringify(results, null, 2));
    const check = async (name, fn) => {
        const t0 = Date.now();
        try { results[name] = { ok: true, value: await fn(), ms: Date.now() - t0 }; }
        catch (err) { results[name] = { ok: false, error: String(err && err.stack || err), ms: Date.now() - t0 }; }
        save();
    };

    const ext = vscode.extensions.getExtension('MNazmi.xpp-vscode');
    await ext.activate();
    await sleep(3000);

    await check('commands registered', async () => {
        const all = await vscode.commands.getCommands(true);
        const missing = ['xpp.analyzePerformance', 'xpp.upgradeReadiness', 'xpp.database.openSsms', 'xpp.restartServer', 'xpp.reviewChanges',
            'xpp.preDeployChecklist', 'xpp.bestPractice', 'xpp.newSysOperationJob', 'xpp.newReport', 'xpp.newWorkflow', 'xpp.newService', 'xpp.translateMissingLabels', 'xpp.findUnusedLabels',
            'xpp.analyzeTrace', 'xpp.syncTables', 'xpp.activity.clear', 'xpp.createTeamRules', 'xpp.showUsageStatistics', 'xpp.infolog.filter',
            'xpp.database.insights', 'xpp.showChangedElements', 'xpp.telemetryQueries', 'xpp.newTableWithForm', 'xpp.newDataEntity', 'xpp.renameElement',
            'xpp.viewDependencies', 'xpp.exportLabels', 'xpp.importLabels', 'xpp.devops.linkWorkItem', 'xpp.devops.openWorkItem', 'xpp.devops.openPipelines'].filter(c => !all.includes(c));
        if (missing.length) { throw new Error('missing: ' + missing.join(', ')); }
        return 'all registered';
    });
    await check('build tasks provided', async () => {
        const tasks = await vscode.tasks.fetchTasks({ type: 'xpp' });
        const actions = tasks.map(t => t.definition.action).sort().join(',');
        if (actions !== 'buildModel,buildProject,deploy,syncDatabase') { throw new Error(`expected build, deploy and sync tasks, got ${actions || 'none'} (tasks need a workspace: XPP_TEST_WORKSPACE)`); }
        return tasks.map(t => t.name);
    });
    await check('analyze performance publishes diagnostics', async () => {
        const uri = vscode.Uri.from({ scheme: 'xpp', path: '/AxClass/SalesFormLetter.xpp' });
        // The command ends with a notification that waits for a click; don't wait for it.
        void vscode.commands.executeCommand('xpp.analyzePerformance', uri);
        for (let i = 0; i < 120; i++) {
            const found = vscode.languages.getDiagnostics(uri).filter(d => d.source === 'X++ performance');
            if (found.length) { return found.map(d => `${d.code}@${d.range.start.line + 1}`); }
            await sleep(500);
        }
        throw new Error('no performance diagnostics');
    });
    await check('rename refuses methods of standard models', async () => {
        const uri = vscode.Uri.from({ scheme: 'xpp', path: '/AxClass/SalesFormLetter.xpp' });
        const doc = await vscode.workspace.openTextDocument(uri);
        const line = doc.getText().split(/\r?\n/).findIndex(l => /\bvoid\s+run\s*\(/.test(l));
        if (line < 0) { throw new Error('no run() method found'); }
        const position = new vscode.Position(line, doc.lineAt(line).text.indexOf('run') + 1);
        const outcome = await vscode.commands.executeCommand('vscode.executeDocumentRenameProvider', uri, position, 'runRenamed').then(() => 'renamed', e => String(e));
        if (!/standard model/.test(outcome)) { throw new Error(outcome); }
        return outcome;
    });
    await check('element hover has actions', async () => {
        const uri = vscode.Uri.from({ scheme: 'xpp', path: '/AxClass/SalesFormLetter.xpp' });
        const doc = await vscode.workspace.openTextDocument(uri);
        const line = doc.getText().split(/\r?\n/).findIndex(l => /\bSalesTable\b/.test(l));
        const position = new vscode.Position(line, doc.lineAt(line).text.indexOf('SalesTable') + 2);
        const hovers = await vscode.commands.executeCommand('vscode.executeHoverProvider', uri, position);
        const text = hovers.flatMap(h => h.contents.map(c => c.value ?? String(c))).join('\n');
        if (!/Open designer/.test(text)) { throw new Error(text.slice(0, 300)); }
        return text.slice(0, 200);
    });
    await check('analyze trace opens the trace panel', async () => {
        const etl = process.env.XPP_TEST_TRACE || path.resolve(__dirname, '..', 'test trace.etl');
        if (!fs.existsSync(etl)) { return 'skipped: no trace file'; }
        const errors = [];
        const original = vscode.window.showErrorMessage;
        vscode.window.showErrorMessage = (message, ...rest) => { errors.push(message); return original(message, ...rest); };
        try {
            await vscode.commands.executeCommand('xpp.analyzeTrace', vscode.Uri.file(etl));
        } finally {
            vscode.window.showErrorMessage = original;
        }
        await new Promise(r => setTimeout(r, 500));
        const tabs = vscode.window.tabGroups.all.flatMap(g => g.tabs).map(t => t.label);
        if (!tabs.some(l => l.endsWith('[Trace]'))) { throw new Error(JSON.stringify({ tabs, errors })); }
        const { parseTracedMethod } = require(path.resolve(__dirname, '..', 'out', 'traceAnalyzer.js'));
        const form = parseTracedMethod('Forms.PurchTable+PurchLine::linkActive');
        const cls = parseTracedMethod('PurchFormLetter::chooseLinesServer');
        if (form.objectType !== 'AxForm' || form.objectName !== 'PurchTable' || form.member !== 'linkActive' || cls.objectType || cls.objectName !== 'PurchFormLetter') {
            throw new Error(JSON.stringify({ form, cls }));
        }
        return tabs.filter(l => l.endsWith('[Trace]'));
    });

    results.finished = true;
    save();
}

module.exports = { run };
