import { promises as fs } from 'fs';
import * as path from 'path';

/**
 * Names of the package folders in a metadata store, including packages linked in with a junction or a directory
 * symbolic link (Node reports those as links, not directories; Visual Studio follows them).
 */
export async function packageFolders(store: string): Promise<string[]> {
    const entries = await fs.readdir(store, { withFileTypes: true }).catch(() => []);
    const names: string[] = [];
    for (const entry of entries) {
        if (entry.isDirectory() || (entry.isSymbolicLink() && (await fs.stat(path.join(store, entry.name)).catch(() => undefined))?.isDirectory())) {
            names.push(entry.name);
        }
    }
    return names;
}
