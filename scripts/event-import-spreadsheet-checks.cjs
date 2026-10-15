'use strict';

const assert = require('node:assert/strict');
(async () => {
const { fetchSpreadsheetCsv, fetchSpreadsheetCsvStream, normalizeSpreadsheetCsvUrl, setNavigationWarning } =
    await import('../src/Sem.Web/wwwroot/event-import.js');

let checks = 0;
function check(condition, message) {
    assert.ok(condition, message);
    checks++;
}
function rejectsUrl(value, message) {
    assert.throws(() => normalizeSpreadsheetCsvUrl(value), undefined, message);
    checks++;
}
const canonical = (id = 'abc_123-X', gid = '0') =>
    `https://docs.google.com/spreadsheets/d/${id}/export?format=csv&gid=${gid}`;
const bytes = value => new TextEncoder().encode(value);
function headers(contentType = 'text/csv', contentLength = null) {
    return {
        get(name) {
            if (name.toLowerCase() === 'content-type') return contentType;
            if (name.toLowerCase() === 'content-length') return contentLength;
            return null;
        }
    };
}
function response(data, { contentType = 'text/csv; charset=utf-8', contentLength = null, status = 200, redirected = false } = {}) {
    const body = data instanceof Uint8Array ? data : bytes(data);
    return {
        status, redirected, headers: headers(contentType, contentLength),
        body: new ReadableStream({ start(controller) { controller.enqueue(body); controller.close(); } })
    };
}

check(normalizeSpreadsheetCsvUrl('https://docs.google.com/spreadsheets/d/abc_123-X/edit') === canonical(), 'regular edit URL defaults gid to zero');
check(normalizeSpreadsheetCsvUrl('https://docs.google.com/spreadsheets/d/abc_123-X/edit?usp=sharing&gid=12') === canonical('abc_123-X', '12'), 'known sharing decoration is discarded and query gid retained');
check(normalizeSpreadsheetCsvUrl('https://docs.google.com/spreadsheets/d/abc_123-X/export?usp=drive_link#gid=7') === canonical('abc_123-X', '7'), 'drive_link decoration and hash gid normalize');
check(normalizeSpreadsheetCsvUrl('https://docs.google.com/spreadsheets/d/e/pub-id/pubhtml?gid=2147483647') === 'https://docs.google.com/spreadsheets/d/e/pub-id/pub?output=csv&gid=2147483647', 'published sheet normalizes to anonymous CSV route');
check(normalizeSpreadsheetCsvUrl('https://docs.google.com:443/spreadsheets/d/id/edit') === canonical('id'), 'default HTTPS port is accepted and removed');
check(normalizeSpreadsheetCsvUrl(canonical('id', '9')) === canonical('id', '9'), 'canonical regular export URL is accepted and rebuilt');
const publishedCanonical = 'https://docs.google.com/spreadsheets/d/e/pub-id/pub?output=csv&gid=8';
check(normalizeSpreadsheetCsvUrl('https://docs.google.com/spreadsheets/d/e/pub-id/pubhtml?single=true&output=csv&gid=8') === publishedCanonical, 'known published CSV parameters are accepted and rebuilt');
check(normalizeSpreadsheetCsvUrl(normalizeSpreadsheetCsvUrl('https://docs.google.com/spreadsheets/d/id/edit?usp=sharing&gid=9')) === canonical('id', '9'), 'regular URL normalization is idempotent');
check(normalizeSpreadsheetCsvUrl(normalizeSpreadsheetCsvUrl('https://docs.google.com/spreadsheets/d/e/pub-id/pubhtml?single=true&gid=8')) === publishedCanonical, 'published URL normalization is idempotent');

for (const [url, reason] of [
    ['http://docs.google.com/spreadsheets/d/id/edit', 'HTTP rejected'],
    ['ftp://docs.google.com/spreadsheets/d/id/edit', 'non-HTTPS scheme rejected'],
    ['https://docs.google.com.evil.test/spreadsheets/d/id/edit', 'host suffix rejected'],
    ['https://evil.test/spreadsheets/d/id/edit', 'wrong host rejected'],
    ['https://user@docs.google.com/spreadsheets/d/id/edit', 'username rejected'],
    ['https://user:pass@docs.google.com/spreadsheets/d/id/edit', 'credentials rejected'],
    ['https://docs.google.com:444/spreadsheets/d/id/edit', 'non-default port rejected'],
    ['https://docs.google.com/spreadsheets/d/id/../other', 'traversal rejected'],
    ['https://docs.google.com/spreadsheets/d/%2e%2e/edit', 'encoded path rejected'],
    ['https://docs.google.com/spreadsheets/d/id%2Fother/edit', 'encoded separator rejected'],
    ['https://docs.google.com/spreadsheets/d/id/edit?gid=1&gid=1', 'duplicate gid rejected'],
    ['https://docs.google.com/spreadsheets/d/id/edit?gid=1#gid=1', 'query and fragment duplicate gid rejected'],
    ['https://docs.google.com/spreadsheets/d/id/edit?key=secret', 'auth/arbitrary parameter rejected'],
    ['https://docs.google.com/spreadsheets/d/id/edit?usp=sharing&usp=drive_link', 'duplicate usp rejected'],
    ['https://docs.google.com/spreadsheets/d/id/edit?usp=other', 'unknown usp rejected'],
    ['https://docs.google.com/spreadsheets/d/id/edit#gid=1&range=A1', 'unsupported fragment rejected'],
    ['https://docs.google.com/spreadsheets/d/id/edit?gid=-1', 'negative gid rejected'],
    ['https://docs.google.com/spreadsheets/d/id/edit?gid=2147483648', 'out-of-range gid rejected'],
    ['https://docs.google.com/spreadsheets/d/id/edit?gid=1.5', 'non-decimal gid rejected'],
    ['https://docs.google.com/spreadsheets/d/id/edit?gid=01', 'non-canonical gid rejected'],
    ['https://docs.google.com/spreadsheets/d/id/copy', 'unsupported route rejected'],
    ['https://docs.google.com/spreadsheets/d/e/id/pubhtml?key=x', 'published auth parameter rejected'],
    ['https://docs.google.com/spreadsheets/d/id/edit ', 'whitespace-normalized URL rejected']
]) rejectsUrl(url, reason);

assert.throws(() => normalizeSpreadsheetCsvUrl('x'), /Invalid spreadsheet URL/);
checks++;

{
    const csv = 'Kind,Title,Duration\\nSong,Example,00:03:00\\n';
    let calls = 0;
    const result = await fetchSpreadsheetCsv(
        'https://docs.google.com/spreadsheets/d/id/edit?gid=2&usp=sharing',
        async (url, options) => {
            calls++;
            check(url === canonical('id', '2'), 'fetch receives only the canonical fixed-host URL');
            check(options.method === 'GET' && options.mode === 'cors', 'fetch is explicit browser CORS GET');
            check(options.credentials === 'omit' && options.referrerPolicy === 'no-referrer', 'fetch omits credentials and referrer');
            check(options.redirect === 'error' && options.cache === 'no-store', 'fetch rejects redirects and disables cache');
            check(options.signal instanceof AbortSignal, 'fetch is abort-bounded');
            return response(csv);
        });
    check(calls === 1 && result === csv, 'mock CSV response is returned without preview/apply');
}

{
    const csv = 'Kind,Title,Duration\\nSong,Streamed,00:03:00\\n';
    let captured = null;
    const streamRef = await fetchSpreadsheetCsvStream(
        'https://docs.google.com/spreadsheets/d/id/edit',
        async () => response(csv));
    captured = streamRef;
    check(streamRef.byteLength === bytes(csv).byteLength && captured instanceof Uint8Array, 'small CSV returns typed bytes for runtime stream wrapping');
    check(new TextDecoder().decode(captured) === csv, 'stream reference contains exact UTF-8 CSV bytes');
}

{
    const oneMiB = 'x'.repeat(1048576);
    let captured = null;
    const streamRef = await fetchSpreadsheetCsvStream(
        'https://docs.google.com/spreadsheets/d/id/edit',
        async () => response(oneMiB));
    captured = streamRef;
    check(streamRef.byteLength === 1048576 && captured.byteLength === 1048576, '1 MiB CSV fits streamed JS interop envelope');
    check(captured[0] === 120 && captured[captured.length - 1] === 120, '1 MiB stream retains boundary bytes');
}

{
    let reads = 0;
    const tooLarge = {
        status: 200, redirected: false, headers: headers('text/csv', '1048577'),
        body: { getReader() { return { async read() { reads++; return { done: true }; }, async cancel() {}, releaseLock() {} }; } }
    };
    await assert.rejects(fetchSpreadsheetCsv('https://docs.google.com/spreadsheets/d/id/edit', async () => tooLarge), /1 MiB/);
    check(reads === 0, 'oversize Content-Length rejected before body read');
}

{
    let arrayBufferCalls = 0;
    const noStream = {
        status: 200, redirected: false, headers: headers(),
        body: null,
        async arrayBuffer() { arrayBufferCalls++; return bytes('unbounded fallback'); }
    };
    await assert.rejects(fetchSpreadsheetCsv('https://docs.google.com/spreadsheets/d/id/edit', async () => noStream), /not streamed/);
    check(arrayBufferCalls === 0, 'unstreamed body is rejected without arrayBuffer fallback');
}

{
    let cancelled = false;
    const overflowing = {
        status: 200, redirected: false, headers: headers(),
        body: new ReadableStream({
            pull(controller) { controller.enqueue(new Uint8Array(1048577)); },
            cancel() { cancelled = true; }
        })
    };
    await assert.rejects(fetchSpreadsheetCsv('https://docs.google.com/spreadsheets/d/id/edit', async () => overflowing), /1 MiB/);
    check(cancelled, 'oversize streamed body is cancelled');
}

for (const [item, message] of [
    [response('<html><title>Sign in</title></html>', { contentType: 'text/html' }), /not CSV/],
    [response('<!doctype html><html>login</html>'), /HTML page/],
    [response(new Uint8Array([0xc3, 0x28])), /encoded data/],
    [response('redirected', { status: 302, redirected: true }), /unavailable/],
    [response('bad status', { status: 403 }), /unavailable/],
    [response('anything', { contentType: 'application/json' }), /not CSV/]
]) {
    await assert.rejects(fetchSpreadsheetCsv('https://docs.google.com/spreadsheets/d/id/edit', async () => item), message);
    checks++;
}

{
    const old = global.setTimeout;
    global.setTimeout = (callback, delay, ...args) => {
        check(delay === 15000, 'fetch deadline is 15 seconds');
        return old(callback, 0, ...args);
    };
    try {
        await assert.rejects(fetchSpreadsheetCsv('https://docs.google.com/spreadsheets/d/id/edit', (_url, options) =>
            new Promise((_, reject) => options.signal.addEventListener('abort', () => reject(new Error('aborted')), { once: true }))), /aborted/);
        checks++;
    } finally { global.setTimeout = old; }
}

{
    const originalWindow = global.window;
    const originalDocument = global.document;
    const docListeners = new Map();
    const windowListeners = new Map();
    const add = store => (type, listener, capture = false) => store.set(`${type}:${capture}`, listener);
    const remove = store => (type, listener, capture = false) => {
        const key = `${type}:${capture}`;
        if (store.get(key) === listener) store.delete(key);
    };
    global.window = {
        location: { href: 'https://app.example/events/1/event-import', origin: 'https://app.example' },
        addEventListener: add(windowListeners), removeEventListener: remove(windowListeners)
    };
    global.document = { addEventListener: add(docListeners), removeEventListener: remove(docListeners) };
    const click = ({ href = 'https://app.example/workspace', target = '', id = '', download = null, button = 0, ctrlKey = false, shiftKey = false } = {}) => {
        let prevented = false, stopped = false;
        const anchor = { href, id, hasAttribute(name) { return name === 'download' && download !== null; }, getAttribute(name) { return name === 'target' ? target : name === 'download' ? download : null; } };
        const event = {
            button, ctrlKey, shiftKey, altKey: false, metaKey: false, defaultPrevented: false,
            target: { closest(selector) { return selector === 'a[href]' ? anchor : null; } },
            preventDefault() { prevented = true; }, stopImmediatePropagation() { stopped = true; }
        };
        docListeners.get('click:true')(event);
        return { prevented, stopped };
    };
    try {
        setNavigationWarning('owner-one', true);
        check(docListeners.has('click:true'), 'busy-navigation capture guard is attached');
        let result = click();
        check(result.prevented && result.stopped, 'unmodified same-origin anchor is blocked while an owner is busy');
        result = click({ href: 'https://app.example/api/core/event-import-sample?id=1', id: 'event-import-sample', download: 'event-import-sample.csv' });
        check(!result.prevented && !result.stopped, 'known authenticated sample attachment remains downloadable');
        result = click({ href: 'https://app.example/workspace', id: 'event-import-sample', download: 'event-import-sample.csv' });
        check(result.prevented && result.stopped, 'attachment exception cannot bypass navigation for another route');
        result = click({ href: 'https://other.example/path' });
        check(!result.prevented && !result.stopped, 'cross-origin anchors are not intercepted');
        result = click({ ctrlKey: true });
        check(!result.prevented && !result.stopped, 'modified new-tab click is not intercepted');
        result = click({ target: '_blank' });
        check(!result.prevented && !result.stopped, 'target blank is not intercepted');
        result = click({ button: 1 });
        check(!result.prevented && !result.stopped, 'non-primary click is not intercepted');
        setNavigationWarning('owner-two', true);
        setNavigationWarning('owner-one', false);
        check(docListeners.has('click:true') && click().prevented, 'guard remains active while another owner is busy');
        setNavigationWarning('owner-two', false);
        check(!docListeners.has('click:true') && !windowListeners.has('beforeunload:false'), 'last owner removal unregisters capture and unload guards');
    } finally {
        setNavigationWarning('owner-one', false);
        setNavigationWarning('owner-two', false);
        if (originalWindow === undefined) delete global.window; else global.window = originalWindow;
        if (originalDocument === undefined) delete global.document; else global.document = originalDocument;
    }
}

console.log(`event-import-spreadsheet-checks: ${checks} assertions passed (mock responses only)`);
})().catch(error => {
    console.error(error);
    process.exitCode = 1;
});