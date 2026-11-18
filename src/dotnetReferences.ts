import { execFile, spawn } from 'child_process';
import { existsSync, promises as fs } from 'fs';
import * as os from 'os';
import * as path from 'path';

/**
 * What Visual Studio's build does before it compiles X++ (Microsoft.Dynamics.Framework.Tools.BuildTasks targets: the
 * Build target depends on ResolveProjectReferences, ResolveAssemblyReferences and GetCopyToOutputDirectoryItems, then
 * CopyReferencesTask): it builds the C# projects the X++ project references and copies what they need at run time into
 * the package's bin folder, where xppc, the AOS and a deployable package find them. Those are the referenced assemblies
 * (project references and assembly references with a HintPath) with their dependencies, PDB and XML files and satellite
 * assemblies, and the files marked "Copy to Output Directory".
 *
 * MSBuild runs on the .rnrproj itself. Visual Studio's build tasks need Visual Studio, so the project's import of them
 * ($(BuildTasksDirectory)) is pointed at empty stand-ins, and a target that only resolves the references is added.
 * The server's build for agents (BuildService.cs, ProjectReferences.cs) does the same.
 */

/** Cancellation as vscode.CancellationToken has it; this module doesn't depend on vscode. */
export interface Cancellation {
    readonly isCancellationRequested: boolean;
    onCancellationRequested(listener: () => void): { dispose(): void };
}

export interface ReferenceFile {
    source: string;
    /** Path relative to the package's bin folder. */
    target: string;
    /** "Copy always"; other files are copied when they are newer. */
    always: boolean;
}

export interface MsBuild {
    exe: string;
    args: string[];
}

export interface CopyResult {
    copied: string[];
    skipped: string[];
    failed: string[];
}

const TargetName = 'XppStudioResolveReferences';

/** The project has references or files to copy, so MSBuild has something to do. */
export function hasReferences(projectXml: string): boolean {
    return /<(ProjectReference|Reference)\s/.test(projectXml) || /<CopyToOutputDirectory>\s*(Always|PreserveNewest)\s*</i.test(projectXml);
}

/** xpp.build.msbuildPath, else Visual Studio's or the Build Tools' MSBuild (vswhere), else the .NET SDK's. */
export async function findMsBuild(configured?: string): Promise<MsBuild> {
    if (configured) {
        if (!existsSync(configured)) {
            throw new Error(`MSBuild not found at ${configured} (setting xpp.build.msbuildPath).`);
        }
        return { exe: configured, args: [] };
    }
    const vswhere = path.join(process.env['ProgramFiles(x86)'] ?? 'C:\\Program Files (x86)', 'Microsoft Visual Studio', 'Installer', 'vswhere.exe');
    if (existsSync(vswhere)) {
        const found = await new Promise<string>(resolve => execFile(vswhere,
            ['-latest', '-prerelease', '-products', '*', '-requires', 'Microsoft.Component.MSBuild', '-find', 'MSBuild\\**\\Bin\\MSBuild.exe'],
            { windowsHide: true, timeout: 30_000 }, (err, stdout) => resolve(err ? '' : stdout)));
        const exe = found.split(/\r?\n/).map(l => l.trim()).find(l => l && existsSync(l));
        if (exe) {
            return { exe, args: [] };
        }
    }
    return { exe: 'dotnet', args: ['msbuild'] };
}

/** Writes the stand-ins for the project's $(BuildTasksDirectory) imports and the resolving target; returns the MSBuild arguments. */
async function prepare(projectFile: string, projectXml: string, resultFile: string): Promise<string[]> {
    const folder = path.join(os.tmpdir(), 'xpp-vscode-msbuild');
    const stubs = path.join(folder, 'BuildTasks');
    await fs.mkdir(stubs, { recursive: true });
    for (const m of projectXml.matchAll(/<Import\s+Project="\$\(BuildTasksDirectory\)\\([^"\\/]+\.targets)"/gi)) {
        await writeIfChanged(path.join(stubs, m[1]), '<Project xmlns="http://schemas.microsoft.com/developer/msbuild/2003" />\r\n');
    }
    const targets = path.join(folder, 'XppStudio.References.targets');
    // BuildOnlySettings sets BuildingProject, without which reference resolution skips dependencies and related files.
    await writeIfChanged(targets, `<Project xmlns="http://schemas.microsoft.com/developer/msbuild/2003">
  <Target Name="${TargetName}" DependsOnTargets="BuildOnlySettings;ResolveReferences;GetCopyToOutputDirectoryItems" />
</Project>
`);
    return [
        projectFile, '-nologo', '-v:minimal', '-nr:false', '-restore', '-p:RestorePackagesConfig=true',
        `-p:BuildTasksDirectory=${stubs}`, `-p:CustomAfterMicrosoftCommonTargets=${targets}`,
        `-t:${TargetName}`, '-getItem:ReferenceCopyLocalPaths', '-getItem:AllItemsFullPathWithTargetPath', `-getResultOutputFile:${resultFile}`,
    ];
}

