// End-to-end smoke test of the X++ code intelligence providers in a real VS Code extension host.
// Run: code --extensionDevelopmentPath=<repo> --extensionTestsPath=<repo>/test/codeIntelSmoke.js --user-data-dir=<temp> --disable-extensions
// Results are written to the file named by XPP_SMOKE_OUT (default %TEMP%\xpp-smoke.json).
const vscode = require('vscode');
const fs = require('fs');
const os = require('os');
const path = require('path');

const objectUri = (type, name) => vscode.Uri.from({ scheme: 'xpp', path: `/${type}/${name}.xpp` });
const sleep = ms => new Promise(r => setTimeout(r, ms));

async function untitled(content) {
    const doc = await vscode.workspace.openTextDocument({ language: 'xpp', content });
    await vscode.window.showTextDocument(doc);
    return doc;
}

function positionOf(doc, text, offsetInText = 0) {
    const i = text instanceof RegExp ? doc.getText().search(text) : doc.getText().indexOf(text);
    if (i < 0) throw new Error(`'${text}' not found`);
    return doc.positionAt(i + offsetInText);
}

async function run() {
    const results = {};
    const out = process.env.XPP_SMOKE_OUT || path.join(os.tmpdir(), 'xpp-smoke.json');
    const save = () => fs.writeFileSync(out, JSON.stringify(results, null, 2));
    const check = async (name, fn) => {
        const t0 = Date.now();
        try {
            results[name] = { ok: true, value: await fn(), ms: Date.now() - t0 };
        } catch (err) {
            results[name] = { ok: false, error: String(err && err.stack || err), ms: Date.now() - t0 };
        }
        save();
    };
    const ext = vscode.extensions.getExtension('MNazmi.xpp-vscode');
    await ext.activate();

    let sfl;
    await check('open SalesFormLetter', async () => {
        for (let i = 0; i < 10; i++) {
            try {
                sfl = await vscode.workspace.openTextDocument(objectUri('AxClass', 'SalesFormLetter'));
                if (sfl.getText().length > 0) break;
            } catch (err) {
                if (i === 9) throw err;
            }
            await sleep(3000);
        }
        await vscode.window.showTextDocument(sfl);
        return { length: sfl.getText().length, head: sfl.getText().slice(0, 80) };
    });
    // Give the language server time to build the extension index (cached on disk after the first run).
    await vscode.commands.executeCommand('xpp.codeIntel.rebuildIndex');
    await sleep(8000);

    await check('outline', async () => {
        const symbols = await vscode.commands.executeCommand('vscode.executeDocumentSymbolProvider', sfl.uri);
        const root = symbols && symbols[0];
        return { root: root && root.name, children: root ? root.children.length : 0, sample: root ? root.children.slice(0, 3).map(c => c.name) : [] };
    });
    const runMatch = /void\s+run\s*\(/.exec(sfl.getText());
    const runAt = sfl.positionAt(runMatch.index + runMatch[0].indexOf('run') + 1);
    await check('implementations(SalesFormLetter.run)', async () => {
        const locs = await vscode.commands.executeCommand('vscode.executeImplementationProvider', sfl.uri, runAt);
        return { count: locs.length, sample: locs.slice(0, 4).map(l => (l.uri || l.targetUri).path) };
    });
    await check('codeLens(SalesFormLetter)', async () => {
        const lenses = await vscode.commands.executeCommand('vscode.executeCodeLensProvider', sfl.uri, 50);
        return { count: lenses.length, sample: lenses.filter(l => l.command).slice(0, 6).map(l => l.command.title) };
    });
    await check('callHierarchy(in, run)', async () => {
        const items = await vscode.commands.executeCommand('vscode.prepareCallHierarchy', sfl.uri, runAt);
        if (!items || !items.length) return { prepared: 0 };
        const incoming = await vscode.commands.executeCommand('vscode.provideIncomingCalls', items[0]);
        const outgoing = await vscode.commands.executeCommand('vscode.provideOutgoingCalls', items[0]);
        return { item: items[0].name + ' ' + items[0].detail, incoming: incoming.length, outgoing: outgoing.length, sampleIn: incoming.slice(0, 2).map(c => c.from.detail + '.' + c.from.name) };
    });
    await check('typeHierarchy(subtypes)', async () => {
        const items = await vscode.commands.executeCommand('vscode.prepareTypeHierarchy', sfl.uri, positionOf(sfl, 'SalesFormLetter', 1));
        if (!items || !items.length) return { prepared: 0 };
        const sub = await vscode.commands.executeCommand('vscode.provideSubtypes', items[0]);
        const sup = await vscode.commands.executeCommand('vscode.provideSupertypes', items[0]);
        return { item: items[0].name, subtypes: sub.length, supertypes: sup.map(s => s.name) };
    });
    await check('workspaceSymbols(SalesFormLetter.ru)', async () => {
        const syms = await vscode.commands.executeCommand('vscode.executeWorkspaceSymbolProvider', 'SalesFormLetter.ru');
        return { count: syms.length, sample: syms.slice(0, 3).map(s => s.containerName + '.' + s.name) };
    });

    const code = [
        'public void probe()',
        '{',
        '    CustTable custTable;',
        '    SalesFormLetter letter;',
        '    str s = "@SYS7149";',
        '    custTable.',
        '    CustTable::find(',
        '    letter.',
        '    NoYes x = NoYes::',
        '    s = "@XvsRem";',
        '}',
    ].join('\n');
    const doc = await untitled(code);
    await check('completion(custTable.)', async () => {
        const list = await vscode.commands.executeCommand('vscode.executeCompletionItemProvider', doc.uri, positionOf(doc, 'custTable.\n', 10), '.');
        const ours = list.items.filter(i => i.kind !== vscode.CompletionItemKind.Text);
        const labels = ours.map(i => typeof i.label === 'string' ? i.label : i.label.label);
        return { count: labels.length, hasAccountNum: labels.includes('AccountNum'), hasInsert: labels.includes('insert'), hasFind: labels.includes('find') };
    });
    await check('completion(CustTable::)', async () => {
        const list = await vscode.commands.executeCommand('vscode.executeCompletionItemProvider', doc.uri, positionOf(doc, 'CustTable::find', 11), ':');
        const ours = list.items.filter(i => i.kind !== vscode.CompletionItemKind.Text);
        const labels = ours.map(i => typeof i.label === 'string' ? i.label : i.label.label);
        return { count: labels.length, hasFind: labels.includes('find'), hasAccountNum: labels.includes('AccountNum') };
    });
    await check('completion(letter. inherited + extension methods)', async () => {
        const list = await vscode.commands.executeCommand('vscode.executeCompletionItemProvider', doc.uri, positionOf(doc, 'letter.\n', 7), '.');
        const items = list.items.filter(i => i.kind !== vscode.CompletionItemKind.Text).map(i => ({ label: typeof i.label === 'string' ? i.label : i.label.label, owner: typeof i.label === 'string' ? '' : i.label.description }));
        return { count: items.length, owners: [...new Set(items.map(i => i.owner))].slice(0, 8) };
    });
    await check('completion(labels "@XvsRem)', async () => {
        const list = await vscode.commands.executeCommand('vscode.executeCompletionItemProvider', doc.uri, positionOf(doc, '"@XvsRem', 7), '@');
        const labelItems = list.items.filter(i => typeof i.label !== 'string' && String(i.label.label).startsWith('@'));
        return { count: labelItems.length, sample: labelItems.slice(0, 3).map(i => typeof i.label === 'string' ? i.label : i.label.label + ' ' + i.label.description) };
    });
    await check('signatureHelp(CustTable::find()', async () => {
        const help = await vscode.commands.executeCommand('vscode.executeSignatureHelpProvider', doc.uri, positionOf(doc, 'CustTable::find(', 16), '(');
        return help ? { signature: help.signatures[0] && help.signatures[0].label, params: help.signatures[0] && help.signatures[0].parameters.length } : null;
    });
    await check('hover(@SYS7149)', async () => {
        const hovers = await vscode.commands.executeCommand('vscode.executeHoverProvider', doc.uri, positionOf(doc, '@SYS7149', 3));
        return hovers.map(h => h.contents.map(c => c.value || c).join(' ')).join(' | ');
    });
    await check('hover(CustTable)', async () => {
        const hovers = await vscode.commands.executeCommand('vscode.executeHoverProvider', doc.uri, positionOf(doc, 'CustTable custTable', 2));
        return hovers.map(h => h.contents.map(c => c.value || c).join(' ')).join(' | ');
    });

    const bad = await untitled('public void broken()\n{\n    int i = 1\n    info("x");\n}\n');
    await check('syntaxCheck', async () => {
        for (let i = 0; i < 20; i++) {
            const d = vscode.languages.getDiagnostics(bad.uri).filter(x => x.source === 'X++ syntax');
            if (d.length) return d.map(x => `${x.range.start.line + 1}:${x.range.start.character + 1} ${x.message}`);
            await sleep(500);
        }
        return 'no diagnostics';
    });

    const messy = await untitled('void m(){\nif(a){\ninfo("x");\n} else {\nb();\n}\n}\n');
    await check('formatting', async () => {
        const edits = await vscode.commands.executeCommand('vscode.executeFormatDocumentProvider', messy.uri, { tabSize: 4, insertSpaces: true });
        if (!edits || !edits.length) return 'no edits';
        const we = new vscode.WorkspaceEdit();
        we.set(messy.uri, edits);
        await vscode.workspace.applyEdit(we);
        return messy.getText();
    });

    await check('toggle(codeLens off)', async () => {
        await vscode.workspace.getConfiguration('xpp.codeIntelligence').update('codeLens', false, vscode.ConfigurationTarget.Global);
        const lenses = await vscode.commands.executeCommand('vscode.executeCodeLensProvider', sfl.uri, 50);
        await vscode.workspace.getConfiguration('xpp.codeIntelligence').update('codeLens', undefined, vscode.ConfigurationTarget.Global);
        return { lensesWhenOff: lenses.length };
    });
    save();
}

module.exports = { run: () => run().catch(err => {
    const out = process.env.XPP_SMOKE_OUT || path.join(os.tmpdir(), 'xpp-smoke.json');
    let prior = {};
    try { prior = JSON.parse(fs.readFileSync(out, 'utf8')); } catch { }
    prior.fatal = String(err && err.stack || err);
    fs.writeFileSync(out, JSON.stringify(prior, null, 2));
}) };
