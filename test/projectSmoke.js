// Project workspaces in a real extension host: node test/runInDownloadedCode.js test/projectSmoke.js
// Without XPP_TEST_WORKSPACE: an old user-level xpp.project is not opened at startup (it becomes a recent project), and the
// project workspace file has the project and model folders. With XPP_TEST_WORKSPACE (the runner opens that workspace):
// the window has the project and both folders. Results go to XPP_SMOKE_OUT (default %TEMP%\xpp-project.json).
const vscode = require('vscode');
const fs = require('fs');
const os = require('os');
const path = require('path');

const ProjectFile = 'C:\\Users\\Mohamed Nazmi\\source\\repos\\TestProj\\TestProj.rnrproj';

async function run() {
    const results = {};
    const out = process.env.XPP_SMOKE_OUT || path.join(os.tmpdir(), 'xpp-project.json');
    const save = () => fs.writeFileSync(out, JSON.stringify(results, null, 2));
    const check = async (name, fn) => {
        try { results[name] = { ok: true, value: await fn() }; }
        catch (err) { results[name] = { ok: false, error: String(err && err.stack || err) }; }
        save();
    };
    const workspace = process.env.XPP_TEST_WORKSPACE;
    const config = () => vscode.workspace.getConfiguration('xpp');

    // Before the run, put what earlier versions left behind into the test profile's user settings:
    // "xpp.project": the TestProj path (xpp-test-electron-ud\User\settings.json).
    await vscode.extensions.getExtension('MNazmi.xpp-vscode').activate();
    const project = require(path.resolve(__dirname, '..', 'out', 'project.js'));

    if (!workspace) {
        await check('startup opens no project, even with an old user setting', async () => {
            for (let i = 0; i < 20 && config().inspect('project').globalValue; i++) await new Promise(r => setTimeout(r, 250));
            const active = await project.findActiveProject();
            const global = config().inspect('project').globalValue;
            if (active || global) throw new Error(`active=${active} global=${global}`);
            return 'no project';
        });
        await check('the project workspace has the project and model folders', async () => {
            const state = new Map();
            const storage = vscode.Uri.file(path.join(os.tmpdir(), 'xpp-project-test'));
            const context = { globalState: { get: (k, d) => state.has(k) ? state.get(k) : d, update: async (k, v) => { state.set(k, v); } }, globalStorageUri: storage };
            const workspaces = new project.ProjectWorkspaces(context);
            const uri = await workspaces.prepare(ProjectFile);
            const content = JSON.parse(fs.readFileSync(uri.fsPath, 'utf8'));
            if (content.settings['xpp.project'] !== ProjectFile || content.folders.length !== 2
                || content.folders[0].path !== path.dirname(ProjectFile) || !/testcust/i.test(content.folders[1].path)) throw new Error(JSON.stringify(content));
            await workspaces.remember(ProjectFile);
            await workspaces.remember('C:\\missing\\Gone.rnrproj');
            await workspaces.remember(ProjectFile);
            const recent = await workspaces.recent();
            if (recent.length !== 1 || recent[0] !== ProjectFile) throw new Error(JSON.stringify(recent));
            fs.writeFileSync(path.join(os.tmpdir(), 'xpp-project-workspace.txt'), uri.fsPath);
            return { workspace: uri.fsPath, content, recent };
        });
        await check('Open Recent and Close Project are registered', async () => {
            const commands = await vscode.commands.getCommands(true);
            for (const c of ['xpp.openRecentProject', 'xpp.closeProject', 'xpp.openProject', 'xpp.selectProject'])
                if (!commands.includes(c)) throw new Error('missing ' + c);
            return true;
        });
    } else {
        await check('the project workspace opens the project and both folders', async () => {
            const active = await project.findActiveProject();
            const folders = (vscode.workspace.workspaceFolders ?? []).map(f => `${f.name} = ${f.uri.fsPath}`);
            if (active !== ProjectFile || folders.length !== 2) throw new Error(JSON.stringify({ active, folders }));
            return { active, folders };
        });
    }
    results.finished = true;
    save();
}

module.exports = { run };
