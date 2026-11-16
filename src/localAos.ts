import { execFile, spawn } from 'child_process';
import { existsSync, promises as fs } from 'fs';
import * as os from 'os';
import * as path from 'path';

/**
 * The AOS of a cloud-hosted or VHD environment, found the way its own tools record it: Visual Studio's Dynamics 365
 * options (DynamicsDevConfig.xml), the batch service, and the AOS web.config. Nothing here assumes a drive.
 */
export interface LocalAos {
    /** PackagesLocalDirectory. */
    packagesFolder: string;
    /** The AOS web.config, when found. */
    webConfig?: string;
    /** appSettings of the web.config (key -> value). */
    settings: Map<string, string>;
    /** Visual Studio's Dynamics 365 option "Runtime host type": IIS or IISExpress (what the developer chose). */
    runtimeHostType: string;
    /** The IIS site of the AOS (AosWebsiteName), AOSService by default. */
    aosWebsiteName: string;
}

export interface AosProcess {
    name: string;
    pid: number;
    /** Where the process comes from, e.g. "IIS site AOSService, application pool AOSService". */
    description?: string;
}

const DevelopmentKey = 'HKLM\\SOFTWARE\\Microsoft\\Dynamics\\AX\\Development';
const BatchServiceKey = 'HKLM\\SYSTEM\\CurrentControlSet\\Services\\DynamicsAxBatch';
const ShellFoldersKey = 'HKCU\\Software\\Microsoft\\Windows\\CurrentVersion\\Explorer\\Shell Folders';

let cached: Promise<LocalAos | undefined> | undefined;

/** The local AOS, or undefined when this machine has none (e.g. a unified developer environment's workstation). */
export function discoverLocalAos(refresh = false): Promise<LocalAos | undefined> {
    if (!cached || refresh) {
        cached = findLocalAos().catch(() => undefined);
    }
    return cached;
}

async function findLocalAos(): Promise<LocalAos | undefined> {
    // Visual Studio reads the user's options and falls back to the machine default (DefaultConfig, in PackagesLocalDirectory\bin).
    const defaultConfig = await readRegistryValue(DevelopmentKey, 'DefaultConfig');
    const documents = await readRegistryValue(ShellFoldersKey, 'Personal') ?? path.join(os.homedir(), 'Documents');
    const devConfigs = [path.join(documents, 'Visual Studio Dynamics 365', 'DynamicsDevConfig.xml'), defaultConfig]
        .filter((f): f is string => !!f && existsSync(f));
    const devText = await Promise.all(devConfigs.map(f => fs.readFile(f, 'utf8').catch(() => '')));
    const option = (name: string) => devText.map(t => xmlValue(t, name)).find(v => !!v);

    // The AOS web.config: next to the web role Visual Studio deploys to, or the one the batch service runs with.
    const webConfigs: string[] = [];
    const webRole = option('WebRoleDeploymentFolder');
    if (webRole) {
        webConfigs.push(path.join(webRole, 'web.config'));
    }
    const batch = await readRegistryValue(BatchServiceKey, 'ImagePath');
    const serviceConfig = batch ? /-service\s+"?([^"]+?\.config)"?\s*$/i.exec(batch)?.[1] : undefined;
    if (serviceConfig) {
        webConfigs.push(serviceConfig);
    }
    const webConfig = webConfigs.find(f => existsSync(f));
    const settings = webConfig ? parseAppSettings(await fs.readFile(webConfig, 'utf8').catch(() => '')) : new Map<string, string>();

    const candidates = [
        settings.get('aos.packagedirectory'),
        settings.get('common.bindir'),
        // DefaultConfig is PackagesLocalDirectory\bin\DynamicsDevConfig.xml.
        defaultConfig ? path.dirname(path.dirname(defaultConfig)) : undefined,
    ];
    const packagesFolder = candidates.find((c): c is string => !!c && existsSync(path.join(c, 'bin', 'xppc.exe')));
    if (!packagesFolder) {
        return undefined;
    }
    return {
        packagesFolder: packagesFolder.replace(/[\\/]+$/, ''),
        webConfig,
        settings,
        runtimeHostType: option('RuntimeHostType') ?? 'IIS',
        aosWebsiteName: option('AosWebsiteName') ?? 'AOSService',
    };
}

/**
 * The processes Visual Studio attaches to on F5 for the developer's runtime host type: for IIS the worker processes of
 * the AOS site's application pool, for IIS Express its processes. A process name (e.g. Batch.exe) instead attaches to
 * that process, as Visual Studio's Attach to Process does.
 */
