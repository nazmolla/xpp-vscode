import * as vscode from 'vscode';
import * as core from './core';
import { withAbortSignal } from './coreHost';
import { selectedDataverse } from './dataverse';

/**
 * Deploy and database sync of the selected unified developer environment. The work is in @fno-studio/core (deploy.ts):
 * packages go up as built on this machine, through Dataverse, the way Visual Studio deploys them.
 */

export { builtPackages, dbSyncArgument, moduleDefinition, packageVersion, zipName, zipPackage } from './core';
export type { DbSync, DeployPackage } from './core';

export async function deployPackages(packages: core.DeployPackage[], dbSync: core.DbSync, log: core.Log, progress?: core.Progress, token?: vscode.CancellationToken): Promise<void> {
    const dv = await selectedDataverse();
    await withAbortSignal(token, signal => core.deployPackages(dv, packages, dbSync, { log, progress, signal }));
}

/** Database sync of the environment (Full, or Module for the given modules), without a deploy. */
export async function syncUdeDatabase(mode: 'Full' | 'Module', modules: string[], log: core.Log, progress?: core.Progress, token?: vscode.CancellationToken): Promise<void> {
    const dv = await selectedDataverse();
    await withAbortSignal(token, signal => core.syncDatabase(dv, mode, modules, { log, progress, signal }));
}
