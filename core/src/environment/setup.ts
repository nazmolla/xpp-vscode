import { execFile, spawn } from 'child_process';
import { createWriteStream, existsSync, promises as fs } from 'fs';
import * as os from 'os';
import * as path from 'path';
import { Readable } from 'stream';
import { pipeline } from 'stream/promises';
import { onAbort, Progress, TokenProvider } from '../host';
import { httpFetch } from '../http';
import { templateConfiguration } from './config';
import { ConfigurationsKey, dynamics365Folder, XppEnvironment } from './discovery';
import { ClientAssets, getAssetDownloadUrl, readUdeEnvironment } from './ude';

const RequiredFreeBytes = 16 * 1024 ** 3;
const LocalDb = '(LocalDB)\\MSSQLLocalDB';

export type PhaseId = 'signIn' | 'sql' | 'download' | 'extract' | 'restore' | 'configure';
export type PhaseState = 'pending' | 'running' | 'done' | 'failed';

export const SetupPhases: { id: PhaseId; label: string }[] = [
    { id: 'signIn', label: 'Sign in to Dataverse' },
    { id: 'sql', label: 'Check SQL Server LocalDB' },
    { id: 'download', label: 'Download metadata and tools' },
    { id: 'extract', label: 'Extract PackagesLocalDirectory' },
    { id: 'restore', label: 'Restore cross-reference database' },
    { id: 'configure', label: 'Configure this machine' },
];

/** Progress of the setup: every message, and the phase (SetupPhases) when one starts. */
export interface SetupProgress extends Progress {
    report(message: string, phase?: PhaseId): void;
}

export interface UdeSetupOptions {
    tokens: TokenProvider;
    /** F&O Studio's server (XppVsCode.Server.exe): checks SQL Server LocalDB and restores the cross-reference database. */
    serverExe: string;
    progress: SetupProgress;
    signal?: AbortSignal;
    /** The active configuration, whose custom metadata folder the new one keeps (so every environment builds the same models). */
    activeEnvironment?: () => Promise<XppEnvironment | undefined>;
}

/**
 * Connect to Environment: the same client setup Visual Studio's Power Platform Tools perform for a unified developer
 * environment. Downloads system metadata, the cross-reference database and the FinOps tools (16 GB free needed),
 * extracts and restores them, links the runtime packages, writes the XPPConfig configuration and makes it the active
 * one in the registry. Returns that configuration file.
 */