/**
 * The referenced projects import these files too (global properties flow to them), and imported files are inputs of
 * their compile: rewriting an unchanged file would rebuild them every time.
 */
async function writeIfChanged(file: string, text: string): Promise<void> {
    if (await fs.readFile(file, 'utf8').catch(() => undefined) !== text) {
        await fs.writeFile(file, text, 'utf8');
    }
}

/** The files the build copies into the package's bin folder, from MSBuild's result. */
export function referenceFiles(result: { Items?: Record<string, Record<string, string>[]> }): ReferenceFile[] {
    const files = new Map<string, ReferenceFile>();
    for (const item of result.Items?.ReferenceCopyLocalPaths ?? []) {
        const source = item.FullPath || item.Identity;
        files.set(path.join(item.DestinationSubDirectory ?? '', path.basename(source)).toLowerCase(),
            { source, target: path.join(item.DestinationSubDirectory ?? '', path.basename(source)), always: false });
    }
    for (const item of result.Items?.AllItemsFullPathWithTargetPath ?? []) {
        const mode = (item.CopyToOutputDirectory ?? '').toLowerCase();
        if ((mode === 'always' || mode === 'preservenewest') && item.TargetPath) {
            files.set(item.TargetPath.toLowerCase(), { source: item.FullPath || item.Identity, target: item.TargetPath, always: mode === 'always' });
        }
    }
    return [...files.values()];
}

/**
 * Builds the referenced projects and lists the files to copy. Returns undefined when MSBuild fails (its output says why).
 * run starts a process with its output going to the user and resolves to whether it succeeded.
 */
export async function resolveReferences(projectFile: string, msbuild: MsBuild, run: (exe: string, args: string[]) => Promise<boolean>): Promise<ReferenceFile[] | undefined> {
    const projectXml = await fs.readFile(projectFile, 'utf8');
    const resultFile = path.join(os.tmpdir(), 'xpp-vscode-msbuild', `result-${process.pid}-${Date.now()}.json`);
    try {
        const args = await prepare(projectFile, projectXml, resultFile);
        if (!await run(msbuild.exe, [...msbuild.args, ...args]) || !existsSync(resultFile)) {
            return undefined;
        }
        return referenceFiles(JSON.parse((await fs.readFile(resultFile, 'utf8')).replace(/^\uFEFF/, '')));
    } finally {
        await fs.rm(resultFile, { force: true });
    }
}

/** Compiled X++ packages and Finance and Operations' own assemblies are never copied into a package. */
export function isPlatformFile(fileName: string, platformBin: string): boolean {
    return /^Dynamics\.AX\./i.test(fileName) || existsSync(path.join(platformBin, fileName));
}

/**
 * Copies the files into the package's bin folder: files marked "copy always" every time, others when they are missing
 * or newer, as CopyReferencesTask does. Unlike it, files keep their subfolder (satellite assemblies, content in folders).
 */
