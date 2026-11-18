import { existsSync } from 'fs';
import * as os from 'os';
import * as path from 'path';
import * as vscode from 'vscode';
import { readActiveEnvironment } from './environment';
import { environmentFinOpsTools, FinOpsVsixName, launchVsixInstaller, mismatchedInstalls, packFinOpsVsix, visualStudioInstalls } from './vsFinOpsTools';

/**
 * Optional, for people who also use Visual Studio on this machine: Visual Studio reads the same active configuration, and
 * when that moves to a new application version, Visual Studio's FinOps tools must match its metadata, or its Application
 * Explorer fails to open. F&O Studio itself never needs Visual Studio; this only looks for an installed Visual Studio
 * with the FinOps tools and does nothing when there is none.
 * interactive: the command; also reports when there is nothing to do.
 */
export async function checkVisualStudioTools(context: vscode.ExtensionContext, options: { interactive: boolean }): Promise<void> {
    const report = (message: string) => options.interactive && vscode.window.showInformationMessage(message);
    const withTools = (await visualStudioInstalls()).filter(i => i.finOpsTools);
    if (!withTools.length) {
        report('No Visual Studio with the Finance and Operations tools is installed on this machine; there is nothing to update.');
        return;
    }
    const env = await readActiveEnvironment().catch(() => undefined);
    const versionFolder = env ? path.dirname(env.frameworkDirectory) : undefined;
    const needed = versionFolder ? await environmentFinOpsTools(versionFolder) : undefined;
    if (!versionFolder || !needed) {
        report('The active configuration has no FinOps tools downloaded by F&O Studio (for example a cloud-hosted environment), so there is nothing to compare.');
        return;
    }
    const install = mismatchedInstalls(withTools, needed)[0];
    if (!install) {
        report(`Visual Studio's FinOps tools match this environment (${needed}).`);
        return;
    }
    const key = `xpp.vsToolsPrompt.${install.instanceId}.${install.finOpsTools}.${needed}`;
    if (!options.interactive && context.globalState.get<boolean>(key)) {
        return;
    }
    const choice = await vscode.window.showWarningMessage(
        `${install.displayName} has FinOps tools ${install.finOpsTools}, but the active configuration's metadata needs ${needed}. `
        + 'Visual Studio\'s Application Explorer will not load until they match. F&O Studio is not affected.',
        'Update Visual Studio', ...(options.interactive ? [] : ['Don\'t Ask Again']));
    if (choice === 'Don\'t Ask Again') {
        await context.globalState.update(key, true);
        return;
    }
    if (choice !== 'Update Visual Studio') {
        return;
    }
    try {
        // Setup keeps the downloaded VSIX; older setups deleted it, so pack one from the extracted tools then.
        const downloaded = path.join(versionFolder, FinOpsVsixName);
        const vsix = existsSync(downloaded) ? downloaded : path.join(os.tmpdir(), `FinOpsTools_${needed}.vsix`);
        if (!existsSync(vsix)) {
            await vscode.window.withProgress({ location: vscode.ProgressLocation.Notification, title: `Preparing FinOps tools ${needed}…` },
                () => packFinOpsVsix(versionFolder, vsix));
        }
        launchVsixInstaller(install, vsix);
        vscode.window.showInformationMessage('Close Visual Studio, then confirm the install in the VSIX Installer (it asks for administrator approval).');
    } catch (err) {
        vscode.window.showErrorMessage((err as Error).message);
    }
}
