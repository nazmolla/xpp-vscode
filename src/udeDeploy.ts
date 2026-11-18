import { execFile } from 'child_process';
import { randomUUID } from 'crypto';
import { existsSync, promises as fs } from 'fs';
import * as os from 'os';
import * as path from 'path';
import * as vscode from 'vscode';
import { Dataverse } from './dataverse';
import { packageFolders } from './packageStore';

/**
 * Deploys locally built packages to a unified developer environment the way Visual Studio does: the package folder
 * from the custom metadata store goes up as is (with the PDBs the build converted to portable), nothing is recompiled.
 * Transport is the Dataverse API that Power Platform Tools and pac use: one msprov_fnopackage row per package with the
 * zip in msprov_packagepayload, then msprov_deploypackagetofinopsasync.
 */

export type DbSync = 'None' | 'Module' | 'Full';

export interface DeployPackage {
    name: string;
    folder: string;
}

/** Server option values (msprov_fnopackage). */
const BuildTypeFull = 0;
const PackageTypeDev = 0;
const DbSyncValue: Record<DbSync, number> = { None: 0, Full: 1, Module: 2 };

/** Not deployed, as in Visual Studio; BuildLogs is where F&O Studio's build writes its logs. */
const ExcludedFiles = ['buildprojectresult.log', 'buildprojectresult.xml', 'buildmodelresult.log', 'buildmodelresult.xml', 'buildmodelresult.err.xml', 'buildprojectresult.err.xml'];
const ExcludedFolders = ['buildlogs'];

/** Single requests stay below Dataverse's 128 MB limit for one file upload; larger files go up in blocks. */
const SingleUploadLimit = 100 * 1024 * 1024;

export function zipName(pkg: string): string {
    return `${pkg}_1_0_0_1_managed.zip`;
}

/** fnomoduledefinition.json, which the environment's apply step reads to identify the package (same shape as pac and Visual Studio). */
export function moduleDefinition(pkg: string, dbSync: DbSync, now = new Date()): string {
    const timestamp = new Date(now.getTime());
    timestamp.setUTCFullYear(timestamp.getUTCFullYear() - 10);
    const pad = (n: number) => String(n).padStart(2, '0');
    const stamp = `${pad(timestamp.getUTCMonth() + 1)}/${pad(timestamp.getUTCDate())}/${timestamp.getUTCFullYear()} `
        + `${pad(timestamp.getUTCHours())}:${pad(timestamp.getUTCMinutes())}:${pad(timestamp.getUTCSeconds())}`;
    return JSON.stringify({
        Module: { Name: pkg, properties: [{ Item1: 'packagingSource', Item2: 'Pipeline' }] },
        BuildType: 'Full',
        PackageType: 'Dev',
        TimestampUtc: stamp,
        CorrelationID: randomUUID(),
        ClientID: os.hostname(),
        OrganizationID: '00000000-0000-0000-0000-000000000000',
        DBSync: { SyncKind: dbSync, Arguments: '' },
        Versions: { Platform: '0.0.0.0', Application: '0.0.0.0', Compiler: '0.0.0.0', PackageVersion: packageVersion(now) },
        AdditionalData: {},
    }, undefined, 2);
}

/** Four-part version from the Windows file time, as Visual Studio and pac compute it. */
export function packageVersion(now: Date): string {
    const fileTime = (BigInt(now.getTime()) + 11644473600000n) * 10000n;
    let rest = fileTime;
    const text = fileTime.toString();
    if (text.length !== 18) {
        return '0.0.0.0';
    }
    const parts: bigint[] = [];
    for (let i = 1; i <= 3; i++) {
        const unit = 10n ** BigInt(18 - i * 5);
        parts.push(rest / unit);
        rest -= parts[i - 1] * unit;
    }
    parts.push(rest);
    return parts.join('.');
}

/** DB sync argument for a module sync of the deployed packages. */
export function dbSyncArgument(dbSync: DbSync, packages: string[]): string {
    return dbSync === 'Module' ? JSON.stringify({ Modules: packages }) : '';
}

