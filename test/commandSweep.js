// End-to-end sweep of every contributed command in a real extension host, the way a developer triggers them:
//   pass "palette":  each command without arguments (Command Palette), every prompt cancelled;
//   pass "element":  element commands with a tree-item argument on standard and custom elements (context menus);
//   pass "designer": designer commands with an element designer open (editor title / webview context menus).
// Prompts are answered by XPP_SWEEP_ANSWER: 'cancel' (default) or 'first' (first quick-pick item; safe commands only).
// Records per command: thrown errors (the wrapper's "Report Problem" error), messages, opened panels/editors, external
// URLs, files written in the custom model, time, and timeouts. Results: XPP_SMOKE_OUT (default %TEMP%\xpp-sweep.json).
// Run: set XPP_TEST_WORKSPACE to the TestProj workspace, then node scripts/run-smoke.js commandSweep
const vscode = require('vscode');
const fs = require('fs');
const os = require('os');
const path = require('path');

const sleep = ms => new Promise(r => setTimeout(r, ms));
const CustomModelFolder = process.env.XPP_SWEEP_MODEL_FOLDER || 'C:\\CustomXppMetadata2vxeu0wu.5pd\\testcust';

// Never run: they close or replace the window, start external programs or change an environment.
const Skip = {
    'xpp.closeProject': 'closes the workspace (window reload)',
    'xpp.openProject': 'opens another window',
    'xpp.openRecentProject': 'opens another window',
    'xpp.selectProject': 'opens another window',
    'xpp.createProject': 'creates a project and opens it',
    'xpp.pac.deploy': 'deploys to an environment',
    'xpp.pac.dbSync': 'synchronizes an environment database',
    'xpp.pac.signIn': 'interactive pac sign-in',
    'xpp.pac.init': 'writes .erp files into the workspace',
    'xpp.database.openSsms': 'starts SSMS',
    'xpp.restartServer': 'run last separately',
    'xpp.debug.start': 'attaches to an environment',
    'xpp.syncTables': 'synchronizes the database',
    'xpp.cleanUpOldVersions': 'deletes application versions',
    'xpp.tools.createPackage': 'slow; covered by xpp_package',
    'xpp.createTeamRules': 'writes a rules file into the workspace without a prompt',
    'xpp.agent.revertCheckpoint': 'reverts files (tested separately)',
    'xpp.agent.startCheckpoint': 'opens a checkpoint that stays open (ending it asks for confirmation)',
    'xpp.verifyInRunningApp': 'opens a Copilot chat',
};
// Destructive on an element: only in the palette pass (argument-less, prompt cancelled).
const NoElementPass = new Set(['xpp.deleteObject', 'xpp.renameElement', 'xpp.excludeFromProject', 'xpp.syncTables', 'xpp.designer.delete', 'xpp.designer.rename', 'xpp.designer.duplicate', 'xpp.designer.moveUp', 'xpp.designer.moveDown']);
const Slow = new Set(['xpp.build', 'xpp.buildProject', 'xpp.bestPractice', 'xpp.preDeployChecklist', 'xpp.reviewChanges', 'xpp.upgradeReadiness', 'xpp.security.findGaps', 'xpp.security.checkSod', 'xpp.security.report', 'xpp.codeIntel.rebuildIndex', 'xpp.standardCodeDrift', 'xpp.findUnusedLabels', 'xpp.runTests', 'xpp.tools.exportProject']);

function listFiles(dir) {
    const out = new Map();
    const walk = d => {
        let entries = [];
        try { entries = fs.readdirSync(d, { withFileTypes: true }); } catch { return; }
        for (const e of entries) {
            const p = path.join(d, e.name);
            if (e.isDirectory()) { if (!/^(bin|XppMetadata|Resources|Descriptor)$/i.test(e.name) || d === dir) walk(p); }
            else { try { out.set(p, fs.statSync(p).mtimeMs); } catch { } }
        }
    };
    walk(dir);
    return out;
}

