import * as vscode from 'vscode';
import { changedElements, ChangedElement } from './changes';
import { DeployState } from './deployState';
import { readActiveEnvironment } from './environment';
import { XppServerClient } from './serverClient';

/**
 * F&O Studio: Verify in the Running App: a check list built from the changed elements (menu items to open, controls a
 * form extension adds, fields an entity extension adds), handed to Copilot's agent mode to run with the Dynamics 365 ERP
 * MCP server (form and data tools) against the deployed environment.
 */
export async function verifyInRunningApp(server: XppServerClient, deployState: DeployState): Promise<void> {
    const env = await readActiveEnvironment();
    if (!await deployState.confirmCurrent('The check')) {
        return;
    }
    const configured = vscode.workspace.getConfiguration('xpp').get<string>('git.baseBranch');
    const changes = (await changedElements(configured || 'main')).filter(c => c.status !== 'D');
    if (!changes.length) {
        vscode.window.showInformationMessage('No changed elements to verify (models must be in a Git repository).');
        return;
    }
    const steps = await vscode.window.withProgress({ location: vscode.ProgressLocation.Window, title: 'Planning the check…' }, () => planSteps(server, changes));
    if (!steps.length) {
        vscode.window.showInformationMessage('The changes have nothing a user can open (menu items, forms or data entities).');
        return;
    }
    const prompt = [
        `Verify my changes in the running Dynamics 365 Finance & Operations environment${env.finOpsUrl ? ` (${env.finOpsUrl})` : ''} with the Dynamics 365 ERP MCP tools (form and data tools).`,
        'Only read: do not save forms or change data. Close every form you open. For each step report PASS or FAIL with what you saw:',
        ...steps.map((s, i) => `${i + 1}. ${s}`),
        'End with a table: step, result, notes. If the ERP MCP tools are not available, say so and stop.',
    ].join('\n');
    await vscode.commands.executeCommand('workbench.action.chat.open', { query: prompt, mode: 'agent' });
}

async function planSteps(server: XppServerClient, changes: ChangedElement[]): Promise<string[]> {
    const steps: string[] = [];
    for (const c of changes) {
        if (/^AxMenuItem(Display|Output|Action)$/.test(c.objectType)) {
            steps.push(`Open menu item ${c.objectName} (${c.objectType.replace('AxMenuItem', '').toLowerCase()}); confirm it opens without an error.`);
        } else if (c.objectType === 'AxFormExtension') {
            const form = c.objectName.split('.')[0];
            const read = await server.bridge('read_object', { objectType: c.objectType, objectName: c.objectName }).catch(() => undefined);
            const controls = collectNames(read?.controls ?? read?.Controls ?? []);
            steps.push(`Open form ${form} (through its menu item)${controls.length ? ` and find the controls ${controls.slice(0, 10).join(', ')}` : ''}; confirm they are visible.`);
        } else if (c.objectType === 'AxDataEntityViewExtension') {
            const entity = c.objectName.split('.')[0];
            const read = await server.bridge('read_object', { objectType: c.objectType, objectName: c.objectName }).catch(() => undefined);
            const fields = collectNames(read?.fields ?? read?.Fields ?? []);
            steps.push(`Read one record of data entity ${entity}${fields.length ? ` and confirm the fields ${fields.slice(0, 10).join(', ')} are returned` : ''}.`);
        } else if (c.objectType === 'AxDataEntityView' && c.status === 'A') {
            steps.push(`Read one record of the new data entity ${c.objectName}.`);
        } else if (c.objectType === 'AxForm' && c.status === 'A') {
            steps.push(`Open the new form ${c.objectName} through its menu item; confirm it opens without an error.`);
        }
    }
    return steps;
}

function collectNames(items: unknown[]): string[] {
    return (items as { name?: string; Name?: string }[]).map(i => i?.name ?? i?.Name).filter((n): n is string => !!n);
}