/** Zips a package folder as <package>/... with fnomoduledefinition.json at the root (.NET's ZipFile, as Visual Studio). */
export async function zipPackage(pkg: DeployPackage, manifest: string, target: string): Promise<void> {
    const script = `
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.IO.Compression, System.IO.Compression.FileSystem
$root = (Get-Item -LiteralPath $env:XPP_DEPLOY_SOURCE).FullName.TrimEnd('\\')
$skipFiles = $env:XPP_DEPLOY_SKIPFILES.Split('|')
$skipFolders = $env:XPP_DEPLOY_SKIPFOLDERS.Split('|')
$zip = [IO.Compression.ZipFile]::Open($env:XPP_DEPLOY_TARGET, 'Create')
try {
  Get-ChildItem -LiteralPath $root -Recurse -File -Force | ForEach-Object {
    $rel = $_.FullName.Substring($root.Length + 1)
    $top = $rel.Split('\\')[0].ToLowerInvariant()
    if ($rel.Contains('\\') -and $skipFolders -contains $top) { return }
    if ($skipFiles -contains $_.Name.ToLowerInvariant() -or $_.Extension -ieq '.delete') { return }
    [void][IO.Compression.ZipFileExtensions]::CreateEntryFromFile($zip, $_.FullName, ($env:XPP_DEPLOY_NAME + '/' + $rel.Replace('\\', '/')), 'Optimal')
  }
  $writer = New-Object IO.StreamWriter($zip.CreateEntry('fnomoduledefinition.json').Open())
  try { $writer.Write($env:XPP_DEPLOY_MANIFEST) } finally { $writer.Dispose() }
} finally { $zip.Dispose() }`;
    await new Promise<void>((resolve, reject) => execFile('powershell', ['-NoProfile', '-NonInteractive', '-Command', script], {
        windowsHide: true,
        timeout: 10 * 60_000,
        env: {
            ...process.env,
            XPP_DEPLOY_SOURCE: pkg.folder,
            XPP_DEPLOY_NAME: pkg.name,
            XPP_DEPLOY_TARGET: target,
            XPP_DEPLOY_MANIFEST: manifest,
            XPP_DEPLOY_SKIPFILES: ExcludedFiles.join('|'),
            XPP_DEPLOY_SKIPFOLDERS: ExcludedFolders.join('|'),
        },
    }, (err, _stdout, stderr) => err ? reject(new Error(`Could not package ${pkg.name}: ${(stderr || err.message).trim().slice(0, 500)}`)) : resolve()));
}

async function upload(dv: Dataverse, rowId: string, file: string, fileName: string, token?: vscode.CancellationToken): Promise<void> {
    const column = `msprov_fnopackages(${rowId})/msprov_packagepayload`;
    const bytes = await fs.readFile(file);
    if (bytes.length <= SingleUploadLimit) {
        await dv.send('PATCH', column, new Uint8Array(bytes), { 'Content-Type': 'application/octet-stream', 'x-ms-file-name': fileName }, token);
        return;
    }
    // Dataverse's block upload: the first request returns the session URL and the block size.
    const session = await dv.send('PATCH', column, undefined, { 'x-ms-transfer-mode': 'chunked', 'x-ms-file-name': fileName }, token);
    const location = session.headers.get('Location');
    const blockSize = Number(session.headers.get('x-ms-chunk-size')) || 4 * 1024 * 1024;
    if (!location) {
        throw new Error('Dataverse did not return an upload session for the package.');
    }
    for (let start = 0; start < bytes.length; start += blockSize) {
        const end = Math.min(start + blockSize, bytes.length);
        await dv.send('PATCH', location, new Uint8Array(bytes.subarray(start, end)), {
            'Content-Type': 'application/octet-stream',
            'Content-Range': `bytes ${start}-${end - 1}/${bytes.length}`,
            'x-ms-file-name': fileName,
        }, token);
    }
}

function asyncOperationId(response: any): string | undefined {
    const value = response?.asyncoperationid ?? response?.AsyncOperationId;
    const id = typeof value === 'object' ? value?.asyncoperationid : value;
    return typeof id === 'string' ? id : undefined;
}

/** Polls a Dataverse system job until it ends; throws with the server's message when it fails. */
async function waitForJob(dv: Dataverse, id: string, what: string, log: (line: string) => void, token?: vscode.CancellationToken): Promise<void> {
    const deadline = Date.now() + 4 * 60 * 60_000;
    let last = '';
    for (;;) {
        if (token?.isCancellationRequested) {
            throw new Error(`Stopped waiting. The ${what} keeps running on the environment (system job ${id}).`);
        }
        if (Date.now() > deadline) {
            throw new Error(`The ${what} did not finish within 4 hours (system job ${id}).`);
        }
        await new Promise(r => setTimeout(r, 15_000));
        const job = await dv.json('GET', `asyncoperations(${id})?$select=statecode,statuscode,message,friendlymessage`, undefined,
            { Prefer: 'odata.include-annotations="OData.Community.Display.V1.FormattedValue"' }, token);
        const status = job['statuscode@OData.Community.Display.V1.FormattedValue'] ?? String(job.statuscode);
        if (status !== last) {
            log(`  ${what}: ${status}`);
            last = status;
        }
        if (job.statecode === 3) {
            if (job.statuscode === 30) {
                return;
            }
            throw new Error(`The ${what} ended with status ${status}: ${job.friendlymessage || job.message || 'no message'}`);
        }
    }
}

