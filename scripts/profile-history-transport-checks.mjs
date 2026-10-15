import fs from 'node:fs/promises';
const source = await fs.readFile(new URL('../src/Sem.Web/wwwroot/profile-history.js', import.meta.url), 'utf8');
const { profile, history } = await import('data:text/javascript;base64,' + Buffer.from(source).toString('base64'));
let passed = 0;
const check = (value, label) => { if (!value) throw new Error('FAIL ' + label); passed++; console.log('PASS ' + label); };
const reject = async (fn, label) => { let rejected = false; try { await fn(); } catch { rejected = true; } check(rejected, label); };
const originalFetch = globalThis.fetch;
const originalSetTimeout = globalThis.setTimeout;
const originalClearTimeout = globalThis.clearTimeout;
try {
    const calls = [];
    globalThis.fetch = async (url, options) => { calls.push({ url, options }); return new Response('{"userId":"target","events":[]}'); };
    const result = await history('target / member');
    const call = calls[0];
    check(result.status === 200 && result.value.userId === 'target', 'history returns one decoded snapshot');
    const url = new URL(call.url, 'https://example.test');
    check(url.pathname === '/api/core/profile-registration-history' && url.searchParams.get('id') === 'target / member', 'history encodes only the target id');
    check(call.options.method === 'GET' && call.options.credentials === 'same-origin' && call.options.cache === 'no-store' && call.options.redirect === 'error' && call.options.signal instanceof AbortSignal, 'history uses current same-origin cookie, no-store, redirect rejection, and abort signal');
    check(!('body' in call.options) && !('headers' in call.options) && calls.length === 1, 'history makes one bodyless request without actor override or retry');
    calls.length = 0;
    const basic = await profile('target');
    check(basic.status === 200 && new URL(calls[0].url, 'https://example.test').pathname === '/api/core/profile', 'basic profile also uses same-cookie transport');

    for (const status of [403]) {
        globalThis.fetch = async () => new Response('ignored', { status });
        const denied = await history('target');
        check(denied.status === status && denied.value === null, '403 returns no profile/history data for explicit UI clearing');
    }
    for (const status of [302, 400, 404, 500]) {
        let count = 0;
        globalThis.fetch = async () => { count++; return new Response('{}', { status }); };
        await reject(() => history('target'), 'non-success status ' + status + ' fails closed');
        check(count === 1, 'non-success status ' + status + ' is not retried');
    }
    globalThis.fetch = async () => new Response('{"userId":"target"');
    await reject(() => history('target'), 'malformed JSON is rejected');
    globalThis.fetch = async () => new Response(new ReadableStream({ start(controller) { controller.enqueue(new Uint8Array([0xff, 0xfe])); controller.close(); } }));
    await reject(() => history('target'), 'invalid UTF-8 is rejected strictly');

    let cancelled = false;
    globalThis.fetch = async () => new Response(new ReadableStream({
        start(controller) { controller.enqueue(new Uint8Array(4 * 1024 * 1024 + 1)); },
        cancel() { cancelled = true; }
    }));
    await reject(() => history('target'), 'history response above 4 MiB is refused');
    check(cancelled, 'oversize history stream is cancelled immediately');

    globalThis.fetch = async () => new Response(new ReadableStream({ start(controller) { controller.enqueue(new Uint8Array(128 * 1024 + 1)); } }));
    await reject(() => profile('target'), 'basic profile response above 128 KiB is refused');

    let aborted = false;
    globalThis.setTimeout = callback => { queueMicrotask(callback); return 123; };
    globalThis.clearTimeout = () => {};
    globalThis.fetch = (_url, options) => new Promise((_, rejectPromise) => {
        options.signal.addEventListener('abort', () => { aborted = true; rejectPromise(new DOMException('Aborted', 'AbortError')); }, { once: true });
    });
    await reject(() => history('target'), 'timeout abort rejects the pending read');
    check(aborted, '25-second timeout signal can abort the fetch and stream lifecycle');

    let count = 0;
    globalThis.fetch = async () => { count++; return new Response('{}'); };
    for (const id of ['', 'x'.repeat(129), 'target\nmember', null]) await reject(() => profile(id), 'invalid client target is refused');
    check(count === 0, 'invalid identifiers make no requests');
    check(source.includes('REQUEST_TIMEOUT_MS = 25_000') && source.includes("new TextDecoder('utf-8', { fatal: true })") && !/localStorage|sessionStorage|console\.|setInterval/.test(source), 'transport bounds timeout, requires strict UTF-8, and does not log/store/poll');
    console.log(`Profile history transport checks passed: ${passed}`);
} finally {
    globalThis.fetch = originalFetch;
    globalThis.setTimeout = originalSetTimeout;
    globalThis.clearTimeout = originalClearTimeout;
}
