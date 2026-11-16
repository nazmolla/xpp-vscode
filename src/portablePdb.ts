import { execFile } from 'child_process';
import { existsSync, promises as fs } from 'fs';
import * as path from 'path';

/** Windows PDBs (MSF 7.00) start with this; portable PDBs start with BSJB. */
const WindowsPdbSignature = 'Microsoft C/C++ MSF 7.00';
const Parallel = 4;

export interface PdbConversion {
    converted: string[];
    failed: { file: string; error: string }[];
}

/** Pdb2Pdb as shipped in the extension (tools\pdb2pdb, fetched by scripts/fetch-pdb2pdb.ps1). */
export function pdb2pdbPath(extensionPath: string): string {
    return path.join(extensionPath, 'tools', 'pdb2pdb', 'Pdb2Pdb.exe');
}

export async function isWindowsPdb(file: string): Promise<boolean> {
    const handle = await fs.open(file, 'r');
    try {
        const buffer = Buffer.alloc(WindowsPdbSignature.length);
        const { bytesRead } = await handle.read(buffer, 0, buffer.length, 0);
        return bytesRead === buffer.length && buffer.toString('latin1') === WindowsPdbSignature;
    } finally {
        await handle.close();
    }
}

/**
 * xppc writes Windows PDBs (its PDB writer is the COM one, which writes no other format), and the C# extension's
 * debugger reads only portable PDBs. Every Windows PDB in a package's bin folder is converted in place; Visual
 * Studio's debugger reads both formats, and Microsoft's own packages already ship portable PDBs. A PDB the AOS has
 * open cannot be overwritten but can be renamed: it is moved aside to <package>\BuildLogs\ReplacedPdbs and deleted
 * there once it is released (on a later conversion).
 */
export async function convertPackagePdbs(tool: string, binFolder: string): Promise<PdbConversion> {
    const result: PdbConversion = { converted: [], failed: [] };
    if (!existsSync(tool)) {
        result.failed.push({ file: tool, error: 'Pdb2Pdb is missing from the extension (tools\\pdb2pdb).' });
        return result;
    }
    const aside = path.join(path.dirname(binFolder), 'BuildLogs', 'ReplacedPdbs');
    for (const old of await fs.readdir(aside).catch(() => [] as string[])) {
        await fs.rm(path.join(aside, old), { force: true }).catch(() => undefined);
    }
    const files = await fs.readdir(binFolder).catch(() => [] as string[]);
    const pending: { pdb: string; module: string }[] = [];
    for (const file of files.filter(f => f.toLowerCase().endsWith('.pdb'))) {
        const pdb = path.join(binFolder, file);
        const base = file.slice(0, -4);
        const module = ['.netmodule', '.dll'].map(ext => path.join(binFolder, base + ext)).find(m => existsSync(m));
        if (module && await isWindowsPdb(pdb).catch(() => false)) {
            pending.push({ pdb, module });
        }
    }
    if (pending.length === 0) {
        return result;
    }
    await fs.mkdir(aside, { recursive: true });
    const work = async () => {
        for (let next = pending.shift(); next; next = pending.shift()) {
            try {
                await convertOne(tool, next.module, next.pdb, aside);
                result.converted.push(next.pdb);
            } catch (error) {
                result.failed.push({ file: next.pdb, error: (error as Error).message });
            }
        }
    };
    await Promise.all(Array.from({ length: Parallel }, work));
    return result;
}

async function convertOne(tool: string, module: string, pdb: string, aside: string): Promise<void> {
    const name = path.basename(pdb);
    const converted = path.join(aside, `${name}.${process.pid}.${Date.now()}.portable`);
    await new Promise<void>((resolve, reject) => execFile(tool, [module, '/pdb', pdb, '/out', converted],
        { windowsHide: true, timeout: 120_000 }, (error, stdout, stderr) => {
            if (error) {
                reject(new Error((stderr || stdout || error.message).toString().trim()));
            } else {
                resolve();
            }
        }));
    // Same volume (the package folder), so these are renames.
    try {
        await fs.rename(converted, pdb);
        return;
    } catch {
        // The AOS (or a debugger) has the old PDB open: move it aside first.
    }
    const old = path.join(aside, `${name}.${process.pid}.${Date.now()}.old`);
    try {
        await fs.rename(pdb, old);
    } catch (error) {
        await fs.rm(converted, { force: true }).catch(() => undefined);
        throw error;
    }
    try {
        await fs.rename(converted, pdb);
    } catch (error) {
        await fs.rename(old, pdb).catch(() => undefined);
        throw error;
    }
    await fs.rm(old, { force: true }).catch(() => undefined);
}