async function ensureErpLinked(dv: Dataverse, token?: vscode.CancellationToken): Promise<void> {
    const found = await dv.json('GET', "solutions?$filter=uniquename eq 'msprov_fnouploadersolution'&$select=solutionid", undefined, {}, token);
    if (!found?.value?.length) {
        throw new Error(`${dv.url} has no Finance and Operations deployment support (solution msprov_fnouploadersolution). Is it a unified developer environment?`);
    }
}

/** Packages built in the custom metadata store; deploy them with deployPackages. */
export async function builtPackages(store: string): Promise<DeployPackage[]> {
    return (await packageFolders(store))
        .filter(name => existsSync(path.join(store, name, 'bin', `Dynamics.AX.${name}.dll`)))
        .map(name => ({ name, folder: path.join(store, name) }));
}

export async function deployPackages(packages: DeployPackage[], dbSync: DbSync, log: (line: string) => void, token?: vscode.CancellationToken): Promise<void> {
    const dv = await Dataverse.forSelectedEnvironment();
    await ensureErpLinked(dv, token);
    const work = path.join(os.tmpdir(), 'xpp-vscode-deploy', String(Date.now()));
    await fs.mkdir(work, { recursive: true });
    try {
        const rows: string[] = [];
        for (const pkg of packages) {
            const fileName = zipName(pkg.name);
            const file = path.join(work, fileName);
            log(`Packaging ${pkg.name} from ${pkg.folder}…`);
            await zipPackage(pkg, moduleDefinition(pkg.name, dbSync), file);
            const size = (await fs.stat(file)).size;
            const created = await dv.send('POST', 'msprov_fnopackages', {
                msprov_name: fileName,
                msprov_buildtype: BuildTypeFull,
                msprov_packagetype: PackageTypeDev,
                msprov_dbsyncoptions: DbSyncValue[dbSync],
                msprov_dbsyncvalue: dbSyncArgument(dbSync, packages.map(p => p.name)),
            }, {}, token);
            const rowId = /([0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12})/i.exec(created.headers.get('OData-EntityId') ?? '')?.[1];
            if (!rowId) {
                throw new Error('Dataverse did not return the package record id.');
            }
            log(`Uploading ${fileName} (${Math.ceil(size / 1024)} KB)…`);
            await upload(dv, rowId, file, fileName, token);
            rows.push(rowId);
        }
        const started = await dv.json('POST', 'msprov_deploypackagetofinopsasync', {
            finopspackages: rows.map(id => ({ '@odata.type': 'Microsoft.Dynamics.CRM.msprov_fnopackage', msprov_fnopackageid: id })),
            buildtype: BuildTypeFull,
            packagetype: PackageTypeDev,
        }, {}, token);
        const job = asyncOperationId(started);
        if (!job) {
            throw new Error('The deployment did not start: Dataverse returned no system job.');
        }
        log(`Deployment started (system job ${job}). This usually takes several minutes${dbSync === 'None' ? '' : ', longer with the database sync'}.`);
        await waitForJob(dv, job, 'deployment', log, token);
    } finally {
        await fs.rm(work, { recursive: true, force: true }).catch(() => undefined);
    }
}

/** Database sync of the environment (Full, or Module for the given modules), without a deploy. */
export async function syncUdeDatabase(mode: 'Full' | 'Module', modules: string[], log: (line: string) => void, token?: vscode.CancellationToken): Promise<void> {
    const dv = await Dataverse.forSelectedEnvironment();
    await ensureErpLinked(dv, token);
    const started = await dv.json('POST', 'msprov_dbsynctofinopsasync', {
        dbsynckind: DbSyncValue[mode],
        dbsyncargument: dbSyncArgument(mode, modules),
    }, {}, token);
    const job = asyncOperationId(started);
    if (!job) {
        throw new Error('The database sync did not start: Dataverse returned no system job.');
    }
    log(`Database sync started (system job ${job}).`);
    await waitForJob(dv, job, 'database sync', log, token);
}
