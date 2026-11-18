// Builds a temporary solution (an X++ project referencing C# projects and an assembly) and checks the files the build
// copies into the package's bin folder, as Visual Studio does; also checks junctioned packages. Needs MSBuild.
// node scripts/references-test.js
const assert = require('assert');
const { execFileSync } = require('child_process');
const fs = require('fs');
const os = require('os');
const path = require('path');
const { prepareProjectReferences, findMsBuild } = require(path.join(__dirname, '..', 'out', 'dotnetReferences.js'));
const { packageFolders } = require(path.join(__dirname, '..', 'out', 'packageStore.js'));

const root = path.join(os.tmpdir(), 'xpp-references-test');
fs.rmSync(root, { recursive: true, force: true });
const write = (file, text) => { fs.mkdirSync(path.dirname(path.join(root, file)), { recursive: true }); fs.writeFileSync(path.join(root, file), text); };

const csproj = (name, extra = '') => `<?xml version="1.0" encoding="utf-8"?>
<Project ToolsVersion="15.0" xmlns="http://schemas.microsoft.com/developer/msbuild/2003">
  <PropertyGroup>
    <Configuration Condition=" '$(Configuration)' == '' ">Debug</Configuration>
    <Platform Condition=" '$(Platform)' == '' ">AnyCPU</Platform>
    <OutputType>Library</OutputType>
    <AssemblyName>${name}</AssemblyName>
    <TargetFrameworkVersion>v4.8</TargetFrameworkVersion>
    <OutputPath>bin\\Debug\\</OutputPath>
    <DebugSymbols>true</DebugSymbols>
    <DebugType>full</DebugType>
  </PropertyGroup>
  <ItemGroup><Reference Include="System" /><Compile Include="Code.cs" /></ItemGroup>
  ${extra}
  <Import Project="$(MSBuildToolsPath)\\Microsoft.CSharp.targets" />
</Project>
`;
const resx = text => `<?xml version="1.0" encoding="utf-8"?>
<root>
  <resheader name="resmimetype"><value>text/microsoft-resx</value></resheader>
  <resheader name="version"><value>2.0</value></resheader>
  <resheader name="reader"><value>System.Resources.ResXResourceReader, System.Windows.Forms, Version=4.0.0.0, Culture=neutral, PublicKeyToken=b77a5c561934e089</value></resheader>
  <resheader name="writer"><value>System.Resources.ResXResourceWriter, System.Windows.Forms, Version=4.0.0.0, Culture=neutral, PublicKeyToken=b77a5c561934e089</value></resheader>
  <data name="Hello" xml:space="preserve"><value>${text}</value></data>
</root>
`;

write('Dep/Dep.csproj', csproj('XvsDep'));
write('Dep/Code.cs', 'namespace XvsDep { public class Dep { public static string Hi() { return "hi"; } } }');
write('Lib/Lib.csproj', csproj('XvsLib', `<PropertyGroup><DocumentationFile>bin\\Debug\\XvsLib.xml</DocumentationFile><NoWarn>1591</NoWarn></PropertyGroup>
  <ItemGroup>
    <EmbeddedResource Include="Strings.resx" /><EmbeddedResource Include="Strings.fr.resx" />
    <Content Include="settings.json"><CopyToOutputDirectory>PreserveNewest</CopyToOutputDirectory></Content>
    <ProjectReference Include="..\\Dep\\Dep.csproj"><Name>XvsDep</Name></ProjectReference>
  </ItemGroup>`));
