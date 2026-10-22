// Drives a VS Code window through F&O Studio views on standard (Microsoft) objects for the README screenshots.
// Each step writes its name to <XPP_SHOTS_DIR>/step.txt and waits for <XPP_SHOTS_DIR>/<name>.done, which
// scripts/screenshots.ps1 creates after capturing the window.
// Run with scripts/screenshots.ps1 (throw-away profile, no project selected, only this extension loaded).
const vscode = require('vscode');
const fs = require('fs');
const os = require('os');
const path = require('path');

const dir = process.env.XPP_SHOTS_DIR || path.join(os.tmpdir(), 'xpp-shots');
const sleep = ms => new Promise(r => setTimeout(r, ms));
const objectUri = (type, name) => vscode.Uri.from({ scheme: 'xpp', path: `/${type}/${name}.xpp` });
const log = msg => fs.appendFileSync(path.join(dir, 'log.txt'), new Date().toISOString() + ' ' + msg + '\n');

async function shot(name, settle = 4000) {
    await sleep(settle);
    const done = path.join(dir, name + '.done');
    fs.writeFileSync(path.join(dir, 'step.txt'), name);
    log('shot ' + name);
    for (let i = 0; i < 120 && !fs.existsSync(done); i++) {
        await sleep(500);
    }
}

async function closeEditors() {
    await vscode.commands.executeCommand('workbench.action.closeAllEditors');
    await sleep(500);
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

async function step(name, fn) {
    try {
        await fn();
    } catch (err) {
        log('step ' + name + ' failed: ' + (err && err.stack || err));
    }
}

async function run() {
    fs.mkdirSync(dir, { recursive: true });
    const ext = vscode.extensions.getExtension('MNazmi.xpp-vscode');
    await ext.activate();
    await vscode.commands.executeCommand('workbench.action.closeAuxiliaryBar');
    await vscode.commands.executeCommand('workbench.action.closePanel');
    await vscode.commands.executeCommand('workbench.view.extension.xpp');
    await sleep(4000);
    // VS Code's first-run sign-in dialog: scripts/screenshots.ps1 dismisses it on this step (nothing is captured).
    await shot('dismiss', 2000);

    // 1. Application Explorer: categories in Visual Studio's order, filtered on a standard name.
    await step('aot', async () => {
        await vscode.commands.executeCommand('xpp.aot.filter', 'CustGroup');
        await sleep(3000);
        await expandTree('xpp.aot', 9);
        await shot('aot', 3000);
    });
    await vscode.commands.executeCommand('xpp.aot.clearFilter');

    // 2. Table designer.
    await step('table-designer', async () => {
        await vscode.commands.executeCommand('xpp.openDesigner', 'AxTable', 'CustGroup');
        await shot('table-designer', 7000);
        await closeEditors();
    });

    // 3. Form designer with the rendered preview.
    await step('form-designer', async () => {
        await vscode.commands.executeCommand('xpp.openDesigner', 'AxForm', 'CustGroup');
        await shot('form-designer', 9000);
        await closeEditors();
    });

    // 4. Code view with code lens (references, overrides, Chain of Command classes).
    let doc;
    await step('code', async () => {
        doc = await vscode.workspace.openTextDocument(objectUri('AxClass', 'SalesFormLetter'));
        const editor = await vscode.window.showTextDocument(doc);
        const i = doc.getText().search(/\bvoid\s+run\s*\(\s*\)/);
        const pos = doc.positionAt(Math.max(0, i));
        editor.selection = new vscode.Selection(pos, pos);
        editor.revealRange(new vscode.Range(pos.translate(-3), pos.translate(25)), vscode.TextEditorRevealType.AtTop);
        await shot('code', 9000);
    });

    // 5. Chain of Command and handlers of a method (Peek Implementations).
    await step('implementations', async () => {
        const def = doc.getText().search(/\bvoid\s+run\s*\(\s*\)/);
        const i = doc.getText().indexOf('run', def + 4);
        const pos = doc.positionAt(i);
        vscode.window.activeTextEditor.selection = new vscode.Selection(pos, pos);
        await vscode.commands.executeCommand('editor.action.peekImplementation');
        await shot('implementations', 7000);
        await vscode.commands.executeCommand('closeReferenceSearch');
        await closeEditors();
    });

    // 6. Completion on a table buffer (fields, methods, methods added by extensions).
    await step('completion', async () => {
        const text = 'class DemoCompletion\n{\n    public void demo()\n    {\n        CustTable custTable = CustTable::find(\'US-001\');\n        custTable.\n    }\n}\n';
        const untitled = await vscode.workspace.openTextDocument({ language: 'xpp', content: text });
        const editor = await vscode.window.showTextDocument(untitled);
        const pos = untitled.positionAt(text.indexOf('custTable.\n') + 'custTable.'.length);
        editor.selection = new vscode.Selection(pos, pos);
        await sleep(2000);
        await vscode.commands.executeCommand('editor.action.triggerSuggest');
        await shot('completion', 5000);
        await vscode.commands.executeCommand('hideSuggestWidget');
        await vscode.commands.executeCommand('workbench.action.revertAndCloseActiveEditor');
    });

    // 7. Entity relationship diagram.
    await step('erd', async () => {
        await vscode.commands.executeCommand('xpp.viewErd', { objectType: 'AxTable', entry: { name: 'CustGroup' } });
        await shot('erd', 9000);
        await closeEditors();
    });

    // 8. View references (clickable).
    await step('references', async () => {
        await vscode.commands.executeCommand('xpp.viewReferences', { objectType: 'AxTable', entry: { name: 'CustGroup' } });
        await shot('references', 7000);
        await closeEditors();
    });

    fs.writeFileSync(path.join(dir, 'step.txt'), 'finished');
    log('finished');
}

module.exports = { run };
