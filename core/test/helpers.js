// Test helpers: a fake TokenProvider and a fetch that replays recorded HTTP responses in order and records the requests.
const fs = require('fs');
const path = require('path');

/** Token provider that hands out a fixed token and records what was asked. */
function fakeTokens(token = 'test-token') {
    const calls = [];
    return {
        calls,
        getToken: async (resource, options) => {
            calls.push({ resource, ...options });
            return token;
        },
    };
}

function fixture(name) {
    return JSON.parse(fs.readFileSync(path.join(__dirname, 'fixtures', name), 'utf8'));
}

/**
 * Replaces globalThis.fetch with a replay of recorded exchanges ({ request: { method, url }, response: { status,
 * headers, body } }). Every request must match the next recording (method, and url as a regular expression); a
 * recording with "repeat" serves that many matching requests. Returns the requests seen and restore().
 */
function replay(recordings) {
    const queue = recordings.map(r => ({ ...r, left: r.repeat ?? 1 }));
    const requests = [];
    const original = globalThis.fetch;
    globalThis.fetch = async (url, init = {}) => {
        const method = init.method ?? 'GET';
        if (init.signal?.aborted) {
            const error = new Error('This operation was aborted');
            error.name = 'AbortError';
            throw error;
        }
        const body = init.body instanceof Uint8Array ? Buffer.from(init.body) : init.body;
        requests.push({ method, url, headers: init.headers ?? {}, body });
        const next = queue[0];
        if (!next || next.request.method !== method || !new RegExp(next.request.url).test(url)) {
            throw new Error(`Unexpected request ${method} ${url}; expected ${next ? `${next.request.method} ${next.request.url}` : 'none'}`);
        }
        if (--next.left === 0) {
            queue.shift();
        }
        const { status = 200, headers = {}, body: responseBody } = next.response;
        const text = responseBody === undefined ? null : typeof responseBody === 'string' ? responseBody : JSON.stringify(responseBody);
        return new Response(status === 204 ? null : text, { status, headers });
    };
    return {
        requests,
        remaining: () => queue.length,
        restore: () => { globalThis.fetch = original; },
    };
}

/** A Log and a Progress that keep what they get. */
function recorder() {
    const lines = [];
    const progress = [];
    return { lines, progress, log: { line: t => lines.push(t) }, report: { report: m => progress.push(m) } };
}

module.exports = { fakeTokens, fixture, replay, recorder };