export async function copyReferences(files: ReferenceFile[], binFolder: string, platformBin: string, log: (line: string) => void): Promise<CopyResult> {
    const result: CopyResult = { copied: [], skipped: [], failed: [] };
    const root = path.resolve(binFolder);
    for (const file of files) {
        const target = path.resolve(root, file.target);
        if (!target.toLowerCase().startsWith(root.toLowerCase() + path.sep)) {
            result.failed.push(file.target);
            log(`Not copied: ${file.target} is outside the package's bin folder.`);
            continue;
        }
        if (isPlatformFile(path.basename(target), platformBin)) {
            result.skipped.push(file.target);
            log(`Not copied: ${file.target} is part of Finance and Operations (reference it with Copy Local = False).`);
            continue;
        }
        try {
            const source = await fs.stat(file.source);
            const existing = await fs.stat(target).catch(() => undefined);
            // Newer than the source is up to date: the build converts copied PDBs to portable ones in place.
            if (!file.always && existing && existing.mtimeMs >= source.mtimeMs) {
                continue;
            }
            await fs.mkdir(path.dirname(target), { recursive: true });
            // CopyFileW keeps the write time, so an unchanged file is not copied again next time.
            await fs.copyFile(file.source, target);
            result.copied.push(file.target);
        } catch (err) {
            result.failed.push(file.target);
            log(`Could not copy ${file.source} to ${target}: ${(err as Error).message}`);
        }
    }
    if (result.copied.length) {
        log(`Copied ${result.copied.length} referenced file(s) into ${binFolder}: ${result.copied.join(', ')}`);
    }
    return result;
}

/** Builds a C# project; returns its assembly (TargetPath), or undefined when the build failed (its output says why). */
export async function buildProject(projectFile: string, msbuild: MsBuild, run: (exe: string, args: string[]) => Promise<boolean>): Promise<string | undefined> {
    const folder = path.join(os.tmpdir(), 'xpp-vscode-msbuild');
    await fs.mkdir(folder, { recursive: true });
    const resultFile = path.join(folder, `build-${process.pid}-${Date.now()}.json`);
    try {
        const ok = await run(msbuild.exe, [...msbuild.args, projectFile, '-nologo', '-v:minimal', '-nr:false', '-restore', '-p:RestorePackagesConfig=true',
            '-t:Build', '-getProperty:TargetPath', `-getResultOutputFile:${resultFile}`]);
        if (!ok || !existsSync(resultFile)) {
            return undefined;
        }
        // With a single -getProperty MSBuild writes the bare value, not JSON.
        const text = (await fs.readFile(resultFile, 'utf8')).replace(/^\uFEFF/, '').trim();
        const target = text.startsWith('{') ? (JSON.parse(text) as { Properties?: { TargetPath?: string } }).Properties?.TargetPath : text;
        return target && existsSync(target) ? target : undefined;
    } finally {
        await fs.rm(resultFile, { force: true });
    }
}

/** The registry value Visual Studio and F&O Studio keep the active configuration's PackagesLocalDirectory in. */
const FrameworkDirectoryProperty = "$([MSBuild]::GetRegistryValue('HKEY_CURRENT_USER\\Software\\Microsoft\\Dynamics\\AX7\\Development\\Configurations', 'FrameworkDirectory'))";

/**
 * A C# class library for X++ code to call: .NET Framework 4.8 (what the AOS runs), portable PDBs (what the VS Code
 * debugger reads), and references to Finance and Operations' assemblies that are compiled against but never copied
 * (Copy Local off: they are part of the environment). Their folder comes from the registry, so the project builds on
 * every developer machine and follows the active version; a build pipeline passes FnOPackagesDirectory instead.
 */
export function csharpProjectXml(name: string, packages: string[]): string {
    const reference = (assembly: string, hintPath: string) =>
        `    <Reference Include="${assembly}">\r\n      <HintPath>${hintPath}</HintPath>\r\n      <Private>false</Private>\r\n    </Reference>\r\n`;
    return `<Project Sdk="Microsoft.NET.Sdk">\r
  <PropertyGroup>\r
    <TargetFramework>net48</TargetFramework>\r
    <RootNamespace>${name}</RootNamespace>\r
    <AssemblyName>${name}</AssemblyName>\r
    <DebugType>portable</DebugType>\r
    <PlatformTarget>AnyCPU</PlatformTarget>\r
    <FnOPackagesDirectory Condition="'$(FnOPackagesDirectory)' == ''">${FrameworkDirectoryProperty}</FnOPackagesDirectory>\r
  </PropertyGroup>\r
  <!-- Finance and Operations: compiled against, not copied (Private false); the X++ types are in namespace Dynamics.AX.Application. -->\r
  <ItemGroup>\r
${reference('Microsoft.Dynamics.AX.Xpp.AxShared', '$(FnOPackagesDirectory)\\bin\\Microsoft.Dynamics.AX.Xpp.AxShared.dll')}${reference('Microsoft.Dynamics.AX.Xpp.Support', '$(FnOPackagesDirectory)\\bin\\Microsoft.Dynamics.AX.Xpp.Support.dll')}${packages.map(p => reference(`Dynamics.AX.${p}`, `$(FnOPackagesDirectory)\\${p}\\bin\\Dynamics.AX.${p}.dll`)).join('')}  </ItemGroup>\r
</Project>\r
`;
}

