import { execFile } from 'child_process';
import { existsSync, promises as fs } from 'fs';
import * as os from 'os';
import * as path from 'path';
import { discoverLocalAos } from './localAos';

/** Where Visual Studio and F&O Studio record the active metadata configuration. */
export const ConfigurationsKey = 'HKCU\\Software\\Microsoft\\Dynamics\\AX7\\Development\\Configurations';

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

export interface LoadEnvironmentOptions {
    /**
     * Folder for the configuration made for a cloud-hosted or VHD environment (they have no XPPConfig); without it such
     * an environment is not found.
     */
    localConfigFolder?: string;
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

/**
 * The metadata configuration of an environment (by Dataverse URL). Without one, or when that environment has no
 * configuration on this machine, the machine-wide one shared with Visual Studio (registry CurrentMetadataConfig), or
 * else the local AOS of a cloud-hosted or VHD environment.
 */
export async function loadEnvironment(dataverseUrl: string | undefined, options: LoadEnvironmentOptions = {}): Promise<XppEnvironment> {
    const selected = dataverseUrl ? await findEnvironment(dataverseUrl).catch(() => undefined) : undefined;
    const registered = selected ? undefined : await readRegistryValue('CurrentMetadataConfig');
    // On cloud-hosted environments the registry may name Visual Studio's DefaultConfig.xml instead of an XPPConfig file.
    const configFile = selected?.configFile ?? (registered && /\.json$/i.test(registered) && existsSync(registered) ? registered : await writeLocalConfig(options.localConfigFolder));
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
 * the custom and the reference folder there) and the AOS web.config, both found through the AOS's own configuration
 * (discoverLocalAos). Rewritten only when it changes, since the metadata server reads it on every request.
 */
async function writeLocalConfig(storageFolder: string | undefined): Promise<string | undefined> {
    const aos = await discoverLocalAos();
    if (!aos || !storageFolder) {
        return undefined;
    }
    const packages = aos.packagesFolder;
    const setting = (key: string) => aos.settings.get(key.toLowerCase()) || undefined;
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

/** A value under ConfigurationsKey (CurrentMetadataConfig, FrameworkDirectory); undefined when it isn't set. */
export function readRegistryValue(name: string): Promise<string | undefined> {
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
