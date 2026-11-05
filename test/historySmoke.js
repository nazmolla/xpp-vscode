// Smoke test of View History's versions in a real extension host: a throw-away Git repository with two commits of a
// class, read back through the xpphistory: documents as X++ code and as metadata. Nothing outside %TEMP% is touched.
// Run: node test/runInDownloadedCode.js test/historySmoke.js; results go to XPP_SMOKE_OUT (default %TEMP%\xpp-history.json).
const vscode = require('vscode');
const fs = require('fs');
const os = require('os');
const path = require('path');
const { execFileSync } = require('child_process');

const classXml = body => `<?xml version="1.0" encoding="utf-8"?>
<AxClass xmlns:i="http://www.w3.org/2001/XMLSchema-instance">
\t<Name>XvsHistory</Name>
\t<SourceCode>
\t\t<Declaration><![CDATA[
public class XvsHistory
{
}
]]></Declaration>
\t\t<Methods>
\t\t\t<Method>
\t\t\t\t<Name>run</Name>
\t\t\t\t<Source><![CDATA[
    public void run()
    {
        info("${body}");
    }

]]></Source>
\t\t\t</Method>
\t\t</Methods>
\t</SourceCode>
</AxClass>
`;

async function run() {
    const results = {};
    const out = process.env.XPP_SMOKE_OUT || path.join(os.tmpdir(), 'xpp-history.json');
    const save = () => fs.writeFileSync(out, JSON.stringify(results, null, 2));
    const check = async (name, fn) => {
        try { results[name] = { ok: true, value: await fn() }; }
        catch (err) { results[name] = { ok: false, error: String(err && err.stack || err) }; }
        save();
    };
    await vscode.extensions.getExtension('MNazmi.xpp-vscode').activate();

    const repo = fs.mkdtempSync(path.join(os.tmpdir(), 'xpp-history-'));
    const git = (...args) => execFileSync('git', args, { cwd: repo, encoding: 'utf8' }).trim();
    const relative = 'Model/AxClass/XvsHistory.xml';
    fs.mkdirSync(path.join(repo, 'Model', 'AxClass'), { recursive: true });
    git('init', '-q');
    git('config', 'user.email', 'test@example.com');
    git('config', 'user.name', 'Test');
    fs.writeFileSync(path.join(repo, relative), classXml('v1'));
    git('add', '.');
    git('commit', '-q', '-m', 'first');
    fs.writeFileSync(path.join(repo, relative), classXml('v2'));
    git('commit', '-q', '-am', 'second');
    const [second, first] = git('log', '--format=%H').split('\n');
    const read = async (ref, view) => (await vscode.workspace.openTextDocument(
        vscode.Uri.from({ scheme: 'xpphistory', path: `/${ref}/${relative}`, query: repo, fragment: view }))).getText();

    await check('command registered', async () => (await vscode.commands.getCommands(true)).includes('xpp.viewHistory'));
    await check('code of each version', async () => {
        const v1 = await read(first, 'code');
        const v2 = await read(second, 'code');
        if (!v1.includes('info("v1")') || !v2.includes('info("v2")') || !v2.startsWith('public class XvsHistory')) throw new Error(v1 + '\n---\n' + v2);
        return v2;
    });
    await check('metadata of a version', async () => {
        const xml = await read(first, 'xml');
        if (!xml.includes('<AxClass')) throw new Error(xml.slice(0, 200));
        return xml.length;
    });
    await check('before the first commit is empty', async () => (await read('empty', 'code')) === '');

    // Git may still hold files of the throw-away repository; leftovers in %TEMP% are harmless.
    try { fs.rmSync(repo, { recursive: true, force: true }); } catch { }
    results.finished = true;
    save();
}

module.exports = { run };