/** The first class of a new C# project, showing how X++ calls it. */
export function csharpClassText(namespace: string): string {
    return `namespace ${namespace}\r
{\r
    /// <summary>\r
    /// Called from X++ (after a build of the X++ project, which copies this assembly into its package):\r
    ///     str greeting = ${namespace}.Greeter::Hello('world');\r
    /// </summary>\r
    public static class Greeter\r
    {\r
        public static string Hello(string name)\r
        {\r
            return "Hello " + name;\r
        }\r
    }\r
}\r
`;
}

/** The referenced assemblies in a package's bin folder that have symbols (not the compiled X++ modules), for the debugger. */
export async function referencedModules(binFolder: string): Promise<string[]> {
    const files = await fs.readdir(binFolder).catch(() => [] as string[]);
    const lower = new Set(files.map(f => f.toLowerCase()));
    return files.filter(f => /\.dll$/i.test(f) && !/^Dynamics\.AX\./i.test(f) && lower.has(f.slice(0, -4).toLowerCase() + '.pdb'));
}

/** Runs a process, sending its output to log line by line; resolves to whether it exited with 0. */
export function runProcess(exe: string, args: string[], log: (line: string) => void, token?: Cancellation): Promise<boolean> {
    log(`> ${path.basename(exe)} ${args.join(' ')}`);
    return new Promise(resolve => {
        const child = spawn(exe, args, { windowsHide: true });
        const cancel = token?.onCancellationRequested(() => child.kill());
        let pending = '';
        const onData = (data: Buffer) => {
            const lines = (pending + data.toString()).split(/\r?\n/);
            pending = lines.pop() ?? '';
            lines.forEach(log);
        };
        child.stdout.on('data', onData);
        child.stderr.on('data', onData);
        child.on('error', err => {
            cancel?.dispose();
            log(`Could not start ${exe}: ${err.message}`);
            resolve(false);
        });
        child.on('close', code => {
            cancel?.dispose();
            if (pending) {
                log(pending);
            }
            resolve(code === 0);
        });
    });
}

/**
 * The reference step for one package: nothing when the project has no references; otherwise builds them and copies
 * their files. Returns false when the build or a copy failed.
 */
export async function prepareProjectReferences(options: {
    projectFile: string;
    packageFolder: string;
    platformBin: string;
    msbuildPath?: string;
    log: (line: string) => void;
    run?: (exe: string, args: string[]) => Promise<boolean>;
    token?: Cancellation;
}): Promise<boolean> {
    const { projectFile, packageFolder, platformBin, log } = options;
    if (!hasReferences(await fs.readFile(projectFile, 'utf8'))) {
        return true;
    }
    log(`Building the projects and assemblies ${path.basename(projectFile)} references…`);
    const msbuild = await findMsBuild(options.msbuildPath);
    const run = options.run ?? ((exe: string, args: string[]) => runProcess(exe, args, log, options.token));
    const files = await resolveReferences(projectFile, msbuild, run);
    if (!files) {
        log(msbuild.exe === 'dotnet'
            ? 'Building the references failed. MSBuild came from the .NET SDK (no Visual Studio or Build Tools found); set xpp.build.msbuildPath to use another MSBuild.'
            : 'Building the references failed.');
        return false;
    }
    if (options.token?.isCancellationRequested) {
        return false;
    }
    const copied = await copyReferences(files, path.join(packageFolder, 'bin'), platformBin, log);
    return copied.failed.length === 0;
}
