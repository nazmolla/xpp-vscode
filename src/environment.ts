import { execFile } from 'child_process';
import { existsSync, promises as fs } from 'fs';
import * as os from 'os';
import * as path from 'path';
import * as vscode from 'vscode';

const ConfigurationsKey = 'HKCU\\Software\\Microsoft\\Dynamics\\AX7\\Development\\Configurations';
const WorkspaceEnvironmentKey = 'xpp.environmentUrl';
/** Cloud-hosted and VHD environments keep every package here, next to a local AOS. */
const LocalPackagesFolders = ['K:\\AosService\\PackagesLocalDirectory', 'C:\\AosService\\PackagesLocalDirectory'];

export interface XppEnvironment {
    /** Org name of the environment (the configuration is <org>___<application version>.json). */
    name: string;
    configFile: string;
    frameworkDirectory: string;
    binDirectory: string;
    referencePackagesPaths: string[];
    modelStoreFolder: string;
    runtimePackagesDirectory?: string;
    crossReferencesDbServerName?: string;
    crossReferencesDatabaseName?: string;
    databaseServer?: string;
    businessDatabaseName?: string;
    defaultModelForNewProjects?: string;
    applicationVersion?: string;
    /** Web client URL (CloudInstanceURL). */
    finOpsUrl?: string;
    /** Set when the configuration belongs to the selected environment; unset for the machine-wide configuration. */
    dataverseUrl?: string;
    /** Cloud-hosted or VHD environment: a local AOS and database, no Dataverse, deploy or pac. */
    local: boolean;
}

/** A metadata configuration in XPPConfig, written by Connect to Environment or Visual Studio's Power Platform Tools. */
export interface EnvironmentConfig {
    org: string;
    applicationVersion: string;
    configFile: string;
    finOpsUrl?: string;
}

let workspaceState: vscode.Memento | undefined;
let storageFolder: string | undefined;
const changed = new vscode.EventEmitter<void>();

/** Fires when this window switches to another environment. */
export const onDidChangeEnvironment = changed.event;

/** Remembers the environment per workspace; the user setting xpp.environmentUrl is the default of every window. */
export function initEnvironmentSelection(context: vscode.ExtensionContext): void {
    workspaceState = context.workspaceState;
    storageFolder = context.globalStorageUri.fsPath;
    context.subscriptions.push(changed, changed.event(() => active = undefined), vscode.workspace.onDidChangeConfiguration(e => {
        if (e.affectsConfiguration('xpp.environmentUrl') && !workspaceState?.get<string>(WorkspaceEnvironmentKey)) {
            changed.fire();
        }
    }));
}

/** Dataverse URL of the environment this window works with. */
export function selectedEnvironmentUrl(): string | undefined {
    return workspaceState?.get<string>(WorkspaceEnvironmentKey)
        || vscode.workspace.getConfiguration('xpp').get<string>('environmentUrl')?.trim()
        || undefined;
}

/** True when this workspace chose its own environment instead of the default (xpp.environmentUrl). */
export function hasWorkspaceEnvironment(): boolean {
    return !!workspaceState?.get<string>(WorkspaceEnvironmentKey);
}

/**
 * Makes an environment the one this window works with. With a folder open the choice belongs to the workspace and is
 * kept out of its settings files, since every developer has an environment of their own. The first environment also
 * becomes the default.
 */
export async function useEnvironment(dataverseUrl: string): Promise<void> {
    const config = vscode.workspace.getConfiguration('xpp');
    const before = selectedEnvironmentUrl();
    const perWorkspace = !!workspaceState && !!vscode.workspace.workspaceFolders?.length;
    if (perWorkspace) {
        await workspaceState!.update(WorkspaceEnvironmentKey, dataverseUrl);
    }
    if (!perWorkspace || !config.inspect<string>('environmentUrl')?.globalValue) {
        await config.update('environmentUrl', dataverseUrl, vscode.ConfigurationTarget.Global);
    }
    // A setting change fires through onDidChangeConfiguration; a workspace choice does not.
    if (perWorkspace && before !== dataverseUrl) {
        changed.fire();
    }
}

