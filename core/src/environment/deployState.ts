/**
 * Built versus deployed, per environment and model. A unified environment runs the code last deployed to it, so tests,
 * the debugger and snippets see old code after a build until the next deploy. The host stores the states (deployStateKey)
 * and records builds and deploys made from this machine.
 */

export interface ModelDeployState {
    /** Last successful build (ms since 1970). */
    builtAt?: number;
    /** Last deploy from this machine (ms since 1970). */
    deployedAt?: number;
}

export interface ModelStaleness extends ModelDeployState {
    /** Built after the last deploy, or never deployed. */
    stale: boolean;
}

/** Storage key of a model's state in an environment. */
export function deployStateKey(environment: string, model: string): string {
    return `${environment}/${model}`.toLowerCase();
}

/** Undefined for a model never built in this environment. */
export function deployStaleness(state: ModelDeployState | undefined): ModelStaleness | undefined {
    if (!state?.builtAt) {
        return undefined;
    }
    return { ...state, stale: !state.deployedAt || state.builtAt > state.deployedAt };
}

/** One line for a model whose deployed code is older than its last build; undefined when it is current. */
export function staleDeployMessage(model: string, state: ModelStaleness | undefined): string | undefined {
    if (!state?.stale) {
        return undefined;
    }
    const built = new Date(state.builtAt!).toLocaleString();
    return state.deployedAt
        ? `${model} was built (${built}) after its last deploy (${new Date(state.deployedAt).toLocaleString()}).`
        : `${model} was built (${built}) but not deployed from this machine yet.`;
}
