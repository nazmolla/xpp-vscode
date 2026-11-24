import * as vscode from 'vscode';
import { PhaseId, PhaseState, runUdeSetup, SetupPhases, UdeEnvironment } from './core';
import { withAbortSignal } from './coreHost';
import { notifyEnvironmentChanged, readActiveEnvironment, selectedEnvironmentUrl, useEnvironment } from './environment';
import { dataverseTokens, resolveDataverseUrl, validateDataverseUrl } from './ude';
import { recordError } from './reportProblem';

export { SetupPhases } from './core';
export type { PhaseId, PhaseState } from './core';

type Step = (message: string, phase?: PhaseId) => void;

/** The last or running Connect to Environment, for the Environment group of the Dynamics 365 view. */
export interface SetupStatus {
    dataverseUrl: string;
    running: boolean;
    phases: { id: PhaseId; label: string; state: PhaseState; detail?: string }[];
    error?: string;
}

const setupChanged = new vscode.EventEmitter<SetupStatus | undefined>();
export const onDidChangeSetup = setupChanged.event;
let status: SetupStatus | undefined;

export function setupStatus(): SetupStatus | undefined {
    return status;
}

export function clearSetupStatus(): void {
    status = undefined;
    setupChanged.fire(undefined);
}

function setPhase(id: PhaseId, detail?: string): void {
    if (!status) {
        return;
    }
    const index = status.phases.findIndex(p => p.id === id);
    status.phases.forEach((p, i) => {
        if (i < index && p.state !== 'done') {
            p.state = 'done';
            p.detail = undefined;
        }
    });
    status.phases[index].state = 'running';
    status.phases[index].detail = detail;
    setupChanged.fire(status);
}

/**
 * F&O Studio: Connect to Environment — the same client setup Visual Studio's Power Platform Tools perform:
 * download system metadata, cross-reference database and FinOps tools, extract, restore, configure.
 */
export async function connectEnvironment(context: vscode.ExtensionContext, log: vscode.OutputChannel, retryUrl?: string): Promise<boolean> {
    const input = retryUrl ?? await vscode.window.showInputBox({
        title: 'Connect to a unified developer environment',
        prompt: 'Dataverse URL of the environment',
        placeHolder: 'https://yourorg.crm.dynamics.com',
        value: selectedEnvironmentUrl(),
        ignoreFocusOut: true,
        validateInput: validateDataverseUrl,
    });
    if (!input) {
        return false;
    }
    let dataverseUrl: string;
    try {
        dataverseUrl = await resolveDataverseUrl(input);
    } catch (err) {
        vscode.window.showErrorMessage((err as Error).message);
        return false;
    }
    log.show(true);
    status = { dataverseUrl, running: true, phases: SetupPhases.map(p => ({ ...p, state: 'pending' as PhaseState })) };
    setupChanged.fire(status);
    void vscode.commands.executeCommand('xpp.tools.focus').then(undefined, () => undefined);

    return (await vscode.window.withProgress(
        { location: vscode.ProgressLocation.Notification, title: 'X++ environment setup', cancellable: true },
        async (progress, token) => {
            const step: Step = (message, phase) => {
                log.appendLine(`${new Date().toLocaleTimeString()} ${message}`);
                const index = SetupPhases.findIndex(p => p.id === phase);
                progress.report({ message: index >= 0 ? `Step ${index + 1}/${SetupPhases.length}: ${message}` : message });
                if (phase) {
                    setPhase(phase, message);
                } else if (status) {
                    const running = status.phases.find(p => p.state === 'running');
                    if (running) {
                        running.detail = message;
                        setupChanged.fire(status);
                    }
                }
            };
            try {
                await runSetup(context, dataverseUrl, step, token);
                const before = selectedEnvironmentUrl();
                await useEnvironment(dataverseUrl);
                // A reconnect of the same environment rewrote its configuration; useEnvironment only fires on a switch.
                if (before === dataverseUrl) {
                    notifyEnvironmentChanged();
                }
                status!.phases.forEach(p => { p.state = 'done'; p.detail = undefined; });
                status!.running = false;
                setupChanged.fire(status);
                return true;
            } catch (err) {
                const message = (err as Error).message;
                step(`FAILED: ${message}`);
                const running = status!.phases.find(p => p.state === 'running');
                if (running) {
                    running.state = 'failed';
                    running.detail = message;
                }
                status!.running = false;
                status!.error = message;
                setupChanged.fire(status);
                recordError('connectEnvironment', err as Error);
                const choice = await vscode.window.showErrorMessage(`Environment setup failed: ${message}`, 'Retry', 'Show Log', 'Report Problem');
                if (choice === 'Retry') {
                    void vscode.commands.executeCommand('xpp.connectEnvironment', dataverseUrl);
                } else if (choice === 'Show Log') {
                    log.show(true);
                } else if (choice === 'Report Problem') {
                    void vscode.commands.executeCommand('xpp.reportProblem', 'connectEnvironment');
                }
                return false;
            }
        })) ?? false;
}

function runSetup(context: vscode.ExtensionContext, dataverseUrl: string, step: Step, token: vscode.CancellationToken): Promise<string> {
    return withAbortSignal(token, signal => runUdeSetup(dataverseUrl, {
        tokens: dataverseTokens,
        serverExe: context.asAbsolutePath('server/bin/XppVsCode.Server.exe'),
        progress: { report: step },
        signal,
        activeEnvironment: readActiveEnvironment,
    }));
}

export type { UdeEnvironment };