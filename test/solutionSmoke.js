// The Project view as a solution (X++ project, references, C# projects and their files) in a real extension host:
// node scripts/run-smoke.js solutionSmoke. Builds a temporary solution around a copy of TestProj. Results go to
// XPP_SMOKE_OUT (default %TEMP%\xpp-solution.json).
const vscode = require('vscode');
const fs = require('fs');
const os = require('os');
const path = require('path');

const TestProject = 'C:\\Users\\Mohamed Nazmi\\source\\repos\\TestProj\\TestProj.rnrproj';

async function run() {
    const results = {};
    const out = process.env.XPP_SMOKE_OUT || path.join(os.tmpdir(), 'xpp-solution.json');
    const save = () => fs.writeFileSync(out, JSON.stringify(results, null, 2));
    const check = async (name, fn) => {
        try { results[name] = { ok: true, value: await fn() }; }
        catch (err) { results[name] = { ok: false, error: String(err && err.stack || err) }; }
        save();
    };
    await vscode.extensions.getExtension('MNazmi.xpp-vscode').activate();
    const project = require(path.resolve(__dirname, '..', 'out', 'project.js'));

    const root = path.join(os.tmpdir(), 'xpp-solution-smoke');
    fs.rmSync(root, { recursive: true, force: true });
    const write = (file, text = '') => { fs.mkdirSync(path.dirname(path.join(root, file)), { recursive: true }); fs.writeFileSync(path.join(root, file), text); };
    const xpp = path.join(root, 'Xpp', 'Xpp.rnrproj');
    write('Xpp/Xpp.rnrproj', fs.readFileSync(TestProject, 'utf8').replace('<Import Project="$(MSBuildBinPath)\\Microsoft.Common.targets" />',
        '<ItemGroup>\r\n    <ProjectReference Include="..\\Lib\\Lib.csproj">\r\n      <Name>Lib</Name>\r\n    </ProjectReference>\r\n  </ItemGroup>\r\n  <Import Project="$(MSBuildBinPath)\\Microsoft.Common.targets" />'));
    write('Other/Other.rnrproj', fs.readFileSync(TestProject, 'utf8'));
    write('Lib/Lib.csproj', '<Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><TargetFramework>net48</TargetFramework></PropertyGroup></Project>');
    write('Lib/Greeter.cs', 'namespace Lib { public static class Greeter { } }');
    write('Lib/Models/Order.cs', 'namespace Lib.Models { public class Order { } }');
    write('Lib/bin/Debug/Lib.dll');
    write('Unreferenced/Unreferenced.csproj', '<Project Sdk="Microsoft.NET.Sdk" />');
    write('Repo.slnx', '<Solution>\r\n  <Project Path="Xpp/Xpp.rnrproj" />\r\n  <Project Path="Other/Other.rnrproj" />\r\n  <Project Path="Unreferenced/Unreferenced.csproj" />\r\n</Solution>\r\n');

    const tree = new project.ProjectTreeProvider();
    const xppNode = { kind: 'xppProject', file: xpp, name: 'Xpp', model: 'testcust', active: true };
    await check('the X++ project shows References first, then its folders', async () => {
        const item = tree.getTreeItem(xppNode);
        if (item.collapsibleState !== vscode.TreeItemCollapsibleState.Expanded || item.contextValue !== 'xppSolutionProject active') throw new Error(JSON.stringify(item));
        const children = await tree.getChildren(xppNode);
        if (children[0].kind !== 'references' || !children.slice(1).every(c => c.kind === 'folder' && c.active)) throw new Error(JSON.stringify(children.map(c => c.kind)));
        return children.map(c => c.kind === 'folder' ? c.folder : c.kind);
    });
    await check('References lists the C# project', async () => {
        const refs = await tree.getChildren({ kind: 'references', project: xpp });
        const item = tree.getTreeItem(refs[0]);
        if (refs.length !== 1 || item.label !== 'Lib' || item.description !== 'C# project' || item.command?.command !== 'vscode.open') throw new Error(JSON.stringify({ refs, item }));
        return item.description;
    });
    await check('the solution lists the referenced and unreferenced C# projects', async () => {
        const { solution, projects } = await project.solutionCsharpProjects(xpp);
        const shown = projects.map(p => `${path.basename(p.file)}:${p.inSolution}`).sort();
        if (!solution || JSON.stringify(shown) !== JSON.stringify(['Lib.csproj:false', 'Unreferenced.csproj:true'])) throw new Error(JSON.stringify(shown));
        return shown;
    });
    await check('a C# project shows its files, which open in the editor', async () => {
        const node = { kind: 'csProject', file: path.join(root, 'Lib', 'Lib.csproj'), name: 'Lib', inSolution: false };
        if (tree.getTreeItem(node).description !== 'C# · not in the solution') throw new Error(tree.getTreeItem(node).description);
        const files = await tree.getChildren(node);
        const labels = files.map(f => tree.getTreeItem(f).label);
        if (JSON.stringify(labels) !== JSON.stringify(['Models', 'Greeter.cs'])) throw new Error(JSON.stringify(labels));
        const greeter = tree.getTreeItem(files[1]);
        if (greeter.command?.command !== 'vscode.open' || greeter.resourceUri.fsPath.toLowerCase() !== path.join(root, 'Lib', 'Greeter.cs').toLowerCase()) throw new Error(JSON.stringify(greeter));
        await vscode.commands.executeCommand(greeter.command.command, ...greeter.command.arguments);
        if (vscode.window.activeTextEditor?.document.uri.fsPath.toLowerCase() !== greeter.resourceUri.fsPath.toLowerCase()) throw new Error('not opened');
        return labels;
    });
    await check('elements of another X++ project cannot be excluded from the open one', async () => {
        const other = { kind: 'xppProject', file: path.join(root, 'Other', 'Other.rnrproj'), name: 'Other', model: 'testcust', active: false };
        if (tree.getTreeItem(other).description !== 'testcust · not open') throw new Error(tree.getTreeItem(other).description);
        const folders = await tree.getChildren(other);
        if (!folders.length || folders.some(f => f.kind !== 'folder' || f.active)) return 'no elements';
        const items = await tree.getChildren(folders[0]);
        const context = tree.getTreeItem(items[0]).contextValue;
        if (!/otherProject/.test(context)) throw new Error(context);
        return context;
    });
    await check('the project workspace has the C# projects as folders', async () => {
        const state = new Map();
        const context = { globalState: { get: (k, d) => state.has(k) ? state.get(k) : d, update: async (k, v) => { state.set(k, v); } }, globalStorageUri: vscode.Uri.file(path.join(root, 'storage')) };
        const uri = await new project.ProjectWorkspaces(context).prepare(xpp);
        const folders = JSON.parse(fs.readFileSync(uri.fsPath, 'utf8')).folders.map(f => f.name);
        if (!folders.includes('Lib (C#)') || !folders.includes('Unreferenced (C#)')) throw new Error(JSON.stringify(folders));
        return folders;
    });
    await check('reference commands are registered', async () => {
        const commands = await vscode.commands.getCommands(true);
        for (const c of ['xpp.addReference', 'xpp.removeReference', 'xpp.newCSharpProject', 'xpp.buildCSharpProject', 'xpp.openThisProject', 'xpp.openProjectFile'])
            if (!commands.includes(c)) throw new Error('missing ' + c);
        return true;
    });
    tree.dispose();
    await vscode.commands.executeCommand('workbench.action.closeAllEditors');
    fs.rmSync(root, { recursive: true, force: true });
    results.finished = true;
    save();
}

module.exports = { run };
