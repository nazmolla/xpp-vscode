// Drives a VS Code window through F&O Studio's features for the screenshots in docs/images.
// Each step writes its name (or JSON with keys to type first) to <XPP_SHOTS_DIR>/step.txt and waits for
// <XPP_SHOTS_DIR>/<name>.done, which scripts/screenshots.ps1 creates after capturing the window.
// Run with scripts/screenshots.ps1 -Workspace <TestProj .code-workspace> (throw-away profile, only this extension loaded).
const vscode = require('vscode');
const fs = require('fs');
const os = require('os');
const path = require('path');

const dir = process.env.XPP_SHOTS_DIR || path.join(os.tmpdir(), 'xpp-shots');
const sleep = ms => new Promise(r => setTimeout(r, ms));
const objectUri = (type, name) => vscode.Uri.from({ scheme: 'xpp', path: `/${type}/${name}.xpp` });
const log = msg => fs.appendFileSync(path.join(dir, 'log.txt'), new Date().toISOString() + ' ' + msg + '\n');
const item = (objectType, objectName) => ({ item: { objectType, objectName } });
const customModel = process.env.XPP_SHOTS_MODEL || 'testcust';

/** The running step; a step that timed out must not capture over the next one. */
let activeStep = '';

/** Captures the window; extra.keys are typed first, extra.capture=false only types them. */
async function shot(name, settle = 4000, extra) {
    await sleep(settle);
    if (activeStep && !name.startsWith(activeStep)) {
        log('skipped stale shot ' + name);
        return;
    }
    const done = path.join(dir, name + '.done');
    fs.writeFileSync(path.join(dir, 'step.txt'), extra ? JSON.stringify({ name, ...extra }) : name);
    log('shot ' + name);
    for (let i = 0; i < 240 && !fs.existsSync(done); i++) {
        await sleep(500);
    }
}

/** Pickers, input boxes and notification progress the extension has shown; the test shares the extension's API object. */
const ui = { picks: 0, inputs: 0, busy: 0 };
function instrument() {
    const w = vscode.window;
    const { showQuickPick, createQuickPick, showInputBox, createInputBox, withProgress } = w;
    w.showQuickPick = function (items, ...rest) {
        Promise.resolve(items).then(() => setTimeout(() => ui.picks++, 300), () => undefined);
        return showQuickPick.call(w, items, ...rest);
    };
    w.createQuickPick = function () {
        const pick = createQuickPick.call(w);
        const show = pick.show.bind(pick);
        pick.show = () => { show(); ui.picks++; };
        return pick;
    };
    w.showInputBox = function (...args) {
        setTimeout(() => ui.inputs++, 300);
        return showInputBox.apply(w, args);
    };
    w.createInputBox = function () {
        const box = createInputBox.call(w);
        const show = box.show.bind(box);
        box.show = () => { show(); ui.inputs++; };
        return box;
    };
    w.withProgress = function (options, task) {
        const counted = options && options.location === vscode.ProgressLocation.Notification;
        if (counted) {
            ui.busy++;
        }
        const result = withProgress.call(w, options, task);
        const done = () => { if (counted) { ui.busy--; } };
        result.then(done, done);
        return result;
    };
    log('instrumented: ' + (w.showQuickPick !== showQuickPick));
}

async function until(condition, timeout = 120000) {
    const end = Date.now() + timeout;
    while (!condition() && Date.now() < end) {
        await sleep(300);
    }
    if (!condition()) {
        log('timed out waiting');
    }
}

const pending = new Set();

/** Runs a command without waiting for it, for commands that end in a quick pick or input box. */
function fire(command, ...args) {
    const p = vscode.commands.executeCommand(command, ...args).then(undefined, err => log(`${command} failed: ${err && err.message || err}`));
    pending.add(p);
    p.then(() => pending.delete(p));
    return p;
}

/**
 * Runs a command that ends in a picker and captures the picker once it shows its results.
 * input: SendKeys strings typed into the command's input box first; keys: typed into the picker before the capture.
 */