write('Lib/Code.cs', 'namespace XvsLib { public class Greeter { public static string Hi() { return XvsDep.Dep.Hi(); } } }');
write('Lib/Strings.resx', resx('Hello'));
write('Lib/Strings.fr.resx', resx('Bonjour'));
write('Lib/settings.json', '{}');
write('Bin/Bin.csproj', csproj('XvsBinary'));
write('Bin/Code.cs', 'namespace XvsBinary { public class B { } }');
write('XppProject/XppProject.rnrproj', `<?xml version="1.0" encoding="utf-8"?>
<Project ToolsVersion="14.0" DefaultTargets="Build" xmlns="http://schemas.microsoft.com/developer/msbuild/2003">
  <PropertyGroup>
    <Configuration Condition=" '$(Configuration)' == '' ">Debug</Configuration>
    <Platform Condition=" '$(Platform)' == '' ">AnyCPU</Platform>
    <BuildTasksDirectory Condition=" '$(BuildTasksDirectory)' == ''">$(MSBuildProgramFiles32)\\MSBuild\\Microsoft\\Dynamics\\AX</BuildTasksDirectory>
    <Model>XvsModel</Model>
    <TargetFrameworkVersion>v4.8</TargetFrameworkVersion>
    <OutputPath>bin</OutputPath>
    <Name>XppProject</Name>
  </PropertyGroup>
  <ItemGroup>
    <Content Include="AxClass\\XvsClass"><SubType>Content</SubType><Name>XvsClass</Name><Link>Classes\\XvsClass</Link></Content>
  </ItemGroup>
  <ItemGroup>
    <ProjectReference Include="..\\Lib\\Lib.csproj"><Name>XvsLib</Name></ProjectReference>
    <Reference Include="XvsBinary, Version=0.0.0.0, Culture=neutral, PublicKeyToken=null"><Name>XvsBinary</Name><HintPath>..\\libs\\XvsBinary.dll</HintPath></Reference>
  </ItemGroup>
  <Import Project="$(MSBuildBinPath)\\Microsoft.Common.targets" />
  <Import Project="$(BuildTasksDirectory)\\Microsoft.Dynamics.Framework.Tools.BuildTasks.17.0.targets" />
</Project>
`);

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
    const timer = setTimeout(() => { console.log('TIMEOUT'); process.exit(1); }, 600000);
    const msbuild = await findMsBuild();
    execFileSync(msbuild.exe, [...msbuild.args, path.join(root, 'Bin', 'Bin.csproj'), '-nologo', '-v:q', '-nr:false'], { stdio: 'ignore' });
    fs.mkdirSync(path.join(root, 'libs'));
    for (const f of ['XvsBinary.dll', 'XvsBinary.pdb']) fs.copyFileSync(path.join(root, 'Bin', 'bin', 'Debug', f), path.join(root, 'libs', f));
    fs.rmSync(path.join(root, 'Bin'), { recursive: true });

    const store = path.join(root, 'Store');
    const packageFolder = path.join(store, 'XvsPackage');
    const bin = path.join(packageFolder, 'bin');
    const platformBin = path.join(root, 'PLD', 'bin');
    fs.mkdirSync(platformBin, { recursive: true });
    fs.writeFileSync(path.join(platformBin, 'XvsBinary.dll'), '');
    const log = [];
    const options = { projectFile: path.join(root, 'XppProject', 'XppProject.rnrproj'), packageFolder, platformBin, log: l => log.push(l) };

    await check('builds the references and copies what Visual Studio copies', async () => {
        assert.ok(await prepareProjectReferences(options), log.join('\n'));
        const copied = fs.readdirSync(bin, { recursive: true }).map(f => f.toString().toLowerCase()).sort();
        for (const f of ['xvslib.dll', 'xvslib.pdb', 'xvslib.xml', 'xvsdep.dll', 'xvsdep.pdb', path.join('fr', 'xvslib.resources.dll'), 'settings.json']) {
            assert.ok(copied.includes(f), `${f} missing; copied: ${copied.join(', ')}`);
        }
    });
    await check('skips Finance and Operations assemblies', async () => {
        assert.ok(!fs.existsSync(path.join(bin, 'XvsBinary.dll')));
        assert.ok(log.some(l => l.includes('XvsBinary.dll is part of Finance and Operations')));
        assert.ok(fs.existsSync(path.join(bin, 'XvsBinary.pdb')), 'the pdb of a binary reference is copied');
    });
    await check('copies nothing when the package is up to date', async () => {
        log.length = 0;
        assert.ok(await prepareProjectReferences(options), log.join('\n'));
        assert.ok(!log.some(l => l.startsWith('Copied')), log.join('\n'));
    });
    await check('fails when a referenced project does not compile', async () => {
        fs.writeFileSync(path.join(root, 'Dep', 'Code.cs'), 'namespace XvsDep { public class Dep { broken } }');
        log.length = 0;
        assert.strictEqual(await prepareProjectReferences(options), false);
        assert.ok(log.some(l => /error CS/.test(l)), log.join('\n'));
    });
    await check('lists packages linked with a junction', async () => {
        const target = path.join(root, 'Artifacts', 'XvsLinked');
        fs.mkdirSync(target, { recursive: true });
        fs.symlinkSync(target, path.join(store, 'XvsLinked'), 'junction');
        fs.symlinkSync(path.join(root, 'nowhere'), path.join(store, 'XvsDangling'), 'junction');
        fs.writeFileSync(path.join(store, 'file.txt'), '');
        assert.deepStrictEqual((await packageFolders(store)).sort(), ['XvsLinked', 'XvsPackage']);
    });

    clearTimeout(timer);
    fs.rmSync(root, { recursive: true, force: true });
    console.log(`failures: ${failures}`);
    process.exit(failures ? 1 : 0);
})();