/** Returns this workspace to the default environment (xpp.environmentUrl). */
export async function useDefaultEnvironment(): Promise<void> {
    const before = selectedEnvironmentUrl();
    await workspaceState?.update(WorkspaceEnvironmentKey, undefined);
    if (before !== selectedEnvironmentUrl()) {
        changed.fire();
    }
}

/** %LOCALAPPDATA%\Microsoft\Dynamics365: the metadata of each application version, and XPPConfig. */
export function dynamics365Folder(): string {
    return path.join(process.env.LOCALAPPDATA ?? os.homedir(), 'Microsoft', 'Dynamics365');
}

/** Org name of a Dataverse URL; a unified developer environment's F&O host starts with the same name. */
export function orgName(url: string): string {
    return new URL(/^https?:\/\//i.test(url.trim()) ? url.trim() : `https://${url.trim()}`).hostname.split('.')[0].toLowerCase();
}

/** The configurations in XPPConfig, the newest of each environment (an application update leaves the old one). */
export async function listEnvironments(): Promise<EnvironmentConfig[]> {
    const folder = path.join(dynamics365Folder(), 'XPPConfig');
    const newest = new Map<string, EnvironmentConfig>();
    for (const file of await fs.readdir(folder).catch(() => [] as string[])) {
        const match = /^(.+)___(\d+(?:\.\d+)+)\.json$/i.exec(file);
        if (!match) {
            continue;
        }
        const org = match[1].toLowerCase();
        const current = newest.get(org);
        if (!current || compareVersions(match[2], current.applicationVersion) > 0) {
            newest.set(org, { org, applicationVersion: match[2], configFile: path.join(folder, file) });
        }
    }
    const configs = [...newest.values()].sort((a, b) => a.org.localeCompare(b.org));
    for (const config of configs) {
        config.finOpsUrl = webClientUrl(await readConfig(config.configFile).catch(() => undefined));
    }
    return configs;
}

/** The configuration of an environment, by its Dataverse URL. */
export async function findEnvironment(dataverseUrl: string): Promise<EnvironmentConfig | undefined> {
    const org = orgName(dataverseUrl);
    return (await listEnvironments()).find(e => e.org === org);
}

// Many callers ask at once (status bar, trees, MCP); a short cache spares rereading every configuration.
const ActiveEnvironmentTtl = 5000;
let active: { url: string | undefined; time: number; environment: Promise<XppEnvironment> } | undefined;

/**
 * The metadata configuration of the selected environment. Without a selection, or when the selected environment has
 * no configuration on this machine, the machine-wide one shared with Visual Studio, or else the local AOS of a
 * cloud-hosted or VHD environment.
 */
export function readActiveEnvironment(): Promise<XppEnvironment> {
    const url = selectedEnvironmentUrl();
    if (!active || active.url !== url || Date.now() - active.time > ActiveEnvironmentTtl) {
        const entry = { url, time: Date.now(), environment: loadActiveEnvironment(url) };
        entry.environment.catch(() => active === entry ? active = undefined : undefined);
        active = entry;
    }
    return active.environment;
}

async function loadActiveEnvironment(dataverseUrl: string | undefined): Promise<XppEnvironment> {
    const selected = dataverseUrl ? await findEnvironment(dataverseUrl).catch(() => undefined) : undefined;
    const registered = selected ? undefined : await readRegistryValue('CurrentMetadataConfig');
    // On cloud-hosted environments the registry may name Visual Studio's DefaultConfig.xml instead of an XPPConfig file.
    const configFile = selected?.configFile ?? (registered && /\.json$/i.test(registered) && existsSync(registered) ? registered : await writeLocalConfig());
    if (!configFile) {
        throw new Error('No active D365FO configuration found. Connect to a unified developer environment first, or work on a cloud-hosted or VHD environment (AosService\\PackagesLocalDirectory).');
    }

    const json = await readConfig(configFile);
    const frameworkDirectory: string = json.FrameworkDirectory;
    if (!frameworkDirectory) {
        throw new Error(`FrameworkDirectory is missing in ${configFile}.`);
    }
    const [name, applicationVersion] = path.basename(configFile, '.json').split('___');

    return {
        name: name.toLowerCase(),
        configFile,
        frameworkDirectory,
        binDirectory: path.join(frameworkDirectory, 'bin'),
        referencePackagesPaths: json.ReferencePackagesPaths ?? [],
        modelStoreFolder: json.ModelStoreFolder,
        runtimePackagesDirectory: json.RuntimePackagesDirectory,
        crossReferencesDbServerName: json.CrossReferencesDbServerName,
        crossReferencesDatabaseName: json.CrossReferencesDatabaseName,
        databaseServer: json.DatabaseServer,
        businessDatabaseName: json.BusinessDatabaseName,
        defaultModelForNewProjects: json.DefaultModelForNewProjects,
        applicationVersion: selected?.applicationVersion ?? (/^\d+(\.\d+)+$/.test(applicationVersion ?? '') ? applicationVersion : undefined),
        finOpsUrl: webClientUrl(json),
        dataverseUrl: selected ? dataverseUrl : undefined,
        // As the metadata server decides: unified developer environments have runtime packages and host type 4.
        local: !json.RuntimePackagesDirectory && json.RuntimeHostType !== 4,
    };
}

/**
 * Cloud-hosted and VHD environments have no XPPConfig, so a configuration is made from PackagesLocalDirectory (both
 * the custom and the reference folder there) and the AOS web.config. Rewritten only when it changes, since the
 * metadata server reads it on every request.
 */
async function writeLocalConfig(): Promise<string | undefined> {
    const packages = LocalPackagesFolders.find(p => existsSync(path.join(p, 'bin', 'xppc.exe')));
    if (!packages || !storageFolder) {
        return undefined;
    }
    const webConfig = await fs.readFile(path.join(path.dirname(packages), 'webroot', 'web.config'), 'utf8').catch(() => '');
    const setting = (key: string) => new RegExp(`key="${key.replace(/\./g, '\\.')}"\\s+value="([^"]*)"`, 'i').exec(webConfig)?.[1] || undefined;
    const dbServer = setting('DataAccess.DbServer') ?? 'localhost';
    const content = JSON.stringify({
        FrameworkDirectory: packages,
        ModelStoreFolder: packages,
        ReferencePackagesPaths: [packages],
        DatabaseServer: dbServer,
        BusinessDatabaseName: setting('DataAccess.Database') ?? 'AxDB',
        CrossReferencesDbServerName: dbServer,
        CrossReferencesDatabaseName: 'DYNAMICSXREFDB',
        CloudInstanceURL: setting('Infrastructure.HostUrl'),
    }, null, 4);
    const file = path.join(storageFolder, `${os.hostname().toLowerCase()}.json`);
    if (await fs.readFile(file, 'utf8').catch(() => undefined) !== content) {
        await fs.mkdir(storageFolder, { recursive: true });
        await fs.writeFile(file, content, 'utf8');
    }
    return file;
}

async function readConfig(configFile: string): Promise<any> {
    return JSON.parse((await fs.readFile(configFile, 'utf8')).replace(/^\uFEFF/, ''));
}

function webClientUrl(json: any): string | undefined {
    const url = typeof json?.CloudInstanceURL === 'string' ? json.CloudInstanceURL.trim().replace(/\/+$/, '') : '';
    return url || undefined;
}

function compareVersions(a: string, b: string): number {
    const x = a.split('.').map(Number);
    const y = b.split('.').map(Number);
    for (let i = 0; i < Math.max(x.length, y.length); i++) {
        const diff = (x[i] ?? 0) - (y[i] ?? 0);
        if (diff !== 0) {
            return diff;
        }
    }
    return 0;
}

function readRegistryValue(name: string): Promise<string | undefined> {
    return new Promise(resolve => {
        execFile('reg', ['query', ConfigurationsKey, '/v', name], { windowsHide: true }, (err, stdout) => {
            if (err) {
                resolve(undefined);
                return;
            }
            const match = stdout.match(new RegExp(`${name}\\s+REG_\\w+\\s+(.+)`));
            resolve(match ? match[1].trim() : undefined);
        });
    });
}
