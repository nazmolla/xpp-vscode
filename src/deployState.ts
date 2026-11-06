import * as vscode from 'vscode';
import { XppBuilder } from './build';
import { readActiveEnvironment } from './environment';
import { readProject } from './project';

interface ModelState { builtAt?: number; deployedAt?: number }

const StateKey = 'xpp.deployState';

/**
 * Built versus deployed, per environment and model. A unified environment runs the code last deployed to it, so tests,
 * the debugger and Run Snippet see old code after a build until the next deploy. Deploys are those run from this machine
 * (pac); cloud-hosted and VHD environments run what was built.
 */
export class DeployState implements vscode.Disposable {
    private readonly changed = new vscode.EventEmitter<void>();
    readonly onDidChange = this.changed.event;
    private readonly subscription: vscode.Disposable;

    constructor(private readonly context: vscode.ExtensionContext, builder: XppBuilder) {
        this.subscription = builder.onDidFinish(result => {
            if (result.ok) {
                void this.record(result.model, { builtAt: result.time.getTime() });
            }
        });
    }

    dispose(): void {
        this.subscription.dispose();
        this.changed.dispose();
    }

    async deployed(models: string[]): Promise<void> {
        const now = Date.now();
        for (const model of models) {
            await this.record(model, { deployedAt: now });
        }
    }

    /** Undefined on cloud-hosted/VHD environments and for models never built in this environment. */
    async of(model: string): Promise<(ModelState & { stale: boolean }) | undefined> {
        const env = await readActiveEnvironment().catch(() => undefined);
        if (!env || env.local) {
            return undefined;
        }
        const state = this.all()[key(env.name, model)];
        if (!state?.builtAt) {
            return undefined;
        }
        return { ...state, stale: !state.deployedAt || state.builtAt > state.deployedAt };
    }

    /**
     * Before running code on the environment: when the project's model was built after its last deploy, asks whether to
     * deploy first. False when the user cancels.
     */
    async confirmCurrent(action: string): Promise<boolean> {
        const message = await this.staleMessage();
        if (!message) {
            return true;
        }
        const choice = await vscode.window.showWarningMessage(`${message} ${action} uses the deployed code.`, 'Deploy First', 'Continue');
        if (choice === 'Deploy First') {
            await vscode.commands.executeCommand('xpp.pac.deploy');
            return false;
        }
        return choice === 'Continue';
    }

    /** For agents: one line when the deployed code is older than the last build. */
    async staleMessage(): Promise<string | undefined> {
        const project = await readProject().catch(() => undefined);
        if (!project?.model) {
            return undefined;
        }
        const state = await this.of(project.model);
        if (!state?.stale) {
            return undefined;
        }
        const built = new Date(state.builtAt!).toLocaleString();
        return state.deployedAt
            ? `${project.model} was built (${built}) after its last deploy (${new Date(state.deployedAt).toLocaleString()}).`
            : `${project.model} was built (${built}) but not deployed from this machine yet.`;
    }

    private all(): Record<string, ModelState> {
        return this.context.globalState.get<Record<string, ModelState>>(StateKey, {});
    }

    private async record(model: string, update: ModelState): Promise<void> {
        const env = await readActiveEnvironment().catch(() => undefined);
        if (!env) {
            return;
        }
        const all = this.all();
        const k = key(env.name, model);
        await this.context.globalState.update(StateKey, { ...all, [k]: { ...all[k], ...update } });
        this.changed.fire();
    }
}

function key(environment: string, model: string): string {
    return `${environment}/${model}`.toLowerCase();
}