export async function runUdeSetup(dataverseUrl: string, options: UdeSetupOptions): Promise<string> {
    const step = (message: string, phase?: PhaseId) => options.progress.report(message, phase);
    const signal = options.signal;
    step(`Signing in to ${dataverseUrl}…`, 'signIn');
    const accessToken = await options.tokens.getToken(dataverseUrl, { interactive: true });
    if (!accessToken) {
        throw new Error(`Not signed in to ${dataverseUrl}.`);
    }
    const env = await readUdeEnvironment(dataverseUrl, accessToken);
    if (env.deploymentType?.toLowerCase() !== 'onlinedev' || !env.applicationVersion) {
        throw new Error(`Not a unified developer environment (type ${env.deploymentType ?? 'unknown'}).`);
    }
    step(`Environment ${env.finOpsUrl}, application ${env.applicationVersion}.`);

    const orgName = new URL(dataverseUrl).hostname.split('.')[0];
    const dynamics365 = dynamics365Folder();
    const versionPath = path.join(dynamics365, env.applicationVersion);
    const pld = path.join(versionPath, 'PackagesLocalDirectory');
    const serverExe = options.serverExe;

    step('Checking SQL Server Express LocalDB…', 'sql');
    const sql = await runJson(serverExe, ['--sql-check', LocalDb]);
    if (!sql.ok) {
        throw new Error(`SQL Server Express LocalDB is not reachable with Windows authentication (${sql.message}). Install it or run "sqllocaldb create MSSQLLocalDB -s".`);
    }

    await fs.mkdir(versionPath, { recursive: true });
    if (!existsSync(pld)) {
        const disk = await fs.statfs(path.parse(versionPath).root);
        const free = disk.bavail * disk.bsize;
        if (free < RequiredFreeBytes) {
            throw new Error(`At least 16 GB free disk space is needed on ${path.parse(versionPath).root}; ${(free / 1024 ** 3).toFixed(1)} GB is available.`);
        }
    }

    const configFolder = path.join(dynamics365, 'XPPConfig');
    const configFile = path.join(configFolder, `${orgName}___${env.applicationVersion}.json`);
    const existing = existsSync(configFile) ? JSON.parse((await fs.readFile(configFile, 'utf8')).replace(/^\uFEFF/, '')) : {};
    const xrefReady = !!existing.CrossReferencesDatabaseName
        && (await runJson(serverExe, ['--sql-check', LocalDb, existing.CrossReferencesDatabaseName])).exists === true;

    step('Checking what is already on this machine…', 'download');
    for (const asset of ClientAssets) {
        const target = path.join(versionPath, asset.file);
        const done = asset.resource === 'systemmetadata' ? existsSync(pld)
            : asset.resource === 'crossreference' ? xrefReady
                : existsSync(path.join(versionPath, 'FinOpsTools'));
        if (done || existsSync(target)) {
            continue;
        }
        const url = await getAssetDownloadUrl(env, accessToken, asset.resource);
        await download(url, target, asset.file, step, signal);
    }

    const zip = path.join(versionPath, 'PackagesLocalDirectory.zip');
    step('Extracting…', 'extract');
    if (!existsSync(pld) && existsSync(zip)) {
        step('Extracting PackagesLocalDirectory (this takes several minutes)…');
        await extract(zip, pld, signal);
        await fs.rm(zip, { force: true });
    }
    const vsix = path.join(versionPath, 'Microsoft.Dynamics.FinOps.ToolsVS2022.vsix');
    if (existsSync(vsix) && !existsSync(path.join(versionPath, 'FinOpsTools', 'extension.vsixmanifest'))) {
        step('Extracting the FinOps tools (language server, debugger proxy)…');
        await extract(vsix, path.join(versionPath, 'FinOpsTools'), signal);
    }

    const customFolder = await customMetadataFolder(options.activeEnvironment);
    await fs.mkdir(configFolder, { recursive: true });

    let xrefName = existing.CrossReferencesDatabaseName ?? `XRef_${orgName}${env.applicationVersion.replace(/\./g, '')}`;
    const bak = path.join(versionPath, 'DYNAMICSXREFDB.bak');
    step(xrefReady ? 'Cross-reference database already restored.' : 'Restoring the cross-reference database…', 'restore');
    if (!xrefReady && existsSync(bak)) {
        xrefName = `XRef_${orgName}${env.applicationVersion.replace(/\./g, '')}`;
        for (let i = 1; (await runJson(serverExe, ['--sql-check', LocalDb, xrefName])).exists && i <= 5; i++) {
            xrefName = `XRef_${orgName}${env.applicationVersion.replace(/\./g, '')}_${i}`;
        }
        const dataFolder = path.join(configFolder, path.basename(configFile, '.json'));
        await fs.mkdir(dataFolder, { recursive: true });
        // LocalDB runs as the user, but a full SQL Server instance reads the files as NETWORK SERVICE.
        for (const folder of [dynamics365, versionPath, dataFolder]) {
            await exec('icacls', [folder, '/grant', '*S-1-5-20:(OI)(CI)F', '/Q']).catch(() => undefined);
        }
        step(`Restoring cross-reference database ${xrefName}…`);
        const restore = await runJson(serverExe, ['--restore-xref', LocalDb, xrefName, bak, dataFolder]);
        if (!restore.ok) {
            throw new Error(`Cross-reference restore failed: ${restore.message}`);
        }
        await fs.rm(bak, { force: true });
    }

    step('Creating runtime package links…', 'configure');
    const runtimeFolder = await createRuntimeLinks(path.join(dynamics365, 'RuntimeSymLinks', orgName), [pld], customFolder, path.join(pld, 'bin'));

    step('Writing the development configuration…');
    const config = {
        // Every member of Microsoft's template: Visual Studio cannot read a configuration that lacks a required one.
        ...await templateConfiguration(versionPath),
        ...existing,
        Description: `Unified development environment ${orgName}. FinOps application version ${env.applicationVersion}. Configuration generated on ${new Date().toLocaleDateString()} by X++ for VS Code.`,
        CloudInstanceURL: env.finOpsUrl ?? existing.CloudInstanceURL,
        ReferencePackagesPaths: [pld],
        FrameworkDirectory: pld,
        ModelStoreFolder: customFolder,
        DebugSourceFolder: customFolder,
        RuntimePackagesDirectory: runtimeFolder,
        CrossReferencesDbServerName: LocalDb,
        CrossReferencesDatabaseName: xrefName,
    };
    await fs.writeFile(configFile, JSON.stringify(config, null, 4), 'utf8');

    await exec('reg', ['add', ConfigurationsKey, '/v', 'CurrentMetadataConfig', '/t', 'REG_SZ', '/d', configFile, '/f']);
    await exec('reg', ['add', ConfigurationsKey, '/v', 'FrameworkDirectory', '/t', 'REG_SZ', '/d', pld, '/f']);
    step(`Done. Active configuration: ${configFile}`);
    return configFile;
}

