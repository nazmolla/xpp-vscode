import { createHash } from 'crypto';
import { execFile } from 'child_process';
import { existsSync, promises as fs } from 'fs';
import * as os from 'os';
import * as path from 'path';
import * as vscode from 'vscode';

/**
 * Microsoft's report processing engine for the report preview (the Report Viewer runtime). F&O Studio doesn't ship it: it is
 * downloaded from nuget.org after the user accepts Microsoft's licence, checked against the hashes pinned here and in
 * server/Host/Reports/ReportRuntime.cs (keep them in step), and the server loads it only when every file matches.
 */

const Version = '150.1652.0';

interface RuntimePackage {
    id: string;
    version: string;
    sha512: string;
    licenseUrl: string;
    files: string[];
}

const Packages: RuntimePackage[] = [
    {
        id: 'Microsoft.ReportingServices.ReportViewerControl.Winforms',
        version: Version,
        sha512: '7ABFAB52A28E7250BFE2D96EAC7D17879326143D05E2124E4407E4C6BA75475A30CE3AEBA307F20340F5C7DF90AF49C29ADF411F6760E08FF487BA54C9E30B97',
        licenseUrl: 'https://go.microsoft.com/fwlink/?LinkId=826162',
        files: [
            'Microsoft.ReportViewer.Common.dll', 'Microsoft.ReportViewer.Common.resources.dll', 'Microsoft.ReportViewer.DataVisualization.dll',
            'Microsoft.ReportViewer.DataVisualization.resources.dll', 'Microsoft.ReportViewer.ProcessingObjectModel.dll',
            'Microsoft.ReportViewer.WinForms.dll', 'Microsoft.ReportViewer.WinForms.resources.dll',
        ],
    },
    {
        id: 'Microsoft.SqlServer.Types',
        version: '14.0.1016.290',
        sha512: '8F55E641C5FCB1A9B918FD22F1D100B4DD601F319A09A0DDB9DCAA7869ABBB6114D98F8EEBA5A6D0F226B990E79214C8796BD60F3FB33645DD74595FF11E2B26',
        licenseUrl: 'https://go.microsoft.com/fwlink/?linkid=842810',
        files: ['Microsoft.SqlServer.Types.dll'],
    },
];

const FileHashes: Record<string, string> = {
    'Microsoft.ReportViewer.Common.dll': '8BF52BD4CE91B38CB594253BC41C7029A0125AA7537A0553637DE5C09906CA18',
    'Microsoft.ReportViewer.Common.resources.dll': 'DECFEEB53F81550CD80E43B5A99FDCE1FCB8C195FE752E81B829648D405FD608',
    'Microsoft.ReportViewer.DataVisualization.dll': '23A342D494C13275CA646DE14F9F2BD350C7BC5477B6F17CDF3B0F69EB58DAC6',
    'Microsoft.ReportViewer.DataVisualization.resources.dll': '17AFD0731EDF134AB7CA888D54834135DACB36A1A9F3F352F383C7C31439A9F8',
    'Microsoft.ReportViewer.ProcessingObjectModel.dll': 'D27D058701D989930249647D249F62FE5AEB65CDBE5E14B2594839E931756A0E',
    'Microsoft.ReportViewer.WinForms.dll': '1B927C2D312E864EA73AD50711E5F6C2F58113869597E3ABF33D3B38DF5F23EA',
    'Microsoft.ReportViewer.WinForms.resources.dll': '4EE0941CEBDDE8BAC38AC8463EA78220A3E1C7086CB943406C1297BC38B43BAE',
    'Microsoft.SqlServer.Types.dll': '30108FF222575A0FEC8DEC65BC92D1B0773E1429ABEF918D969DFFEB7B5BB2CA',
};

/** Where the server looks for the runtime (%LOCALAPPDATA%\FOStudio\ReportViewer\<version>). */
export function reportRuntimeFolder(): string {
    return path.join(process.env.LOCALAPPDATA ?? path.join(os.homedir(), 'AppData', 'Local'), 'FOStudio', 'ReportViewer', Version);
}

async function sha(file: string, algorithm: 'sha256' | 'sha512'): Promise<string> {
    return createHash(algorithm).update(await fs.readFile(file)).digest('hex').toUpperCase();
}

/** True when every runtime file is there and matches its pinned hash. */
export async function isReportRuntimeInstalled(): Promise<boolean> {
    const folder = reportRuntimeFolder();
    for (const [file, hash] of Object.entries(FileHashes)) {
        const target = path.join(folder, file);
        if (!existsSync(target) || await sha(target, 'sha256') !== hash) {
            return false;
        }
    }
    return true;
}

/**
 * Makes sure the runtime is installed; asks for the licence and downloads it when it isn't. Returns false when the user
 * declines or it can't be installed.
 */
