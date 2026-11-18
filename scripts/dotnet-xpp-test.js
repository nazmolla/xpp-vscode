// X++ and C# together, end to end: node scripts/dotnet-xpp-test.js <exe> <config> <project.rnrproj>
// Creates a C# project from F&O Studio's template (calling X++: CustTable), builds it, adds it as a reference of the X++
// project (.rnrproj item + AxReference), creates an X++ class calling the C# code, builds the model (references are
// built and copied into the package first) and checks it compiles; then removes everything again.
const assert = require('assert');
const { spawn } = require('child_process');
const fs = require('fs');
const os = require('os');
const path = require('path');
const { csharpProjectXml, csharpClassText, buildProject, findMsBuild, runProcess } = require(path.join(__dirname, '..', 'out', 'dotnetReferences.js'));
const { projectReferences } = require(path.join(__dirname, '..', 'out', 'solution.js'));

const [exe, configFile, projectFile] = process.argv.slice(2);
const config = JSON.parse(fs.readFileSync(configFile, 'utf8').replace(/^\uFEFF/, ''));
const model = /<Model>\s*([^<]+?)\s*<\/Model>/.exec(fs.readFileSync(projectFile, 'utf8'))[1];
const projectBackup = fs.readFileSync(projectFile);
const root = path.join(os.tmpdir(), 'xpp-dotnet-xpp-test');
const csName = 'XvsCsLib';
const className = 'XvsTestCallsCSharp';

const child = spawn(exe, ['--config', configFile, '--project', projectFile], { windowsHide: true });
let buffer = '';
const pending = new Map();
let id = 0;
child.stdout.on('data', d => {
    buffer += d.toString();
    const lines = buffer.split('\n');
    buffer = lines.pop();
    for (const line of lines.filter(l => l.trim())) {
        const msg = JSON.parse(line);
        pending.get(msg.id)?.(msg.result);
    }
});
const rpc = (method, params) => new Promise(resolve => { pending.set(++id, resolve); child.stdin.write(JSON.stringify({ jsonrpc: '2.0', id, method, params }) + '\n'); });
const bridge = async (action, body) => { const raw = await rpc('xpp/bridge', { action, body }); return typeof raw === 'string' ? JSON.parse(raw) : raw; };

let failures = 0;
const check = (name, ok, detail) => { if (!ok) failures++; console.log(`${ok ? 'PASS' : 'FAIL'} ${name}${detail ? ': ' + detail : ''}`); };
setTimeout(() => { console.log('TIMEOUT'); child.kill(); process.exit(1); }, 900000);

(async () => {
    await rpc('initialize', {});
    fs.rmSync(root, { recursive: true, force: true });
    const csDir = path.join(root, csName);
    fs.mkdirSync(csDir, { recursive: true });
    const csproj = path.join(csDir, `${csName}.csproj`);
    fs.writeFileSync(csproj, csharpProjectXml(csName, ['ApplicationPlatform', 'ApplicationFoundation', 'ApplicationSuite']));
    fs.writeFileSync(path.join(csDir, 'Greeter.cs'), csharpClassText(csName));
    // C# calling X++.
    fs.writeFileSync(path.join(csDir, 'Customers.cs'), `namespace ${csName} { public static class Customers { public static string Name(string account) { return Dynamics.AX.Application.CustTable.find(account, false).name(); } } }`);
    const log = [];
    try {
        const assembly = await buildProject(csproj, await findMsBuild(), (e, a) => runProcess(e, a, l => log.push(l)));
        check('the C# template builds against Finance and Operations (C# calling X++)', !!assembly, assembly ?? log.slice(-5).join('\n'));

        let r = await bridge('add_reference', { modelName: model, objectType: 'AxReference', objectName: csName, assemblyPath: assembly, projectPath: csproj });
        check('add_reference', r.success, r.message);
        const refs = projectReferences(projectFile, fs.readFileSync(projectFile, 'utf8'));
        const real = p => fs.realpathSync.native(p).toLowerCase();
        check('the .rnrproj references the C# project', refs.some(x => x.kind === 'project' && x.exists && real(x.path) === real(csproj)));
        const listed = await bridge('project_references', {});
        check('the model has the AxReference', listed.modelReferences.includes(csName), JSON.stringify(listed.modelReferences));

        r = await bridge('create_object', {
            objectType: 'AxClass', objectName: className, modelName: model, declaration: `class ${className}\n{\n}`,
            methods: [`public static str greet()\n{\n    return ${csName}.Greeter::Hello('X++');\n}`],
        });
        check('X++ class calling C# created', r.success, r.message);

        r = await bridge('build', { modelName: model });
        const mine = (r.diagnostics ?? []).filter(d => d.objectName === className);
        check('the model builds with the X++ class calling C#', r.success && !mine.some(d => /error/i.test(d.severity)), r.success ? `${mine.length} diagnostic(s) on ${className}` : r.message.slice(0, 1500));
        const bin = path.join(config.ModelStoreFolder, model, 'bin');
        check('the build copied the C# assembly into the package', fs.existsSync(path.join(bin, `${csName}.dll`)) && fs.existsSync(path.join(bin, `${csName}.pdb`)));
        check('Finance and Operations assemblies were not copied', !fs.existsSync(path.join(bin, 'Microsoft.Dynamics.AX.Xpp.Support.dll')) && !fs.existsSync(path.join(bin, 'Dynamics.AX.ApplicationSuite.dll')));
    } catch (err) {
        check('no exception', false, err.stack);
    } finally {
        await bridge('delete_object', { objectType: 'AxClass', objectName: className });
        const removed = await bridge('remove_reference', { modelName: model, objectType: 'AxReference', objectName: csName, name: csName, assemblyName: csName });
        check('remove_reference', removed.success, removed.message);
        const bin = path.join(config.ModelStoreFolder, model, 'bin');
        check('the copied assembly was removed', !fs.existsSync(path.join(bin, `${csName}.dll`)));
        fs.writeFileSync(projectFile, projectBackup);
        // Rebuild without the class, so the package is as before.
        const rebuilt = await bridge('build', { modelName: model });
        check('the model builds again after clean-up', rebuilt.success, rebuilt.success ? '' : rebuilt.message.slice(0, 500));
        fs.rmSync(root, { recursive: true, force: true });
        child.stdin.end();
        child.kill();
        console.log(`failures: ${failures}`);
        process.exit(failures ? 1 : 0);
    }
})();
