// The host side of @fno-studio/core in VS Code: cancellation tokens become AbortSignals (sign-in: ude.ts dataverseTokens).
import * as vscode from 'vscode';

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
