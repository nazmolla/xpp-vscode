// Smoke test of the productivity features in a real extension host (standard objects only).
// Run like test/codeIntelSmoke.js; results go to XPP_SMOKE_OUT (default %TEMP%\xpp-productivity.json).
const vscode = require('vscode');
const fs = require('fs');
const os = require('os');
const path = require('path');

const sleep = ms => new Promise(r => setTimeout(r, ms));

async function run() {
    const results = {};
    const out = process.env.XPP_SMOKE_OUT || path.join(os.tmpdir(), 'xpp-productivity.json');
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
        return ['xpp.searchCode', 'xpp.findExamples', 'xpp.addToPrivilege', 'xpp.generateTests', 'xpp.runSnippet']
            .map(c => c + ':' + all.includes(c));
    });

    // Obsolete warnings on a call to a standard obsolete static method / class.
    await check('obsolete diagnostics', async () => {
        const code = 'class DemoObsolete\n{\n    public static void main(Args _args)\n    {\n        AccountingDistributeEquallyNegativeAllocationFactorsWarningFlight flight;\n    }\n}\n';
        const doc = await vscode.workspace.openTextDocument({ language: 'xpp', content: code });
        await vscode.window.showTextDocument(doc);
        for (let i = 0; i < 40; i++) {
            const d = vscode.languages.getDiagnostics(doc.uri).filter(x => /obsolete/i.test(x.message));
            if (d.length) return d.map(x => `${x.range.start.line + 1}: ${x.message}`);
            await sleep(1000);
        }
        return 'none';
    });

    // Quick fixes offered on a string literal and on an implements clause.
    await check('quick fixes', async () => {
        const code = 'class DemoQuickFix implements SysPackable\n{\n    public void demo()\n    {\n        info("Hello world");\n    }\n}\n';
        const doc = await vscode.workspace.openTextDocument({ language: 'xpp', content: code });
        await vscode.window.showTextDocument(doc);
        await sleep(1500);
        const at = (text, delta = 1) => { const p = doc.positionAt(code.indexOf(text) + delta); return new vscode.Range(p, p); };
        const onString = await vscode.commands.executeCommand('vscode.executeCodeActionProvider', doc.uri, at('"Hello'));
        const onImplements = await vscode.commands.executeCommand('vscode.executeCodeActionProvider', doc.uri, at('SysPackable'));
        return { string: (onString || []).map(a => a.title), implements: (onImplements || []).map(a => a.title) };
    });

    // Snippets contributed for xpp.
    await check('snippets', async () => {
        const doc = await vscode.workspace.openTextDocument({ language: 'xpp', content: '\n' });
        const editor = await vscode.window.showTextDocument(doc);
        editor.selection = new vscode.Selection(0, 0, 0, 0);
        const list = await vscode.commands.executeCommand('vscode.executeCompletionItemProvider', doc.uri, new vscode.Position(0, 0));
        return (list.items || []).filter(i => i.kind === vscode.CompletionItemKind.Snippet).map(i => typeof i.label === 'string' ? i.label : i.label.label).slice(0, 40);
    });

    // Language server actions behind Search Code and Find Examples (the UI's data).
    await check('search + examples via server', async () => {
        const api = ext.exports;
        return api ? Object.keys(api) : 'no exports';
    });

    results.finished = true;
    save();
}

module.exports = { run };