/** The custom metadata folder of the active configuration, so every environment builds the same models, or a new C:\CustomXppMetadata<random>. */
async function customMetadataFolder(activeEnvironment?: () => Promise<XppEnvironment | undefined>): Promise<string> {
    const env = await activeEnvironment?.().catch(() => undefined);
    // PackagesLocalDirectory of a cloud-hosted environment holds Microsoft's packages too.
    if (env && !env.local && env.modelStoreFolder && existsSync(env.modelStoreFolder)) {
        return env.modelStoreFolder;
    }
    const folder = path.join(path.parse(os.homedir()).root, 'CustomXppMetadata' + Math.random().toString(36).slice(2, 10));
    await fs.mkdir(folder, { recursive: true });
    return folder;
}

/** Junction folder the runtime and compiler see: bin, every reference folder, and the custom folder as ZZZZ__<name>. */
async function createRuntimeLinks(folder: string, referenceFolders: string[], customFolder: string, frameworkBin: string): Promise<string> {
    let target = folder;
    for (let i = 1; existsSync(target); i++) {
        await fs.rm(target, { recursive: true, force: true }).catch(() => undefined);
        if (!existsSync(target)) {
            break;
        }
        target = `${folder}${i}`;
    }
    await fs.mkdir(target, { recursive: true });
    const links: [string, string][] = [['bin', frameworkBin], ...referenceFolders.map(r => [path.basename(r), r] as [string, string]), ['ZZZZ__' + path.basename(customFolder), customFolder]];
    for (const [name, source] of links) {
        await fs.symlink(source, path.join(target, name), 'junction');
    }
    return target;
}

async function download(url: string, target: string, label: string, step: (message: string) => void, signal?: AbortSignal): Promise<void> {
    const controller = new AbortController();
    const stop = onAbort(signal, () => controller.abort());
    const partial = target + '.partial';
    try {
        const response = await httpFetch(url, { signal: controller.signal });
        if (!response.ok || !response.body) {
            throw new Error(`Download of ${label} failed: ${response.status}`);
        }
        const total = Number(response.headers.get('content-length') ?? 0);
        let received = 0;
        let lastReport = 0;
        const body = Readable.fromWeb(response.body as any);
        body.on('data', (chunk: Buffer) => {
            received += chunk.length;
            if (Date.now() - lastReport > 5000) {
                lastReport = Date.now();
                step(`Downloading ${label}: ${total ? Math.floor(received * 100 / total) + '%' : (received / 1024 ** 2).toFixed(0) + ' MB'}`);
            }
        });
        await pipeline(body, createWriteStream(partial));
        if (total && received !== total) {
            throw new Error(`Download of ${label} was incomplete (${received} of ${total} bytes).`);
        }
        await fs.rename(partial, target);
        step(`Downloaded ${label}.`);
    } finally {
        stop();
        await fs.rm(partial, { force: true }).catch(() => undefined);
    }
}

/** Extracts a zip with the Windows tar (bsdtar), which handles large archives much faster than PowerShell. */
async function extract(zip: string, destination: string, signal?: AbortSignal): Promise<void> {
    await fs.mkdir(destination, { recursive: true });
    await new Promise<void>((resolve, reject) => {
        const child = spawn(path.join(process.env.SystemRoot ?? 'C:\\Windows', 'System32', 'tar.exe'), ['-xf', zip, '-C', destination], { windowsHide: true });
        const stop = onAbort(signal, () => child.kill());
        let stderr = '';
        child.stderr.on('data', d => { stderr += d.toString(); });
        child.on('error', reject);
        child.on('exit', code => {
            stop();
            code === 0 ? resolve() : reject(new Error(`Extracting ${path.basename(zip)} failed: ${stderr.slice(0, 300)}`));
        });
    });
}

function exec(file: string, args: string[]): Promise<string> {
    return new Promise((resolve, reject) => execFile(file, args, { windowsHide: true }, (err, stdout) => err ? reject(err) : resolve(stdout)));
}

async function runJson(exe: string, args: string[]): Promise<any> {
    return new Promise(resolve => execFile(exe, args, { windowsHide: true, timeout: 3_600_000 }, (_err, stdout) => {
        try {
            resolve(JSON.parse(stdout.trim().split('\n').pop() ?? '{}'));
        } catch {
            resolve({ ok: false, message: stdout.trim() || 'no output' });
        }
    }));
}
