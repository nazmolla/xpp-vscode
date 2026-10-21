import { execFile } from 'child_process';
import { promises as fs } from 'fs';
import * as path from 'path';

const ConfigurationsKey = 'HKCU\\Software\\Microsoft\\Dynamics\\AX7\\Development\\Configurations';

export interface XppEnvironment {
    configFile: string;
    frameworkDirectory: string;
    binDirectory: string;
    referencePackagesPaths: string[];
    modelStoreFolder: string;
    runtimePackagesDirectory?: string;
    crossReferencesDbServerName?: string;
    crossReferencesDatabaseName?: string;
    defaultModelForNewProjects?: string;
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

/** Reads the active D365FO metadata configuration shared with Visual Studio. */
export async function readActiveEnvironment(): Promise<XppEnvironment> {
    const configFile = await readRegistryValue('CurrentMetadataConfig');
    if (!configFile) {
        throw new Error('No active D365FO configuration found. Connect to a unified developer environment first.');
    }

    const raw = (await fs.readFile(configFile, 'utf8')).replace(/^\uFEFF/, '');
    const json = JSON.parse(raw);
    const frameworkDirectory: string = json.FrameworkDirectory;
    if (!frameworkDirectory) {
        throw new Error(`FrameworkDirectory is missing in ${configFile}.`);
    }

    return {
        configFile,
        frameworkDirectory,
        binDirectory: path.join(frameworkDirectory, 'bin'),
        referencePackagesPaths: json.ReferencePackagesPaths ?? [],
        modelStoreFolder: json.ModelStoreFolder,
        runtimePackagesDirectory: json.RuntimePackagesDirectory,
        crossReferencesDbServerName: json.CrossReferencesDbServerName,
        crossReferencesDatabaseName: json.CrossReferencesDatabaseName,
        defaultModelForNewProjects: json.DefaultModelForNewProjects,
    };
}
