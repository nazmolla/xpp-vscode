/**
 * What a host (the VS Code extension, a command-line plugin) provides to the cloud operations. The core never signs in,
 * shows UI or reads settings itself.
 */

export interface TokenRequestOptions {
    /** False: return a token only when one is available without user interaction (undefined otherwise). */
    interactive: boolean;
}

/** Access tokens for a resource such as a Dataverse org URL (https://org.crm.dynamics.com); scope `<resource>/.default`. */
export interface TokenProvider {
    getToken(resource: string, options: TokenRequestOptions): Promise<string | undefined>;
}

/** Short status of a running operation (a notification, a spinner). */
export interface Progress {
    report(message: string): void;
}

/** Full output of an operation (an output channel, stderr). */
export interface Log {
    line(text: string): void;
}

/** Common options of long-running operations. */
export interface OperationOptions {
    log?: Log;
    progress?: Progress;
    signal?: AbortSignal;
}

/**
 * Runs listener when signal aborts; when it already has, on the next tick (as a VS Code cancellation token does).
 * Returns the function that removes the listener.
 */
export function onAbort(signal: AbortSignal | undefined, listener: () => void): () => void {
    if (!signal) {
        return () => undefined;
    }
    if (signal.aborted) {
        const handle = setTimeout(listener, 0);
        return () => clearTimeout(handle);
    }
    signal.addEventListener('abort', listener, { once: true });
    return () => signal.removeEventListener('abort', listener);
}
