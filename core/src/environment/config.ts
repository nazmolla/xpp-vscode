import { existsSync, promises as fs } from 'fs';
import * as path from 'path';

/**
 * Every member of Microsoft's Template_xppconfig.json (FinOps tools Resources). Visual Studio reads configurations with a
 * DataContractJsonSerializer where AosWebsiteName, AudienceUri, DatabaseServer, DebugSourceFolder, DefaultModelForNewProjects,
 * EnableOfflineAuthentication, ModelStoreFolder and WebRoleDeploymentFolder are required: a file without one of them is
 * silently left out of "Manage local XPP configurations" and Visual Studio cannot load metadata with it.
 */
export const TemplateDefaults: Record<string, unknown> = {
    AdminIdentityProvider: 'https://sts.windows.net/',
    AosWebsiteName: 'AOSService',
    ApplicationHostConfigFile: 'C:\\AOSService\\PackagesLocalDirectory\\bin\\applicationHost.config',
    AudienceUri: 'spn:00000015-0000-0000-c000-000000000000',
    AzureAppID: '',
    AzureCR_Key: null,
    BusinessDatabaseName: 'AxDbRain',
    BusinessDatabasePassword: '',
    BusinessDatabaseUserName: '',
    ContainerMemory: null,
    DatabaseServer: 'localhost',
    DefaultCompany: '',
    DefaultModelForNewProjects: 'FleetManagement',
    EnableOfflineAuthentication: true,
    ModuleExclusionList: null,
    OfflineAuthenticationAdminEmail: '',
    PartitionKey: 'initial',
    RunBatchWithinAOS: null,
    RuntimeHostType: 4,
    WebRoleDeploymentFolder: 'C:\\AOSService\\webroot',
};

/** Members Visual Studio requires (DataMember IsRequired) besides the folders setup always writes. */
export const RequiredMembers = ['AosWebsiteName', 'AudienceUri', 'DatabaseServer', 'DefaultModelForNewProjects', 'EnableOfflineAuthentication', 'WebRoleDeploymentFolder'];

/** Microsoft's template from the downloaded FinOps tools, else the same values built in; sample paths are left to the caller. */
export async function templateConfiguration(versionFolder?: string): Promise<Record<string, unknown>> {
    const template = versionFolder && path.join(versionFolder, 'FinOpsTools', 'Resources', 'Template_xppconfig.json');
    if (template && existsSync(template)) {
        try {
            const parsed = JSON.parse((await fs.readFile(template, 'utf8')).replace(/^\uFEFF/, ''));
            const result: Record<string, unknown> = { ...TemplateDefaults };
            for (const key of Object.keys(TemplateDefaults)) {
                if (key in parsed) {
                    result[key] = parsed[key];
                }
            }
            return result;
        } catch {
            // fall back to the built-in values
        }
    }
    return { ...TemplateDefaults };
}

/** Adds the template members a configuration lacks; existing values are kept. Returns the names added. */
export function completeConfiguration(config: Record<string, unknown>, template: Record<string, unknown> = TemplateDefaults): string[] {
    const added: string[] = [];
    for (const [key, value] of Object.entries(template)) {
        if (!(key in config)) {
            config[key] = value;
            added.push(key);
        }
    }
    return added;
}

/**
 * Configurations F&O Studio wrote before it included every template member: completes the file in place so Visual
 * Studio can read it. Only files F&O Studio generated are touched. Returns the members added (empty: nothing to do).
 */
export async function repairGeneratedConfiguration(file: string, versionFolder?: string): Promise<string[]> {
    const text = (await fs.readFile(file, 'utf8')).replace(/^\uFEFF/, '');
    const config = JSON.parse(text) as Record<string, unknown>;
    if (!/by (X\+\+|F&O Studio) for VS Code/i.test(String(config.Description ?? '')) || RequiredMembers.every(m => m in config)) {
        return [];
    }
    const added = completeConfiguration(config, await templateConfiguration(versionFolder));
    if (added.length) {
        await fs.writeFile(file, JSON.stringify(config, null, 4), 'utf8');
    }
    return added;
}

/** Completes every configuration F&O Studio generated in the XPPConfig folder. Returns file name → members added. */
export async function repairGeneratedConfigurations(configFolder: string): Promise<Map<string, string[]>> {
    const repaired = new Map<string, string[]>();
    const files = (await fs.readdir(configFolder).catch(() => [] as string[])).filter(f => f.toLowerCase().endsWith('.json'));
    for (const name of files) {
        const file = path.join(configFolder, name);
        try {
            const config = JSON.parse((await fs.readFile(file, 'utf8')).replace(/^\uFEFF/, ''));
            const framework = typeof config.FrameworkDirectory === 'string' ? config.FrameworkDirectory : undefined;
            const added = await repairGeneratedConfiguration(file, framework ? path.dirname(framework) : undefined);
            if (added.length) {
                repaired.set(name, added);
            }
        } catch {
            // not a configuration F&O Studio can read; leave it alone
        }
    }
    return repaired;
}