async function pickShot(name, command, args, { input, keys, settle = 1500, timeout = 300000 } = {}) {
    const picks = ui.picks;
    const inputs = ui.inputs;
    fire(command, ...(args || []));
    if (input) {
        await until(() => ui.inputs > inputs, 30000);
        await shot(name + '-input', 800, { keys: input, capture: false });
        // The command starts its progress notification only after the input box closes.
        await sleep(1500);
    }
    await until(() => ui.picks > picks && ui.busy === 0, timeout);
    await shot(name, settle, keys ? { keys } : undefined);
}

async function reset() {
    // A command still waiting on a picker or the server would show its picker during the next step.
    // Notifications with buttons keep their command's promise open until dismissed.
    for (let i = 0; i < 120 && pending.size; i++) {
        await vscode.commands.executeCommand('workbench.action.closeQuickOpen');
        await vscode.commands.executeCommand('notifications.clearAll');
        await sleep(500);
    }
    await vscode.commands.executeCommand('workbench.action.closeQuickOpen');
    await vscode.commands.executeCommand('workbench.action.closeAllEditors');
    await vscode.commands.executeCommand('workbench.action.closePanel');
    await vscode.commands.executeCommand('workbench.action.closeSidebar');
    await vscode.commands.executeCommand('notifications.clearAll');
    await sleep(500);
}

/** Commands that end in a notification or report: started without waiting, then given time (and progress) to finish. */
async function runAndWait(command, args, ms) {
    fire(command, ...(args || []));
    await sleep(2000);
    await until(() => ui.busy === 0, 300000);
    await sleep(ms);
}

async function widenSidebar() {
    await vscode.commands.executeCommand('references-view.tree.focus');
    await sleep(500);
    for (let i = 0; i < 10; i++) {
        await vscode.commands.executeCommand('workbench.action.increaseViewWidth');
    }
    await sleep(800);
}

async function expandTree(view, steps) {
    await vscode.commands.executeCommand(`${view}.focus`);
    await sleep(800);
    await vscode.commands.executeCommand('list.focusFirst');
    for (let i = 0; i < steps; i++) {
        await vscode.commands.executeCommand('list.expand');
        await sleep(900);
        await vscode.commands.executeCommand('list.focusDown');
    }
}

/** Opens an element's code with the cursor on the first match of the pattern (on its first group, if any); returns the editor. */
async function openCode(type, name, pattern, offset = 0, wait = 6000) {
    const doc = await vscode.workspace.openTextDocument(objectUri(type, name));
    const editor = await vscode.window.showTextDocument(doc);
    const match = pattern ? pattern.exec(doc.getText()) : null;
    const at = match ? match.index + (match[1] ? match[0].indexOf(match[1]) : 0) : 0;
    const pos = doc.positionAt(at + offset);
    editor.selection = new vscode.Selection(pos, pos);
    editor.revealRange(new vscode.Range(pos.translate(-Math.min(pos.line, 4)), pos.translate(25)), vscode.TextEditorRevealType.AtTop);
    await sleep(wait);
    return editor;
}

async function step(name, fn, timeoutMs = 360000) {
    const only = (process.env.XPP_SHOTS_ONLY || '').split(',').filter(s => s);
    if (only.length && !only.includes(name)) {
        return;
    }
    log('step ' + name);
    try {
        activeStep = '';
        await reset();
        activeStep = name;
        let timer;
        const timeout = new Promise((_, reject) => { timer = setTimeout(() => reject(new Error('timed out')), timeoutMs); });
        await Promise.race([fn(), timeout]).finally(() => clearTimeout(timer));
    } catch (err) {
        log('step ' + name + ' failed: ' + (err && err.stack || err));
    } finally {
        activeStep = '';
    }
}

