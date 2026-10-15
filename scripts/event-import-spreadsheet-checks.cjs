'use strict';

const assert = require('node:assert/strict');
(async () => {
const { fetchSpreadsheetCsv, normalizeSpreadsheetCsvUrl } =
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

console.log(`event-import-spreadsheet-checks: ${checks} assertions passed (mock responses only)`);
})().catch(error => {
    console.error(error);
    process.exitCode = 1;
});