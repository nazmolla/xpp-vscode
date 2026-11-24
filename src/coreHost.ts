import * as vscode from 'vscode';
import { TokenProvider } from './core';
import { getDataverseToken, getDataverseTokenSilently } from './ude';

/** The core's sign-in: VS Code's Microsoft account, interactive or silent (scopes and client id as before, see ude.ts). */
export const dataverseTokens: TokenProvider = {
    getToken: (resource, { interactive }) => interactive ? getDataverseToken(resource) : getDataverseTokenSilently(resource),
};

/** An AbortSignal that follows a cancellation token; call dispose when the operation ends. */
export function abortSignalOf(token: vscode.CancellationToken | undefined): { signal: AbortSignal | undefined; dispose(): void } {
    if (!token) {
        return { signal: undefined, dispose: () => undefined };
    }
    const controller = new AbortController();
    if (token.isCancellationRequested) {
        controller.abort();
        return { signal: controller.signal, dispose: () => undefined };
    }
    const subscription = token.onCancellationRequested(() => controller.abort());
    return { signal: controller.signal, dispose: () => subscription.dispose() };
}

/** Runs fn with an AbortSignal that follows token. */
export async function withAbortSignal<T>(token: vscode.CancellationToken | undefined, fn: (signal: AbortSignal | undefined) => Promise<T>): Promise<T> {
    const link = abortSignalOf(token);
    try {
        return await fn(link.signal);
    } finally {
        link.dispose();
    }
}
