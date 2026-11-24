import * as vscode from 'vscode';
import * as core from './core';
import { withAbortSignal } from './coreHost';
import { selectedDataverse } from './dataverse';
import { readActiveEnvironment } from './environment';
import { findActiveProject, readProject } from './project';
import { XppServerClient } from './serverClient';
import { DeployState } from './deployState';
import { showReportableError } from './reportProblem';

/**
 * X++ SysTest tests on the unified developer environment's cloud runtime (the runner is in @fno-studio/core, tests.ts):
 * the active project's model and classes, and the selected environment, for the agent tool, the command and the Test
 * Explorer.
 */

export { parseTrx } from './core';
export type { DiscoveredTest, TestOutcome, TestRunRequest } from './core';

/** The test classes of the request, or else those of the open project. */
export function discoverTests(server: XppServerClient, request: core.TestRunRequest, log: (line: string) => void): Promise<core.DiscoveredTest[]> {
    return core.discoverTests(server, request, { projectClasses, log: { line: log } });
}

async function projectClasses(): Promise<string[] | undefined> {
    const project = await readProject();
    return project?.items.filter(i => i.objectType === 'AxClass').map(i => i.objectName);
}

export async function runTests(server: XppServerClient, request: core.TestRunRequest, log: (line: string) => void, token?: vscode.CancellationToken): Promise<core.TestRunResult> {
    const env = await readActiveEnvironment();
    if (env.local) {
        throw new Error('Tests run through Dataverse, which only unified developer environments have. On a cloud-hosted or VHD environment, run them from Visual Studio on the VM.');
    }
    const project = await readProject(await findActiveProject());
    const model = project?.model;
    if (!model) {
        throw new Error('Open a D365FO project first: tests run from the package of the project\'s model.');
    }
    return withAbortSignal(token, signal => core.runTests(request, {
        reader: server,
        dataverse: selectedDataverse,
        model,
        runtimeRoot: env.runtimePackagesDirectory ?? env.frameworkDirectory,
        projectClasses,
        log: { line: log },
        signal,
    }));
}
interface RunTestsInput {
    classes?: string[];
    methods?: string[];
    timeoutMinutes?: number;
}

/** Registers the agent tool and the command; both use the same runner. */
export function registerTests(context: vscode.ExtensionContext, server: XppServerClient, deployState: DeployState): void {
    const output = vscode.window.createOutputChannel('X++ Tests');
    context.subscriptions.push(output);

    context.subscriptions.push(vscode.lm.registerTool<RunTestsInput>('xpp_run_tests', {
        prepareInvocation: options => ({
            invocationMessage: `Running X++ tests${options.input.classes?.length ? ' in ' + options.input.classes.join(', ') : ''} on the cloud runtime`,
            confirmationMessages: {
                title: 'Run X++ tests on the environment',
                message: 'The tests run on the connected environment against the deployed code and may create test data there.',
            },
        }),
        invoke: async (options, token) => {
            const lines: string[] = [];
            try {
                const result = await runTests(server, options.input, line => { lines.push(line); output.appendLine(line); }, token);
                output.appendLine(result.summary);
                const stale = await deployState.staleMessage();
                const summary = stale ? `WARNING: ${stale} The tests ran against the deployed code; deploy (Dynamics 365 > Deploy) and run them again to test the latest build.\n${result.summary}` : result.summary;
                return new vscode.LanguageModelToolResult([new vscode.LanguageModelTextPart(summary)]);
            } catch (err) {
                const message = `TEST RUN FAILED: ${(err as Error).message}\n${lines.slice(-5).join('\n')}`;
                output.appendLine(message);
                return new vscode.LanguageModelToolResult([new vscode.LanguageModelTextPart(message)]);
            }
        },
    }));

    context.subscriptions.push(vscode.commands.registerCommand('xpp.runTests', async (arg?: { objectName?: string; objectType?: string }) => {
        const classes = arg?.objectType === 'AxClass' && arg.objectName ? [arg.objectName] : undefined;
        if (!await deployState.confirmCurrent('Running tests')) {
            return;
        }
        output.show(true);
        await vscode.window.withProgress({ location: vscode.ProgressLocation.Notification, title: 'Running X++ tests', cancellable: true }, async (_progress, token) => {
            try {
                const result = await runTests(server, { classes }, line => output.appendLine(line), token);
                output.appendLine(result.summary);
                const failed = result.results.some(r => r.outcome !== 'Passed');
                void (failed ? vscode.window.showErrorMessage : vscode.window.showInformationMessage)(result.summary.split('\n')[0]);
            } catch (err) {
                output.appendLine(`Test run failed: ${(err as Error).message}`);
                showReportableError('testRun', err, 'Test run failed: ');
            }
        });
    }));
}
