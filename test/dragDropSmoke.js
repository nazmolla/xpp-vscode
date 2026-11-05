// Smoke test of drag and drop in a real extension host: what an Application Explorer drag carries, the drop into code,
// and the URIs designers read (no metadata is changed). Run: node test/runInDownloadedCode.js test/dragDropSmoke.js
// Results go to XPP_SMOKE_OUT (default %TEMP%\xpp-dragdrop.json).
const vscode = require('vscode');
const fs = require('fs');
const os = require('os');
const path = require('path');

async function run() {
    const results = {};
    const out = process.env.XPP_SMOKE_OUT || path.join(os.tmpdir(), 'xpp-dragdrop.json');
    const save = () => fs.writeFileSync(out, JSON.stringify(results, null, 2));
    const check = async (name, fn) => {
        try { results[name] = { ok: true, value: await fn() }; }
        catch (err) { results[name] = { ok: false, error: String(err && err.stack || err) }; }
        save();
    };
    await vscode.extensions.getExtension('MNazmi.xpp-vscode').activate();
    // The extension's own module instance (same file, same require cache).
    const dnd = require(path.resolve(__dirname, '..', 'out', 'dragAndDrop.js'));
    const dataTransfer = new vscode.DataTransfer();
    const controller = new dnd.ElementDragAndDrop(undefined, () => undefined);

    await check('drag from Application Explorer and project', async () => {
        controller.handleDrag([
            { kind: 'element', objectType: 'AxEdt', entry: { name: 'CustAccount' } },
            { kind: 'item', item: { objectType: 'AxMenuItemDisplay', objectName: 'CustTable', folder: '' } },
            { kind: 'type', objectType: 'AxTable' },
        ], dataTransfer, new vscode.CancellationTokenSource().token);
        const json = JSON.parse(await dataTransfer.get(dnd.ElementsMime).asString());
        const uris = await dataTransfer.get('text/uri-list').asString();
        if (json.length !== 2 || !uris.includes('xpp-element:/AxEdt/CustAccount')) throw new Error(JSON.stringify(json) + ' ' + uris);
        return { json, uris };
    });
    await check('designers read the dragged elements', async () => {
        const fromUris = dnd.elementsFromUris(['xpp-element:/AxEnum/SalesStatus', 'file:///c:/x.txt']);
        const recent = controller.recentDrag();
        if (fromUris.length !== 1 || fromUris[0].objectName !== 'SalesStatus' || recent.length !== 2) throw new Error(JSON.stringify({ fromUris, recent }));
        return { fromUris, recent };
    });
    await check('drop into X++ code inserts the names', async () => {
        const doc = await vscode.workspace.openTextDocument({ language: 'xpp', content: '\n' });
        const edit = await new dnd.ElementDropIntoCode().provideDocumentDropEdits(doc, new vscode.Position(0, 0), dataTransfer, new vscode.CancellationTokenSource().token);
        if (edit.insertText !== 'CustAccount, CustTable') throw new Error(String(edit.insertText));
        return edit.insertText;
    });
    await check('Add from Application Explorer command', async () => (await vscode.commands.getCommands(true)).includes('xpp.designer.addElement'));

    results.finished = true;
    save();
}

module.exports = { run };