export async function ensureReportRuntime(): Promise<boolean> {
    if (await isReportRuntimeInstalled()) {
        return true;
    }
    for (;;) {
        const choice = await vscode.window.showInformationMessage(
            'The report preview uses Microsoft\'s report engine (the Report Viewer runtime), which F&O Studio does not include. '
            + `Install it now? F&O Studio downloads Microsoft.ReportingServices.ReportViewerControl.Winforms ${Version} and Microsoft.SqlServer.Types from nuget.org; `
            + 'by installing you accept Microsoft\'s licence terms for them.',
            { modal: true }, 'Accept Licence and Install', 'View Licence Terms');
        if (choice === 'View Licence Terms') {
            for (const p of Packages) {
                await vscode.env.openExternal(vscode.Uri.parse(p.licenseUrl));
            }
            continue;
        }
        if (choice !== 'Accept Licence and Install') {
            return false;
        }
        break;
    }
    await vscode.window.withProgress({ location: vscode.ProgressLocation.Notification, title: 'Installing the report preview runtime' }, async progress => {
        await installReportRuntime(message => progress.report({ message }));
    });
    vscode.window.showInformationMessage('Report preview runtime installed.');
    return true;
}

/** Downloads, verifies and extracts the runtime files; nothing is kept when a hash doesn't match. */
export async function installReportRuntime(report: (message: string) => void = () => undefined): Promise<void> {
    const folder = reportRuntimeFolder();
    const work = await fs.mkdtemp(path.join(os.tmpdir(), 'fos-reportviewer-'));
    try {
        for (const p of Packages) {
            report(`Downloading ${p.id} ${p.version}…`);
            const url = `https://api.nuget.org/v3-flatcontainer/${p.id.toLowerCase()}/${p.version}/${p.id.toLowerCase()}.${p.version}.nupkg`;
            const response = await fetch(url);
            if (!response.ok) {
                throw new Error(`Downloading ${p.id} from nuget.org failed: HTTP ${response.status}.`);
            }
            const nupkg = path.join(work, `${p.id}.nupkg`);
            await fs.writeFile(nupkg, new Uint8Array(await response.arrayBuffer()));
            if (await sha(nupkg, 'sha512') !== p.sha512) {
                throw new Error(`${p.id} ${p.version} from nuget.org is not the package F&O Studio expects (hash mismatch); nothing was installed.`);
            }
            report(`Extracting ${p.id}…`);
            await extractEntries(nupkg, p.files.map(f => `lib/net40/${f}`), work);
        }
        for (const file of Object.keys(FileHashes)) {
            if (await sha(path.join(work, file), 'sha256') !== FileHashes[file]) {
                throw new Error(`${file} doesn't match its expected hash; nothing was installed.`);
            }
        }
        await fs.mkdir(folder, { recursive: true });
        for (const file of Object.keys(FileHashes)) {
            await fs.copyFile(path.join(work, file), path.join(folder, file));
        }
        for (const p of Packages) {
            await fs.writeFile(path.join(folder, `${p.id}.licence.txt`), `${p.id} ${p.version}\r\nLicence terms: ${p.licenseUrl}\r\nDownloaded from nuget.org by F&O Studio after the user accepted the licence.\r\n`);
        }
    } finally {
        await fs.rm(work, { recursive: true, force: true });
    }
}

/** Extracts the named zip entries (flat, by file name) with .NET's ZipFile; entries are matched by exact name, so nothing else is written. */
function extractEntries(zip: string, entries: string[], target: string): Promise<void> {
    const script = `
Add-Type -AssemblyName System.IO.Compression.FileSystem
$names = $env:FOS_ENTRIES -split '\\|'
$zip = [IO.Compression.ZipFile]::OpenRead($env:FOS_ZIP)
try {
  foreach ($name in $names) {
    $entry = $zip.Entries | Where-Object { $_.FullName -eq $name } | Select-Object -First 1
    if (-not $entry) { throw "Missing $name" }
    [IO.Compression.ZipFileExtensions]::ExtractToFile($entry, (Join-Path $env:FOS_TARGET $entry.Name), $true)
  }
} finally { $zip.Dispose() }`;
    return new Promise((resolve, reject) => {
        execFile('powershell.exe', ['-NoProfile', '-NonInteractive', '-ExecutionPolicy', 'Bypass', '-Command', script], {
            windowsHide: true,
            env: { ...process.env, FOS_ZIP: zip, FOS_ENTRIES: entries.join('|'), FOS_TARGET: target },
        }, (error, _stdout, stderr) => error ? reject(new Error(`Extracting ${path.basename(zip)} failed: ${stderr || error.message}`)) : resolve());
    });
}