export async function aosHostProcesses(aos: LocalAos, processName?: string): Promise<AosProcess[]> {
    if (processName) {
        const name = /\.exe$/i.test(processName) ? processName : `${processName}.exe`;
        return processesByName(name);
    }
    if (/^iisexpress$/i.test(aos.runtimeHostType)) {
        return (await processesByName('iisexpress.exe')).map(p => ({ ...p, description: 'IIS Express' }));
    }
    if (!existsSync(appcmdPath())) {
        throw new Error('IIS is not installed on this machine (appcmd.exe not found). If the AOS runs in IIS Express, set Runtime host type to IISExpress in Visual Studio\'s Dynamics 365 options.');
    }
    const result: AosProcess[] = [];
    for (const { site, pool, workers } of await aosApplicationPools(aos)) {
        for (const pid of workers.filter(w => !result.some(r => r.pid === w))) {
            result.push({ name: 'w3wp.exe', pid, description: `IIS site ${site}, application pool ${pool}` });
        }
    }
    return result;
}

function appcmdPath(): string {
    return path.join(process.env.windir ?? 'C:\\Windows', 'System32', 'inetsrv', 'appcmd.exe');
}

/** The IIS sites of the AOS (AosWebsiteName), their application pool and its running worker processes. */
async function aosApplicationPools(aos: LocalAos): Promise<{ site: string; pool: string; workers: number[] }[]> {
    const appcmd = appcmdPath();
    if (!existsSync(appcmd)) {
        return [];
    }
    const sites = parseAppcmd(await run(appcmd, ['list', 'site']), 'SITE')
        .filter(s => s.name.toLowerCase().includes(aos.aosWebsiteName.toLowerCase()));
    const result: { site: string; pool: string; workers: number[] }[] = [];
    for (const site of sites) {
        const pool = parseAppcmd(await run(appcmd, ['list', 'app', `/site.name:${site.name}`]), 'APP')[0]?.attributes.get('applicationpool');
        if (pool) {
            const workers = parseAppcmd(await run(appcmd, ['list', 'wp', `/apppool.name:${pool}`]), 'WP').map(wp => Number(wp.name)).filter(pid => pid > 0);
            result.push({ site: site.name, pool, workers });
        }
    }
    return result;
}

const BatchService = 'DynamicsAxBatch';

/**
 * After a build the AOS still runs the old assemblies. As Visual Studio does after every build, the hosts that run the
 * AOS now are restarted so they load the new ones: IIS Express (stopped and started again with its own command line),
 * the AOS site's application pool in IIS (recycled), and the batch service. Hosts that are not running are left alone;
 * they load the new build when they start. Each step is reported through log; a failed step doesn't stop the others.
 */
export async function restartLocalAos(aos: LocalAos, log: (line: string) => void): Promise<boolean> {
    let failed = false;
    const step = async (what: string, action: () => Promise<void>) => {
        try {
            await action();
            log(`${what}: done.`);
        } catch (error) {
            failed = true;
            const message = (error as Error).message;
            log(`${what}: FAILED. ${message}${/denied|0x80070005|elevat/i.test(message) ? ' Run VS Code as administrator, as Visual Studio must be on this environment.' : ''}`);
        }
    };
    for (const host of await processesByName('iisexpress.exe')) {
        await step(`Restarting IIS Express (process ${host.pid})`, () => restartIisExpress(host.pid));
    }
    for (const { site, pool, workers } of await aosApplicationPools(aos).catch(() => [])) {
        if (workers.length) {
            await step(`Recycling application pool ${pool} (IIS site ${site})`, async () => { await run(appcmdPath(), ['recycle', 'apppool', `/apppool.name:${pool}`]); });
        }
    }
    if (/STATE\s*:\s*4\s+RUNNING/i.test(await run('sc', ['query', BatchService]).catch(() => ''))) {
        await step(`Restarting the batch service (${BatchService})`, async () => {
            // net stop waits until the service has stopped; the batch service finishes its running tasks first.
            await run('net', ['stop', BatchService], 300_000);
            await run('net', ['start', BatchService], 120_000);
        });
    }
    return !failed;
}

