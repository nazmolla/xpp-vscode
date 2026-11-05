import { spawn } from 'child_process';
import { existsSync, promises as fs } from 'fs';
import * as path from 'path';
import * as vscode from 'vscode';
import { DatabaseTarget } from './connection';

/** SQL Server Management Studio, newest first: SSMS 21+ installs under Release\, older versions under Common7 directly. */
async function findSsms(): Promise<string | undefined> {
    const roots = [process.env['ProgramFiles'], process.env['ProgramFiles(x86)']].filter((r): r is string => !!r);
    const candidates: string[] = [];
    for (const root of roots) {
        for (const folder of await fs.readdir(root).catch(() => [] as string[])) {
            if (/^Microsoft SQL Server Management Studio \d+$/i.test(folder)) {
                candidates.push(path.join(root, folder, 'Release', 'Common7', 'IDE', 'SSMS.exe'), path.join(root, folder, 'Common7', 'IDE', 'Ssms.exe'));
            }
        }
    }
    const version = (file: string) => Number(/Studio (\d+)/i.exec(file)?.[1] ?? 0);
    return candidates.filter(existsSync).sort((a, b) => version(b) - version(a))[0];
}

/** Opens SSMS connected to the database; with SQL credentials the password goes to the clipboard, never on a command line. */
export async function openInSsms(target: DatabaseTarget): Promise<void> {
    const ssms = await findSsms();
    if (!ssms) {
        throw new Error('SQL Server Management Studio was not found. Install it, or use New SQL Query here.');
    }
    const args = ['-S', target.server, '-d', target.database];
    if (target.credentials) {
        args.push('-U', target.credentials.user);
        await vscode.env.clipboard.writeText(target.credentials.password);
        vscode.window.showInformationMessage('The database password is on the clipboard: paste it in SSMS\'s sign-in, then clear the clipboard.');
    } else {
        args.push('-E');
    }
    const child = spawn(ssms, args, { detached: true, stdio: 'ignore' });
    child.on('error', err => vscode.window.showErrorMessage(`SSMS could not start: ${err.message}`));
    child.unref();
}
