// Smoke test of the designer's right-click Go To and the other context-menu actions in a real extension host
// (standard objects only, nothing is changed). Run: node test/runInDownloadedCode.js test/goToSmoke.js
// Results go to XPP_SMOKE_OUT (default %TEMP%\xpp-goto.json).
const vscode = require('vscode');
const fs = require('fs');
const os = require('os');
const path = require('path');

const sleep = ms => new Promise(r => setTimeout(r, ms));
const tabs = () => vscode.window.tabGroups.all.flatMap(g => g.tabs.map(t => t.label));

async function waitForTab(label, ms = 60000) {
    for (let waited = 0; waited < ms; waited += 500) {
        if (tabs().some(t => t.startsWith(label))) return true;
        await sleep(500);
    }
    return false;
}

async function run() {
    const results = {};
    const out = process.env.XPP_SMOKE_OUT || path.join(os.tmpdir(), 'xpp-goto.json');
    const save = () => fs.writeFileSync(out, JSON.stringify(results, null, 2));
    const check = async (name, fn) => {
        const t0 = Date.now();
        try { results[name] = { ok: true, value: await fn(), ms: Date.now() - t0 }; }
        catch (err) { results[name] = { ok: false, error: String(err && err.stack || err), ms: Date.now() - t0 }; }
        save();
    };
    await vscode.extensions.getExtension('MNazmi.xpp-vscode').activate();
    await sleep(3000);

    await check('commands registered', async () => {
        const all = new Set(await vscode.commands.getCommands(true));
        const wanted = ['xpp.goTo.ExtendedDataType', 'xpp.goTo.EnumType', 'xpp.goTo.BaseObject', 'xpp.goTo.ActionMenuItem', 'xpp.goTo.Label', 'xpp.goTo.referenced',
            'xpp.openReference', 'xpp.designer.rename', 'xpp.designer.duplicate', 'xpp.designer.moveUp', 'xpp.designer.moveDown',
            'xpp.duplicateObject', 'xpp.copyName', 'xpp.copyReference', 'xpp.openDesignerToSide', 'xpp.revealInAot'];
        const missing = wanted.filter(c => !all.has(c));
        if (missing.length) throw new Error('missing ' + missing.join(', '));
        return wanted.length;
    });

    // What a right-click on the field CustTable.AccountNum passes: Go to Extended Data Type opens the EDT's designer.
    await check('go to EDT from a field node', async () => {
        await vscode.commands.executeCommand('xpp.goTo.ExtendedDataType', { xppRefs: JSON.stringify({ ExtendedDataType: { objectType: 'AxEdt', objectName: 'CustAccount' } }) });
        if (!await waitForTab('CustAccount')) throw new Error('no designer tab; tabs: ' + tabs().join(' | '));
        return tabs().filter(t => t.startsWith('CustAccount'));
    });

    await check('go to base object from an extension in the Application Explorer', async () => {
        await vscode.commands.executeCommand('xpp.goTo.BaseObject', { objectType: 'AxTableExtension', entry: { name: 'CustTable.RevRec' } });
        if (!await waitForTab('CustTable')) throw new Error('no designer tab; tabs: ' + tabs().join(' | '));
        return tabs().filter(t => t.startsWith('CustTable'));
    });

    await check('go to label opens the label file', async () => {
        await vscode.commands.executeCommand('xpp.goTo.Label', { xppRefs: JSON.stringify({ Label: { objectType: 'AxLabelFile', objectName: 'SCM', labelId: 'SalesStatusLabel' } }) });
        if (!await waitForTab('SCM')) throw new Error('no label tab; tabs: ' + tabs().join(' | '));
        return tabs().filter(t => t.startsWith('SCM'));
    });

    await check('code links: intrinsic functions and labels', async () => {
        const code = 'class DemoLinks\n{\n    public void demo()\n    {\n        str t = tableStr(CustGroup);\n        str m = menuItemDisplayStr(CustTable);\n        info("@SYS12345");\n    }\n}\n';
        const doc = await vscode.workspace.openTextDocument({ language: 'xpp', content: code });
        const links = await vscode.commands.executeCommand('vscode.executeLinkProvider', doc.uri);
        return links.map(l => `${doc.getText(l.range)} -> ${decodeURIComponent(l.target.query)}`);
    });

    await check('open reference resolves the element type', async () => {
        await vscode.commands.executeCommand('xpp.openReference', ['AxTable', 'AxView'], 'CustGroup');
        if (!await waitForTab('CustGroup')) throw new Error('no designer tab; tabs: ' + tabs().join(' | '));
        return tabs().filter(t => t.startsWith('CustGroup'));
    });

    await check('copy name and X++ reference', async () => {
        await vscode.commands.executeCommand('xpp.copyReference', { xppIntrinsic: 'fieldStr(CustTable, AccountNum)' });
        const fromNode = await vscode.env.clipboard.readText();
        await vscode.commands.executeCommand('xpp.copyReference', { objectType: 'AxMenuItemDisplay', entry: { name: 'CustTable' } });
        const fromTree = await vscode.env.clipboard.readText();
        await vscode.commands.executeCommand('xpp.copyName', { objectType: 'AxTable', entry: { name: 'CustGroup' } });
        return [fromNode, fromTree, await vscode.env.clipboard.readText()];
    });

    await check('locate in Application Explorer', async () => {
        await vscode.commands.executeCommand('xpp.revealInAot', { item: { objectType: 'AxEnum', objectName: 'SalesStatus' } });
        return 'no error';
    });

    results.finished = true;
    save();
}

module.exports = { run };
