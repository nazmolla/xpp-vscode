import { existsSync, promises as fs } from 'fs';
import * as os from 'os';
import * as path from 'path';
import { XppEnvironment } from '../environment';

/**
 * Compiles a package's reports into the RDL files the AOS reads at run time (<package>\Reports), as Visual Studio's build
 * does, with Microsoft's reportsc.exe. server/Host/ReportCompiler.cs follows the same rules; keep them alike.
 */

/** The package's report names (AxReport elements of every model in the package folder). */
export async function reportNames(packageFolder: string): Promise<string[]> {
    const names = new Map<string, string>();
    for (const model of await fs.readdir(packageFolder, { withFileTypes: true }).catch(() => [])) {
        if (!model.isDirectory()) {
            continue;
        }
        for (const file of await fs.readdir(path.join(packageFolder, model.name, 'AxReport')).catch(() => [] as string[])) {
            if (file.toLowerCase().endsWith('.xml')) {
                names.set(file.slice(0, -4).toLowerCase(), file.slice(0, -4));
            }
        }
    }
    return [...names.values()].sort((a, b) => a.localeCompare(b));
}

function isSharedStore(env: XppEnvironment): boolean {
    return env.referencePackagesPaths.some(p => path.resolve(p).toLowerCase() === path.resolve(env.modelStoreFolder).toLowerCase());
}

/**
 * reportsc reads one metadata root, so the standard packages and the custom ones must sit side by side. A shared store
 * (cloud-hosted and VHD environments) already is that root; otherwise a folder of junctions to every package is kept under
 * %LOCALAPPDATA%\FOStudio\ReportMetadata.
 */
export async function reportMetadataRoot(env: XppEnvironment, log: (line: string) => void): Promise<string> {
    if (isSharedStore(env)) {
        return env.modelStoreFolder;
    }
    // The configuration file's name, as the server names the same folder.
    const name = path.basename(env.configFile, path.extname(env.configFile)).replace(/[^\w.-]/g, '_');
    const root = path.join(process.env.LOCALAPPDATA ?? os.homedir(), 'FOStudio', 'ReportMetadata', name);
    await fs.mkdir(root, { recursive: true });

    // The custom folder wins over a reference package of the same name.
    const wanted = new Map<string, string>();
    for (const folder of [env.modelStoreFolder, ...env.referencePackagesPaths]) {
        for (const entry of await fs.readdir(folder, { withFileTypes: true }).catch(() => [])) {
            const key = entry.name.toLowerCase();
            if (entry.isDirectory() && !wanted.has(key) && existsSync(path.join(folder, entry.name, 'Descriptor'))) {
                wanted.set(key, path.join(folder, entry.name));
            }
        }
    }

    // Links are recorded in a manifest (name|target) so a build checks them without reading each junction's target.
    const manifest = path.join(root, 'links.txt');
    const recorded = new Map<string, string>();
    for (const line of (await fs.readFile(manifest, 'utf8').catch(() => '')).split(/\r?\n/)) {
        const bar = line.indexOf('|');
        if (bar > 0) {
            recorded.set(line.slice(0, bar).toLowerCase(), line.slice(bar + 1));
        }
    }
    for (const entry of await fs.readdir(root, { withFileTypes: true })) {
        const link = path.join(root, entry.name);
        if (!(await fs.lstat(link)).isSymbolicLink()) {
            continue;
        }
        const key = entry.name.toLowerCase();
        if (!wanted.has(key) || recorded.get(key)?.toLowerCase() !== wanted.get(key)!.toLowerCase()) {
            // unlink removes the junction itself, never what it points to.
            await fs.unlink(link);
        }
    }
    for (const [key, target] of [...wanted]) {
        const link = path.join(root, path.basename(target));
        if (!existsSync(link)) {
            await fs.symlink(target, link, 'junction').catch(error => log(`Could not link ${target} into ${root}: ${(error as Error).message}`));
        }
        if (!existsSync(link)) {
            wanted.delete(key);
        }
    }
    await fs.writeFile(manifest, [...wanted.values()].map(target => `${path.basename(target)}|${target}`).join('\r\n'));
    return root;
}

/** Compiled reports (Report.Design.rdl) of reports the package no longer has would still be deployed; they go. */
export async function removeStaleReports(packageFolder: string, names: string[], log: (line: string) => void): Promise<void> {
    const folder = path.join(packageFolder, 'Reports');
    const current = new Set(names.map(n => n.toLowerCase()));
    for (const file of await fs.readdir(folder).catch(() => [] as string[])) {
        if (file.toLowerCase().endsWith('.rdl') && !current.has(file.split('.')[0].toLowerCase())) {
            await fs.rm(path.join(folder, file), { force: true });
            log(`Removed ${file}: its report is no longer in the package.`);
        }
    }
}

/** reportsc's arguments for the package's reports, or undefined when the package has none. */
export async function reportCompilerArgs(env: XppEnvironment, pkg: string, packageFolder: string, logDir: string, xmlLog: string, log: (line: string) => void): Promise<string[] | undefined> {
    const names = await reportNames(packageFolder);
    await removeStaleReports(packageFolder, names, log);
    if (!names.length) {
        return undefined;
    }
    const root = await reportMetadataRoot(env, log);
    const output = path.join(packageFolder, 'Reports');
    await fs.mkdir(output, { recursive: true });
    return [
        `-metadata=${root}`,
        `-modelmodule=${pkg}`,
        // Always by name: without it reportsc first deletes every .rdl in the output folder.
        `-name=${names.join(',')}`,
        `-output=${output}`,
        `-LabelsPath=${root}`,
        `-log=${path.join(logDir, `Dynamics.AX.${pkg}.reportsc.log`)}`,
        `-xmllog=${xmlLog}`,
    ];
}
