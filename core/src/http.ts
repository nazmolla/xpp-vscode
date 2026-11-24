const NetworkHints: Record<string, string> = {
    ENOTFOUND: 'the host name does not resolve; check the URL, DNS or proxy',
    ECONNREFUSED: 'the connection was refused; check the proxy and firewall',
    ECONNRESET: 'the connection was reset; check the proxy and firewall',
    ETIMEDOUT: 'the connection timed out; check the proxy and firewall',
    UND_ERR_CONNECT_TIMEOUT: 'the connection timed out; check the proxy and firewall',
    UNABLE_TO_GET_ISSUER_CERT_LOCALLY: 'a proxy inspects TLS with a certificate this machine does not trust',
    SELF_SIGNED_CERT_IN_CHAIN: 'a proxy inspects TLS with a certificate this machine does not trust',
};

/** fetch whose network errors name the host and the cause; Node's own message is only "fetch failed". */
export async function httpFetch(url: string, init?: RequestInit): Promise<Response> {
    try {
        return await fetch(url, init);
    } catch (err) {
        if ((err as Error).name === 'AbortError') {
            throw err;
        }
        const cause = (err as { cause?: { code?: string; message?: string } }).cause;
        const code = cause?.code ?? '';
        // Only the host: download URLs carry a SAS token.
        throw new Error(`Cannot reach ${new URL(url).host}: ${NetworkHints[code] ?? cause?.message ?? (err as Error).message}${code ? ` (${code})` : ''}.`);
    }
}
