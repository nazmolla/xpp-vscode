import { execFile, spawn } from 'child_process';
import { existsSync, promises as fs } from 'fs';
import * as path from 'path';

/** Identity of Microsoft's Finance and Operations tools for Visual Studio. */
export const FinOpsToolsId = 'DynamicsFnO.DeveloperTools';
/** The VSIX environment setup downloads into <version folder> and keeps after extracting it to FinOpsTools. */
export const FinOpsVsixName = 'Microsoft.Dynamics.FinOps.ToolsVS2022.vsix';

export interface VisualStudioInstall {
    instanceId: string;
    installationPath: string;
    installationVersion: string;
    displayName: string;
    /** Version of the FinOps tools installed in this Visual Studio, if any. */
    finOpsTools?: string;
}

/** The Identity Version in an extension.vsixmanifest, when its Id is the FinOps tools (Microsoft's manifest also holds a commented-out template Identity). */
export function finOpsManifestVersion(manifest: string): string | undefined {
    const idPattern = new RegExp(`\\bId="${FinOpsToolsId.replace(/\./g, '\\.')}"`, 'i');
    for (const identity of manifest.match(/<Identity\b[^>]*>/gi) ?? []) {
        if (idPattern.test(identity)) {
            return /\bVersion="([^"]+)"/i.exec(identity)?.[1];
        }
    }
    return undefined;
}

/** Version of the FinOps tools an environment needs: the ones F&O Studio extracted for it (<version>\FinOpsTools). */
export async function environmentFinOpsTools(versionFolder: string): Promise<string | undefined> {
    const manifest = path.join(versionFolder, 'FinOpsTools', 'extension.vsixmanifest');
    return existsSync(manifest) ? finOpsManifestVersion(await fs.readFile(manifest, 'utf8')) : undefined;
}

/** Visual Studio installations (vswhere) with the FinOps tools version each has, per machine or per user. */
export async function visualStudioInstalls(): Promise<VisualStudioInstall[]> {
    const vswhere = path.join(process.env['ProgramFiles(x86)'] ?? 'C:\\Program Files (x86)', 'Microsoft Visual Studio', 'Installer', 'vswhere.exe');
    if (!existsSync(vswhere)) {
        return [];
    }
    const output = await new Promise<string>(resolve => execFile(vswhere, ['-all', '-prerelease', '-format', 'json', '-utf8'], { windowsHide: true, timeout: 30_000 },
        (_err, stdout) => resolve(stdout ?? '')));
    let installs: VisualStudioInstall[];
    try {
        installs = (JSON.parse(output) as any[]).map(i => ({
            instanceId: i.instanceId, installationPath: i.installationPath, installationVersion: i.installationVersion, displayName: i.displayName,
        }));
    } catch {
        return [];
    }
    for (const install of installs) {
        const major = (install.installationVersion ?? '').split('.')[0];
        const folders = [
            path.join(install.installationPath, 'Common7', 'IDE', 'Extensions'),
            path.join(process.env.LOCALAPPDATA ?? '', 'Microsoft', 'VisualStudio', `${major}.0_${install.instanceId}`, 'Extensions'),
        ];
        install.finOpsTools = await findFinOpsTools(folders);
    }
    return installs;
}

/** Extensions sit in <Extensions>\<random>\ (machine) or <Extensions>\<publisher>\<name>\<version>\ (older per-user layout). */
async function findFinOpsTools(folders: string[]): Promise<string | undefined> {
    for (const folder of folders) {
        for (const manifest of await manifests(folder, 3)) {
            const text = await fs.readFile(manifest, 'utf8').catch(() => '');
            const version = text.includes(FinOpsToolsId) ? finOpsManifestVersion(text) : undefined;
            if (version) {
                return version;
            }
        }
    }
    return undefined;
}

async function manifests(folder: string, depth: number): Promise<string[]> {
    const entries = await fs.readdir(folder, { withFileTypes: true }).catch(() => []);
    const found: string[] = [];
    for (const entry of entries) {
        if (entry.isFile() && entry.name.toLowerCase() === 'extension.vsixmanifest') {
            found.push(path.join(folder, entry.name));
        } else if (entry.isDirectory() && depth > 1) {
            found.push(...await manifests(path.join(folder, entry.name), depth - 1));
        }
    }
    return found;
}

/** Visual Studio installations whose FinOps tools differ from the version the environment's metadata needs. */
export function mismatchedInstalls(installs: VisualStudioInstall[], needed: string): VisualStudioInstall[] {
    return installs.filter(i => i.finOpsTools && i.finOpsTools !== needed);
}

/**
 * Packs the environment's extracted FinOps tools (<version>\FinOpsTools, which setup keeps for the debugger proxy) into a
 * VSIX for the VSIX Installer: file entries only, forward slashes (OPC), built by the Windows tar.
 */
export async function packFinOpsVsix(versionFolder: string, target: string): Promise<void> {
    const source = path.join(versionFolder, 'FinOpsTools');
    const files: string[] = [];
    const walk = async (folder: string, prefix: string): Promise<void> => {
        for (const entry of await fs.readdir(folder, { withFileTypes: true })) {
            const relative = prefix ? `${prefix}/${entry.name}` : entry.name;
            if (entry.isDirectory()) {
                await walk(path.join(folder, entry.name), relative);
            } else if (entry.isFile()) {
                files.push(relative);
            }
        }
    };
    await walk(source, '');
    if (!files.includes('extension.vsixmanifest')) {
        throw new Error(`${source} does not hold the FinOps tools.`);
    }
    const list = target + '.files.txt';
    await fs.writeFile(list, files.join('\n'), 'utf8');
    await fs.rm(target, { force: true });
    try {
        await new Promise<void>((resolve, reject) => execFile(path.join(process.env.SystemRoot ?? 'C:\\Windows', 'System32', 'tar.exe'),
            ['--format', 'zip', '-n', '-cf', target, '-C', source, '-T', list], { windowsHide: true, timeout: 600_000 },
            (err, _stdout, stderr) => err ? reject(new Error(`Packing the FinOps tools failed: ${stderr || err.message}`)) : resolve()));
    } finally {
        await fs.rm(list, { force: true });
    }
}

/** Opens Visual Studio's VSIX Installer for the environment's FinOps tools; the user confirms (and elevates) there. */
export function launchVsixInstaller(install: VisualStudioInstall, vsix: string): void {
    const installer = path.join(install.installationPath, 'Common7', 'IDE', 'VSIXInstaller.exe');
    if (!existsSync(installer)) {
        throw new Error(`VSIXInstaller.exe was not found in ${install.installationPath}.`);
    }
    spawn(installer, [`/instanceIds:${install.instanceId}`, vsix], { detached: true, stdio: 'ignore' }).unref();
}