/** Stops IIS Express and starts it again as it was started (Visual Studio starts it with the AOS's applicationHost.config). */
async function restartIisExpress(pid: number): Promise<void> {
    const commandLine = (await run('powershell.exe', ['-NoProfile', '-NonInteractive', '-Command',
        `(Get-CimInstance Win32_Process -Filter "ProcessId=${Math.trunc(pid)}").CommandLine`]).catch(() => '')).trim();
    const quoted = /^"([^"]+)"\s*(.*)$/.exec(commandLine);
    const plain = quoted ? undefined : /^(\S+)\s*(.*)$/.exec(commandLine);
    let executable = quoted?.[1] ?? plain?.[1];
    let args = quoted?.[2] ?? plain?.[2] ?? '';
    if (!executable || !/iisexpress\.exe$/i.test(executable)) {
        // Visual Studio's own command line (xppcAgent's StartIISExpress).
        const documents = await readRegistryValue(ShellFoldersKey, 'Personal') ?? path.join(os.homedir(), 'Documents');
        executable = path.join(process.env.ProgramW6432 ?? process.env.ProgramFiles ?? 'C:\\Program Files', 'IIS Express', 'iisexpress.exe');
        args = `/config:"${path.join(documents, 'IISExpress', 'config', 'applicationHost.config')}" /apppool:Dynamics365`;
    }
    process.kill(pid);
    for (let i = 0; i < 40 && (await processesByName('iisexpress.exe')).some(p => p.pid === pid); i++) {
        await new Promise(resolve => setTimeout(resolve, 250));
    }
    const child = spawn(executable, args ? [args] : [], {
        argv0: `"${executable}"`, windowsVerbatimArguments: true, detached: true, stdio: 'ignore', windowsHide: true,
    });
    await new Promise<void>((resolve, reject) => {
        child.once('error', reject);
        child.once('spawn', () => resolve());
    });
    child.unref();
}

/** Lines such as: WP "9172" (applicationPool:AOSService) -> name and lower-case attributes. */
export function parseAppcmd(output: string, kind: string): { name: string; attributes: Map<string, string> }[] {
    const items: { name: string; attributes: Map<string, string> }[] = [];
    for (const line of output.split(/\r?\n/)) {
        const match = new RegExp(`^${kind}\\s+"([^"]*)"\\s*(?:\\((.*)\\))?`, 'i').exec(line.trim());
        if (match) {
            const attributes = new Map<string, string>();
            for (const part of (match[2] ?? '').split(',')) {
                const at = part.indexOf(':');
                if (at > 0) {
                    attributes.set(part.slice(0, at).trim().toLowerCase(), part.slice(at + 1).trim());
                }
            }
            items.push({ name: match[1], attributes });
        }
    }
    return items;
}

/** appSettings keys in lower case. */
export function parseAppSettings(webConfig: string): Map<string, string> {
    const settings = new Map<string, string>();
    for (const match of webConfig.matchAll(/<add\s+key="([^"]+)"\s+value="([^"]*)"/gi)) {
        settings.set(match[1].toLowerCase(), decodeXml(match[2]));
    }
    return settings;
}

function xmlValue(xml: string, element: string): string | undefined {
    const value = new RegExp(`<${element}>([^<]*)</${element}>`, 'i').exec(xml)?.[1]?.trim();
    return value ? decodeXml(value) : undefined;
}

function decodeXml(text: string): string {
    return text.replace(/&lt;/g, '<').replace(/&gt;/g, '>').replace(/&quot;/g, '"').replace(/&apos;/g, '\'').replace(/&amp;/g, '&');
}

async function processesByName(image: string): Promise<{ name: string; pid: number }[]> {
    const output = await run('tasklist', ['/FI', `IMAGENAME eq ${image}`, '/FO', 'CSV', '/NH']).catch(() => '');
    return output.split(/\r?\n/).map(line => /^"([^"]+)","(\d+)"/.exec(line)).filter((m): m is RegExpExecArray => !!m)
        .filter(m => m[1].toLowerCase() === image.toLowerCase()).map(m => ({ name: m[1], pid: Number(m[2]) }));
}

function readRegistryValue(key: string, name: string): Promise<string | undefined> {
    return run('reg', ['query', key, '/v', name]).then(stdout => {
        const match = new RegExp(`^\\s*${name}\\s+REG_\\w+\\s+(.*)$`, 'im').exec(stdout);
        return match ? match[1].trim() : undefined;
    }, () => undefined);
}

function run(file: string, args: string[], timeout = 30_000): Promise<string> {
    return new Promise((resolve, reject) => execFile(file, args, { windowsHide: true, timeout }, (error, stdout, stderr) => {
        if (error) {
            reject(new Error(`${path.basename(file)} ${args.join(' ')}: ${(stderr || stdout || error.message).toString().trim()}`));
        } else {
            resolve(stdout.toString());
        }
    }));
}
