// The solution helpers behind the Project view (no VS Code, no server): node scripts/solution-test.js
const assert = require('assert');
const fs = require('fs');
const os = require('os');
const path = require('path');
const { parseSolution, findSolution, projectReferences, csharpProjectTree, addToSolution, assemblyNameOf } = require(path.join(__dirname, '..', 'out', 'solution.js'));
const { referencedModules } = require(path.join(__dirname, '..', 'out', 'dotnetReferences.js'));

const root = path.join(os.tmpdir(), 'xpp-solution-test');
fs.rmSync(root, { recursive: true, force: true });
const write = (file, text = '') => { fs.mkdirSync(path.dirname(path.join(root, file)), { recursive: true }); fs.writeFileSync(path.join(root, file), text); };
const names = nodes => nodes.map(n => n.children ? `${n.name}/[${names(n.children)}]` : n.name).join(',');

let failures = 0;
async function check(name, fn) {
    try {
        await fn();
        console.log(`PASS ${name}`);
    } catch (e) {
        failures++;
        console.log(`FAIL ${name}: ${e.message}`);
    }
}

(async () => {
    write('Repo/Xpp/Xpp.rnrproj', `<Project><PropertyGroup><Model>m</Model></PropertyGroup>
  <ItemGroup>
    <ProjectReference Include="..\\Lib\\Lib.csproj"><Name>Lib</Name><Project>{1}</Project></ProjectReference>
    <Reference Include="Ext, Version=1.0.0.0, Culture=neutral, PublicKeyToken=null"><Name>Ext</Name><HintPath>..\\libs\\Ext.dll</HintPath></Reference>
  </ItemGroup></Project>`);
    write('Repo/libs/Ext.dll');
    write('Repo/Lib/Lib.csproj', '<Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><AssemblyName>Contoso.Lib</AssemblyName></PropertyGroup></Project>');
    write('Repo/Lib/Greeter.cs');
    write('Repo/Lib/Models/Order.cs');
    write('Repo/Lib/bin/Debug/Lib.dll');
    write('Repo/Lib/obj/x.cs');
    write('Repo/Lib/.vs/x');
    write('Repo/Lib/Tests/Lib.Tests.csproj', '<Project Sdk="Microsoft.NET.Sdk" />');
    write('Repo/Lib/Tests/GreeterTests.cs');
    write('Repo/Old/Old.csproj', `<Project ToolsVersion="15.0"><ItemGroup>
    <Compile Include="Properties\\AssemblyInfo.cs" /><Compile Include="Code\\A.cs" /><Compile Include="..\\Shared\\S.cs"><Link>Shared\\S.cs</Link></Compile>
    <None Include="app.config" /><Compile Include="Gen\\*.cs" /><Folder Include="Empty\\" /></ItemGroup></Project>`);
    write('Repo/Old/NotInProject.cs');
    write('Repo/Repo.slnx', '<Solution>\r\n  <Project Path="Xpp/Xpp.rnrproj" Type="fc65038c-1b2f-41e1-a629-bed71d161fff" />\r\n  <Folder Name="/src/">\r\n    <Project Path="Lib\\Lib.csproj" />\r\n  </Folder>\r\n</Solution>\r\n');
    write('Other/Other.sln', 'Microsoft Visual Studio Solution File, Format Version 12.00\r\n'
        + 'Project("{FC65038C-1B2F-41E1-A629-BED71D161FFF}") = "Xpp", "..\\Repo\\Xpp\\Xpp.rnrproj", "{A}"\r\nEndProject\r\n'
        + 'Project("{2150E333-8FDC-42A3-9474-1A3956D46DE8}") = "Solution Items", "Solution Items", "{B}"\r\nEndProject\r\n'
        + 'Project("{FAE04EC0-301F-11D3-BF4B-00C04F79EFBC}") = "Old", "..\\Repo\\Old\\Old.csproj", "{C}"\r\nEndProject\r\n');

    const xpp = path.join(root, 'Repo', 'Xpp', 'Xpp.rnrproj');
    await check('slnx and sln list their projects, not solution folders', async () => {
        assert.deepStrictEqual(parseSolution(path.join(root, 'Repo', 'Repo.slnx'), fs.readFileSync(path.join(root, 'Repo', 'Repo.slnx'), 'utf8')),
            [xpp, path.join(root, 'Repo', 'Lib', 'Lib.csproj')]);
        assert.deepStrictEqual(parseSolution(path.join(root, 'Other', 'Other.sln'), fs.readFileSync(path.join(root, 'Other', 'Other.sln'), 'utf8')),
            [xpp, path.join(root, 'Repo', 'Old', 'Old.csproj')]);
    });
    await check('the solution that lists the project is found above it', async () => {
        const solution = await findSolution(xpp);
        assert.strictEqual(solution.file, path.join(root, 'Repo', 'Repo.slnx'));
        assert.strictEqual(solution.name, 'Repo');
    });
    await check('project and assembly references are read', async () => {
        assert.deepStrictEqual(projectReferences(xpp, fs.readFileSync(xpp, 'utf8')), [
            { kind: 'project', name: 'Lib', path: path.join(root, 'Repo', 'Lib', 'Lib.csproj'), exists: true },
            { kind: 'assembly', name: 'Ext', path: path.join(root, 'Repo', 'libs', 'Ext.dll'), exists: true },
        ]);
        assert.strictEqual(assemblyNameOf('Lib.csproj', fs.readFileSync(path.join(root, 'Repo', 'Lib', 'Lib.csproj'), 'utf8')), 'Contoso.Lib');
        assert.strictEqual(assemblyNameOf('C:\\x\\Old.csproj', '<Project/>'), 'Old');
    });
    await check('SDK-style projects show their folder without build output or nested projects', async () => {
        assert.strictEqual(names(await csharpProjectTree(path.join(root, 'Repo', 'Lib', 'Lib.csproj'))), 'Models/[Order.cs],Greeter.cs');
    });
    await check('older projects show their items, links and folders', async () => {
        assert.strictEqual(names(await csharpProjectTree(path.join(root, 'Repo', 'Old', 'Old.csproj'))), 'Code/[A.cs],Empty/[],Properties/[AssemblyInfo.cs],Shared/[S.cs],app.config');
    });
    await check('a project is added to an slnx solution once', async () => {
        const slnx = path.join(root, 'Repo', 'Repo.slnx');
        await addToSolution(slnx, path.join(root, 'Repo', 'Old', 'Old.csproj'));
        await addToSolution(slnx, path.join(root, 'Repo', 'Old', 'Old.csproj'));
        const text = fs.readFileSync(slnx, 'utf8');
        assert.strictEqual(text.match(/Old\.csproj/g).length, 1);
        assert.ok(text.includes('  <Project Path="Old\\Old.csproj" />\r\n</Solution>'), text);
    });
    await check('the debugger loads referenced assemblies that have symbols', async () => {
        write('Pkg/bin/Dynamics.AX.Pkg.dll'); write('Pkg/bin/Dynamics.AX.Pkg.pdb');
        write('Pkg/bin/Contoso.Lib.dll'); write('Pkg/bin/Contoso.Lib.pdb'); write('Pkg/bin/NoSymbols.dll');
        assert.deepStrictEqual(await referencedModules(path.join(root, 'Pkg', 'bin')), ['Contoso.Lib.dll']);
    });

    fs.rmSync(root, { recursive: true, force: true });
    console.log(`failures: ${failures}`);
    process.exit(failures ? 1 : 0);
})();