async function run() {
    fs.mkdirSync(dir, { recursive: true });
    const ext = vscode.extensions.getExtension('MNazmi.xpp-vscode');
    await ext.activate();
    instrument();
    await vscode.commands.executeCommand('workbench.action.closeAuxiliaryBar');
    await vscode.commands.executeCommand('workbench.action.closePanel');
    await vscode.commands.executeCommand('workbench.view.extension.xpp');
    await sleep(6000);
    // VS Code's first-run sign-in dialog: scripts/screenshots.ps1 dismisses it on this step (nothing is captured).
    await shot('dismiss', 2000);

    // ── Application Explorer and projects ──
    await step('aot', async () => {
        await vscode.commands.executeCommand('xpp.aot.filter', 'CustGroup');
        await sleep(3000);
        await expandTree('xpp.aot', 9);
        await shot('aot', 3000);
    });
    await step('aot-model-view', async () => {
        await vscode.commands.executeCommand('xpp.aot.modelView');
        await vscode.commands.executeCommand('xpp.aot.filter', 'CustGroup');
        await sleep(3000);
        await expandTree('xpp.aot', 6);
        await shot('aot-model-view', 3000);
        await vscode.commands.executeCommand('xpp.aot.classicView');
    });
    await step('aot-context-menu', async () => {
        await vscode.commands.executeCommand('xpp.aot.filter', 'CustGroup');
        await sleep(3000);
        await vscode.commands.executeCommand('xpp.revealInAot', item('AxTable', 'CustGroup'));
        await shot('aot-context-menu', 3000, { keys: ['+{F10}'], wait: 1500 });
        await vscode.commands.executeCommand('workbench.action.closeQuickOpen');
    });
    await vscode.commands.executeCommand('xpp.aot.clearFilter');
    await step('project-view', async () => {
        await expandTree('xpp.project', 4);
        await shot('project-view', 3000);
    });
    await step('dynamics-365-view', async () => {
        await vscode.commands.executeCommand('xpp.aot.focus');
        await vscode.commands.executeCommand('workbench.actions.treeView.xpp.aot.collapseAll');
        await vscode.commands.executeCommand('xpp.tools.focus');
        await shot('dynamics-365-view', 3000);
    });
    await step('security-view', async () => {
        await expandTree('xpp.security', 3);
        await shot('security-view', 15000);
    });
    await step('open-element', async () => {
        fire('xpp.openObject');
        await shot('open-element', 1500, { keys: ['CustGroup'], wait: 4000 });
    });
    await step('new-element', async () => {
        await pickShot('new-element', 'xpp.newElement');
    });

    // ── Designers ──
    await step('table-designer', async () => {
        await vscode.commands.executeCommand('xpp.openDesigner', 'AxTable', 'CustGroup');
        await shot('table-designer', 9000);
    });
    await step('form-designer', async () => {
        await vscode.commands.executeCommand('xpp.openDesigner', 'AxForm', 'CustGroup');
        await shot('form-designer', 9000);
    });
    await step('form-layout', async () => {
        await vscode.commands.executeCommand('xpp.openFormDesigner', 'CustGroup');
        await shot('form-layout', 9000);
    });
    await step('label-editor', async () => {
        await vscode.commands.executeCommand('xpp.openDesigner', 'AxLabelFile', 'SYS_en-US');
        await shot('label-editor', 10000);
    });
    await step('erd', async () => {
        await vscode.commands.executeCommand('xpp.viewErd', item('AxTable', 'CustGroup'));
        await shot('erd', 9000);
    });
    await step('hierarchy-diagram', async () => {
        await vscode.commands.executeCommand('xpp.viewHierarchy', item('AxClass', 'SalesFormLetter'));
        await shot('hierarchy-diagram', 9000);
    });
    await step('references-diagram', async () => {
        await vscode.commands.executeCommand('xpp.viewReferences', item('AxTable', 'CustGroup'));
        await shot('references-diagram', 9000);
    });

    // ── Code ──
    await step('code-view', async () => {
        await openCode('AxTable', 'CustGroup', /\bstatic\s+CustGroup\s+find\s*\(/, 0, 9000);
        await shot('code-view', 2000);
    });
    await step('hover', async () => {
        await openCode('AxTable', 'CustGroup', /"(@GLS390)"/, 2, 12000);
        await vscode.commands.executeCommand('workbench.action.focusActiveEditorGroup');
        // A real Ctrl+K Ctrl+I after the window is in front; bringing the window forward closes a hover shown earlier.
        await shot('hover', 1000, { keys: ['^k', '^i'], wait: 5000 });
    });
    await step('implementations', async () => {
        const editor = await openCode('AxClass', 'SalesFormLetter', /\bvoid\s+run\s*\(\s*\)/, 0, 0);
        const text = editor.document.getText();
        const pos = editor.document.positionAt(text.indexOf('run', text.search(/\bvoid\s+run\s*\(\s*\)/) + 4));
        editor.selection = new vscode.Selection(pos, pos);
        editor.revealRange(new vscode.Range(pos.translate(-3), pos.translate(25)), vscode.TextEditorRevealType.AtTop);
        await sleep(9000);
        await vscode.commands.executeCommand('editor.action.peekImplementation');
        await shot('implementations', 7000);
        await vscode.commands.executeCommand('closeReferenceSearch');
    });
    await step('completion', async () => {
        const doc = await vscode.workspace.openTextDocument({ language: 'xpp', content: 'class XvsDemo\n{\n    public static void main(Args _args)\n    {\n        CustGroup custGroup = CustGroup::\n    }\n}\n' });
        const editor = await vscode.window.showTextDocument(doc);
        const pos = new vscode.Position(4, doc.lineAt(4).text.length);
        editor.selection = new vscode.Selection(pos, pos);
        await sleep(5000);
        await vscode.commands.executeCommand('editor.action.triggerSuggest');
        await shot('completion', 5000);
        await vscode.commands.executeCommand('hideSuggestWidget');
        await vscode.commands.executeCommand('workbench.action.revertAndCloseActiveEditor');
    });
    await step('call-hierarchy', async () => {
        await openCode('AxTable', 'CustGroup', /\bstatic\s+CustGroup\s+(find)\s*\(/, 0, 8000);
        await vscode.commands.executeCommand('references-view.showCallHierarchy');
        await sleep(6000);
        await widenSidebar();
        // Expand two callers so the tree shows callers of callers, not a flat list.
        await vscode.commands.executeCommand('references-view.tree.focus');
        await vscode.commands.executeCommand('list.focusFirst');
        for (let i = 0; i < 2; i++) {
            await vscode.commands.executeCommand('list.focusDown');
            await vscode.commands.executeCommand('list.expand');
            await sleep(3000);
        }
        await shot('call-hierarchy', 3000);
    });
    await step('type-hierarchy', async () => {
        await openCode('AxClass', 'SalesFormLetter', /\bclass\s+(SalesFormLetter)\b/, 0, 12000);
        await vscode.commands.executeCommand('references-view.showTypeHierarchy');
        await sleep(15000);
        await vscode.commands.executeCommand('references-view.tree.focus');
        await vscode.commands.executeCommand('list.focusFirst');
        await vscode.commands.executeCommand('list.focusDown');
        await vscode.commands.executeCommand('list.expand');
        // Drag the sidebar's edge so the subclass names are readable.
        await shot('type-hierarchy', 3000, { drag: [228, 500, 620, 500], wait: 2000 });
    });
    await step('find-references', async () => {
        await openCode('AxTable', 'CustGroup', /\bstatic\s+CustGroup\s+(find)\s*\(/, 0, 8000);
        await vscode.commands.executeCommand('references-view.findReferences');
        await sleep(8000);
        await widenSidebar();
        await shot('find-references', 2000);
    });
    await step('outline', async () => {
        await openCode('AxClass', 'SalesFormLetter', null, 0, 6000);
        await vscode.commands.executeCommand('outline.focus');
        await shot('outline', 4000);
    });

    // ── Productivity ──
    await step('search-code', async () => {
        await pickShot('search-code', 'xpp.searchCode', [], { input: ['^a', 'CustGroup::find', '{ENTER}'] });
    });
    await step('find-message-source', async () => {
        await pickShot('find-message-source', 'xpp.findMessageSource', [{ message: "Field 'Customer group' must be filled in." }], { input: ['{ENTER}'] });
    });
    await step('find-examples', async () => {
        await pickShot('find-examples', 'xpp.findExamples', [], { input: ['batch job with contract', '{ENTER}'] });
    });
    await step('find-labels', async () => {
        // Label file picker, then the search text, then the results picker.
        const picks = ui.picks;
        fire('xpp.tools.findLabels');
        await until(() => ui.picks > picks, 30000);
        const inputs = ui.inputs;
        await shot('find-labels-file', 1000, { keys: ['SYS', '{ENTER}'], capture: false });
        await until(() => ui.inputs > inputs, 30000);
        await shot('find-labels-input', 800, { keys: ['customer group', '{ENTER}'], capture: false });
        await until(() => ui.picks > picks + 1, 300000);
        await shot('find-labels', 1500);
    });
    await step('extensions-and-handlers', async () => {
        await pickShot('extensions-and-handlers', 'xpp.codeIntel.showExtensions', [item('AxClass', 'SalesFormLetter')]);
    });
    await step('code-intelligence-settings', async () => {
        await pickShot('code-intelligence-settings', 'xpp.codeIntel.configure');
    });

    // ── Analysis ──
    await step('analyze-performance', async () => {
        await runAndWait('xpp.analyzePerformance', [item('AxClass', 'CustCollectionLetterCreate')], 6000);
        await vscode.commands.executeCommand('workbench.actions.view.problems');
        await shot('analyze-performance', 3000);
    });
    const trace = fs.readdirSync(path.join(__dirname, '..')).find(f => f.toLowerCase().endsWith('.etl'));
    if (trace) {
        await step('trace', async () => {
            await runAndWait('xpp.analyzeTrace', [vscode.Uri.file(path.join(__dirname, '..', trace))], 8000);
            await shot('trace', 1000);
        });
    }
    await step('telemetry-queries', async () => {
        await runAndWait('xpp.telemetryQueries', [item('AxForm', 'CustGroup')], 3000);
        await shot('telemetry-queries', 1000);
    });
    await step('upgrade-readiness', async () => {
        fire('xpp.upgradeReadiness', customModel);
        await sleep(2000);
        await until(() => ui.busy === 0, 300000);
        await vscode.commands.executeCommand('xpp.results.focus');
        await shot('upgrade-readiness', 5000);
    });
    await step('standard-code-drift', async () => {
        fire('xpp.standardCodeDrift', customModel);
        await shot('standard-code-drift-input', 3000, { keys: ['{ENTER}'], capture: false });
        await sleep(2000);
        await until(() => ui.busy === 0, 300000);
        await vscode.commands.executeCommand('xpp.results.focus');
        await shot('standard-code-drift', 3000);
    });

    // ── Security ──
    await step('analyze-license', async () => {
        await runAndWait('xpp.security.analyzeLicense', [item('AxSecurityRole', 'TradeSalesClerk')], 5000);
        await shot('analyze-license', 1000);
    });
    await step('who-can-access', async () => {
        await runAndWait('xpp.security.whoCanAccess', [item('AxMenuItemDisplay', 'CustGroup')], 5000);
        await shot('who-can-access', 1000);
    });
    await step('security-gaps', async () => {
        fire('xpp.security.findGaps');
        await shot('security-gaps-input', 3000, { keys: ['{ENTER}'], capture: false });
        await sleep(2000);
        await until(() => ui.busy === 0, 300000);
        await shot('security-gaps', 15000);
    });
    await step('segregation-of-duties', async () => {
        await runAndWait('xpp.security.checkSod', [item('AxSecurityRole', 'TradeSalesClerk')], 5000);
        await shot('segregation-of-duties', 1000);
    });

    // ── Build, test, environment ──
    await step('model-dependencies', async () => {
        await runAndWait('xpp.tools.modelDependencies', [{ model: { name: customModel } }], 6000);
        await shot('model-dependencies', 1000);
    });
    await step('build', async () => {
        await runAndWait('xpp.buildProject', [], 5000);
        await vscode.commands.executeCommand('workbench.actions.view.problems');
        await shot('build', 2000);
    });
    await step('test-explorer', async () => {
        await vscode.commands.executeCommand('workbench.view.testing.focus');
        await shot('test-explorer', 6000);
    });
    await step('copilot-activity', async () => {
        await vscode.commands.executeCommand('xpp.activity.focus');
        await shot('copilot-activity', 4000);
    });
    await step('batch-job-wizard', async () => {
        fire('xpp.newSysOperationJob');
        await shot('batch-job-wizard', 3000);
    });
    await step('walkthrough', async () => {
        await vscode.commands.executeCommand('workbench.action.openWalkthrough', 'MNazmi.xpp-vscode#xpp.gettingStarted', false);
        await shot('walkthrough', 4000);
    });
    await step('settings', async () => {
        await vscode.commands.executeCommand('xpp.tools.options');
        await shot('settings', 4000);
    });

    await reset();
    fs.writeFileSync(path.join(dir, 'step.txt'), 'finished');
    log('finished');
}

module.exports = { run };