async function run() {
    const results = {};
    const out = process.env.XPP_SMOKE_OUT || path.join(os.tmpdir(), 'xpp-sweep.json');
    const save = () => fs.writeFileSync(out, JSON.stringify(results, null, 2));
    const answer = process.env.XPP_SWEEP_ANSWER || 'cancel';
    const only = (process.env.XPP_SWEEP_ONLY || '').split(',').filter(Boolean);
    const passes = (process.env.XPP_SWEEP_PASSES || 'palette,element,designer').split(',');

    // --- instrumentation ---
    let current = null;
    const rec = () => current ?? (current = { messages: [], prompts: [], panels: [], external: [], clipboard: [], unhandled: [] });
    const w = vscode.window;
    const orig = { info: w.showInformationMessage, warn: w.showWarningMessage, error: w.showErrorMessage, qp: w.showQuickPick, ib: w.showInputBox, open: w.showOpenDialog, save: w.showSaveDialog, panel: w.createWebviewPanel, cqp: w.createQuickPick, cib: w.createInputBox, ext: vscode.env.openExternal };
    const msg = kind => (message, ...rest) => {
        const items = rest.filter(r => typeof r === 'string' || (r && r.title));
        rec().messages.push({ kind, message: String(message).slice(0, 400), buttons: items.map(i => typeof i === 'string' ? i : i.title) });
        return Promise.resolve(undefined);
    };
    w.showInformationMessage = msg('info');
    w.showWarningMessage = msg('warning');
    w.showErrorMessage = msg('error');
    w.showQuickPick = async (items, options) => {
        const list = await items;
        rec().prompts.push({ kind: 'quickPick', title: options?.title ?? options?.placeHolder, count: list?.length ?? 0 });
        if (answer === 'first' && list?.length) return options?.canPickMany ? [list[0]] : list[0];
        return undefined;
    };
    w.showInputBox = async options => { rec().prompts.push({ kind: 'inputBox', title: options?.title ?? options?.prompt }); return undefined; };
    w.showOpenDialog = async options => { rec().prompts.push({ kind: 'openDialog', title: options?.title }); return undefined; };
    w.showSaveDialog = async options => { rec().prompts.push({ kind: 'saveDialog', title: options?.title }); return undefined; };
    w.createWebviewPanel = (...args) => { const p = orig.panel.apply(w, args); rec().panels.push({ viewType: args[0], title: args[1], panel: p }); return p; };
    const autoHide = (kind, factory) => () => {
        const ui = factory.call(w);
        const show = ui.show.bind(ui);
        ui.show = () => { rec().prompts.push({ kind, title: ui.title ?? ui.placeholder }); show(); setTimeout(() => ui.hide(), 1500); };
        return ui;
    };
    w.createQuickPick = autoHide('createQuickPick', orig.cqp);
    w.createInputBox = autoHide('createInputBox', orig.cib);
    vscode.env.openExternal = async uri => { rec().external.push(String(uri).slice(0, 300)); return true; };
    process.on('unhandledRejection', err => { rec().unhandled.push(String(err && err.stack || err).slice(0, 600)); });

    const ext = vscode.extensions.getExtension('MNazmi.xpp-vscode');
    await ext.activate();
    await sleep(8000);

    const pkg = JSON.parse(fs.readFileSync(path.join(__dirname, '..', 'package.json'), 'utf8'));
    const registered = new Set(await vscode.commands.getCommands(true));
    const contributed = pkg.contributes.commands.map(c => c.command);
    results.notRegistered = contributed.filter(c => !registered.has(c));
    save();

    const execute = async (key, id, args) => {
        current = null;
        rec();
        const before = listFiles(CustomModelFolder);
        const t0 = Date.now();
        const limit = Slow.has(id) ? 600000 : 90000;
        let status, value, error;
        try {
            value = await Promise.race([vscode.commands.executeCommand(id, ...args), sleep(limit).then(() => { throw new Error('TIMEOUT ' + limit + ' ms'); })]);
            status = 'ok';
        } catch (err) {
            status = /^TIMEOUT/.test(err && err.message) ? 'timeout' : 'threw';
            error = String(err && err.stack || err).slice(0, 800);
        }
        await sleep(1500);
        const after = listFiles(CustomModelFolder);
        const written = [...after].filter(([p, m]) => before.get(p) !== m).map(([p]) => p.replace(CustomModelFolder, ''));
        const deleted = [...before.keys()].filter(p => !after.has(p)).map(p => p.replace(CustomModelFolder, ''));
        const r = current;
        const panels = [];
        for (const p of r.panels) {
            let html = '';
            try { html = p.panel.webview.html; } catch { }
            panels.push({ viewType: p.viewType, title: p.title, htmlChars: html.length });
        }
        const tabs = vscode.window.tabGroups.all.flatMap(g => g.tabs).map(t => t.label);
        const crashed = r.messages.some(m => m.kind === 'error' && m.buttons.includes('Report Problem'));
        results[key] = {
            status: crashed ? 'crashed' : status, ms: Date.now() - t0, value: value === undefined ? undefined : String(JSON.stringify(value)).slice(0, 200),
            error, messages: r.messages, prompts: r.prompts, panels, tabs, external: r.external, unhandled: r.unhandled, written, deleted,
        };
        save();
        for (const p of r.panels) { try { p.panel.dispose(); } catch { } }
        await vscode.commands.executeCommand('workbench.action.closeAllEditors');
        current = null;
    };

    const want = id => !Skip[id] && (!only.length || only.includes(id));
    for (const id of contributed) if (Skip[id]) results['skipped:' + id] = { status: 'skipped', reason: Skip[id] };

    if (passes.includes('palette')) {
        const hidden = new Set((pkg.contributes.menus.commandPalette ?? []).filter(m => m.when === 'false').map(m => m.command));
        for (const id of contributed.filter(c => want(c) && !hidden.has(c))) await execute('palette:' + id, id, []);
    }

    if (passes.includes('element')) {
        const elementCommands = pkg.contributes.menus['view/item/context'].map(m => m.command)
            .concat((pkg.contributes.menus['webview/context'] ?? []).map(m => m.command))
            .filter((c, i, a) => a.indexOf(c) === i && !c.startsWith('xpp.designer.') && !NoElementPass.has(c) && want(c));
        const targets = [
            { objectType: 'AxTable', objectName: 'CustTable' },
            { objectType: 'AxClass', objectName: 'SalesFormLetter' },
            { objectType: 'AxForm', objectName: 'CustTable' },
            { objectType: 'AxTable', objectName: process.env.XPP_SWEEP_CUSTOM_TABLE || 'HSAssetRemovalReasonTable' },
        ];
        for (const target of targets) {
            for (const id of elementCommands) {
                await execute(`element:${target.objectType}:${target.objectName}:${id}`, id, [{ item: target, objectType: target.objectType, entry: { name: target.objectName } }]);
            }
        }
    }

    if (passes.includes('designer')) {
        const designerCommands = contributed.filter(c => c.startsWith('xpp.designer.') && !NoElementPass.has(c) && want(c));
        for (const target of [{ t: 'AxTable', n: 'CustTable' }, { t: 'AxClass', n: 'SalesFormLetter' }]) {
            for (const id of designerCommands) {
                current = null;
                await vscode.commands.executeCommand('xpp.openDesigner', target.t, target.n);
                await sleep(3000);
                await execute(`designer:${target.t}:${target.n}:${id}`, id, []);
            }
        }
    }

    results.finished = true;
    save();
}

module.exports = { run };
