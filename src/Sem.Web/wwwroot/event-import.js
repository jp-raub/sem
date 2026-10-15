// Memory-only import transport. Every write is a single explicit network attempt.
const MAX_BYTES = 1048576;
const MAX_RESPONSE_BYTES = 7 * 1024 * 1024;
const navigationWarnings = new Map();

const SPREADSHEET_ID = '[A-Za-z0-9_-]{1,256}';
const SAFE_QUERY_OPTIONS = new Set(['sharing', 'drive_link']);
const CSV_CONTENT_TYPES = new Set(['text/csv', 'text/plain', 'application/csv', 'application/octet-stream']);

function spreadsheetIdUrl(input) {
    if (typeof input !== 'string' || input.length === 0 || input.length > 2048 || input.trim() !== input) throw new Error('Invalid spreadsheet URL');
    const rawPath = /^https:\/\/[^/?#]+([^?#]*)/i.exec(input)?.[1] ?? '';
    if (rawPath.includes('\\') || rawPath.includes('%') || rawPath.split('/').some(segment => segment === '.' || segment === '..'))
        throw new Error('Invalid spreadsheet URL path');
    let source;
    try { source = new URL(input); } catch { throw new Error('Invalid spreadsheet URL'); }
    if (source.protocol !== 'https:' || source.hostname !== 'docs.google.com' || source.port !== ''
        || source.username !== '' || source.password !== '' || source.origin !== 'https://docs.google.com'
        || source.pathname.includes('%') || source.pathname.includes('\\')) throw new Error('Invalid spreadsheet URL');

    const regular = new RegExp(`^/spreadsheets/d/(${SPREADSHEET_ID})(?:/(edit|export))?$`).exec(source.pathname);
    const published = new RegExp(`^/spreadsheets/d/e/(${SPREADSHEET_ID})/(pubhtml|pub)$`).exec(source.pathname);
    if (!regular && !published) throw new Error('Invalid spreadsheet URL');

    let gid = null;
    const seen = new Set();
    for (const [key, value] of source.searchParams) {
        if (seen.has(key)) throw new Error('Invalid spreadsheet URL parameters');
        seen.add(key);
        if (key === 'gid') gid = validGid(value);
        else if (key === 'usp' && SAFE_QUERY_OPTIONS.has(value)) { /* known share-link decoration, discarded */ }
        else if (key === 'format' && !published && value === 'csv') { /* canonical export decoration, discarded */ }
        else if (key === 'output' && published && value === 'csv') { /* canonical publish decoration, discarded */ }
        else if (key === 'single' && published && value === 'true') { /* known publish decoration, discarded */ }
        else throw new Error('Unsupported spreadsheet URL parameter');
    }
    if (source.hash) {
        const fragment = source.hash.slice(1);
        if (!/^gid=[0-9]+$/.test(fragment) || gid !== null) throw new Error('Unsupported spreadsheet URL fragment');
        gid = validGid(fragment.slice(4));
    }

    const id = (regular || published)[1];
    const target = new URL(published
        ? `https://docs.google.com/spreadsheets/d/e/${id}/pub?output=csv`
        : `https://docs.google.com/spreadsheets/d/${id}/export?format=csv`);
    target.searchParams.set('gid', gid ?? '0');
    return target.href;
}

function validGid(value) {
    if (!/^(?:0|[1-9][0-9]{0,9})$/.test(value) || Number(value) > 2147483647) throw new Error('Invalid spreadsheet tab identifier');
    return value;
}

// Only this fixed-origin canonical URL is ever fetched; the submitted URL is never sent.
export function normalizeSpreadsheetCsvUrl(input) {
    return spreadsheetIdUrl(input);
}

export async function fetchSpreadsheetCsv(input, fetchImpl = globalThis.fetch) {
    const url = spreadsheetIdUrl(input);
    if (typeof fetchImpl !== 'function') throw new Error('Spreadsheet fetch unavailable');
    const controller = new AbortController();
    const timer = setTimeout(() => controller.abort(), 15000);
    try {
        const response = await fetchImpl(url, {
            method: 'GET', mode: 'cors', credentials: 'omit', redirect: 'error',
            cache: 'no-store', referrerPolicy: 'no-referrer', signal: controller.signal
        });
        if (!response || response.status !== 200 || response.redirected) throw new Error('Spreadsheet CSV unavailable');
        const contentType = (response.headers.get('Content-Type') || '').split(';', 1)[0].trim().toLowerCase();
        if (!CSV_CONTENT_TYPES.has(contentType)) throw new Error('Spreadsheet response is not CSV');
        const declaredText = response.headers.get('Content-Length');
        if (declaredText !== null) {
            if (!/^[0-9]+$/.test(declaredText.trim()) || Number(declaredText) > MAX_BYTES)
                throw new Error('Spreadsheet CSV exceeds 1 MiB');
        }
        const reader = response.body?.getReader?.();
        if (!reader) throw new Error('Spreadsheet response body is not streamed');
        const chunks = [];
        let size = 0;
        try {
            for (;;) {
                const { done, value } = await reader.read();
                if (done) break;
                size += value.byteLength;
                if (size > MAX_BYTES) {
                    try { await reader.cancel(); } catch { /* stop reading even when cancel fails */ }
                    throw new Error('Spreadsheet CSV exceeds 1 MiB');
                }
                chunks.push(value);
            }
        } finally { try { reader.releaseLock(); } catch { /* already closed */ } }
        const bytes = new Uint8Array(size);
        let offset = 0;
        for (const chunk of chunks) { bytes.set(chunk, offset); offset += chunk.byteLength; }
        const prefix = new TextDecoder('utf-8').decode(bytes.subarray(0, Math.min(bytes.length, 512))).replace(/^\uFEFF/, '').trimStart();
        if (/^(?:<!doctype\s+html\b|<html\b|<head\b|<body\b)/i.test(prefix)) throw new Error('Spreadsheet returned an HTML page');
        return new TextDecoder('utf-8', { fatal: true }).decode(bytes);
    } finally { clearTimeout(timer); }
}

async function readJson(response, maxBytes) {
    const declared = Number(response.headers.get('Content-Length') || 0);
    if (declared > maxBytes) throw new Error('Response too large');
    if (!response.body) {
        const text = await response.text();
        if (new TextEncoder().encode(text).byteLength > maxBytes) throw new Error('Response too large');
        return JSON.parse(text);
    }
    const reader = response.body.getReader();
    const chunks = [];
    let size = 0;
    try {
        for (;;) {
            const { done, value } = await reader.read();
            if (done) break;
            size += value.byteLength;
            if (size > maxBytes) { await reader.cancel(); throw new Error('Response too large'); }
            chunks.push(value);
        }
    } finally { reader.releaseLock(); }
    const bytes = new Uint8Array(size);
    let offset = 0;
    for (const chunk of chunks) { bytes.set(chunk, offset); offset += chunk.byteLength; }
    return JSON.parse(new TextDecoder('utf-8', { fatal: true }).decode(bytes));
}
export async function readCsvInput(input) {
    const file = input?.files?.[0];
    if (!file) return { ok: false, error: 'ファイルを選択してください。' };
    if (file.size > MAX_BYTES) { input.value = ''; return { ok: false, error: 'CSVは1 MiB以下にしてください。' }; }
    try {
        const bytes = await file.arrayBuffer();
        input.value = '';
        if (bytes.byteLength > MAX_BYTES) return { ok: false, error: 'CSVは1 MiB以下にしてください。' };
        return { ok: true, csv: new TextDecoder('utf-8', { fatal: true }).decode(bytes) };
    } catch { input.value = ''; return { ok: false, error: 'UTF-8として読み取れないCSVです。' }; }
}
export async function context(eventId) {
    const controller = new AbortController();
    const timer = setTimeout(() => controller.abort(), 25000);
    try {
        const response = await fetch('/api/core/event-import-context?' + new URLSearchParams({ id: eventId }),
            { cache: 'no-store', redirect: 'error', credentials: 'same-origin', signal: controller.signal });
        if (response.status === 403) return { status: 403, value: null };
        if (response.status !== 200) throw new Error('Current authority unavailable');
        return { status: 200, value: await readJson(response, 128 * 1024) };
    } finally { clearTimeout(timer); }
}
export async function post(action, eventId, actorId, payload) {
    if (!['event-import-preview', 'event-import-apply'].includes(action)) throw new Error('Invalid operation');
    const controller = new AbortController();
    const timer = setTimeout(() => controller.abort(), 25000);
    try {
        const csrf = await fetch('/api/csrf', { cache: 'no-store', redirect: 'error', credentials: 'same-origin', signal: controller.signal });
        if (!csrf.ok) throw new Error('CSRF unavailable');
        const { token } = await readJson(csrf, 64 * 1024);
        if (typeof token !== 'string' || !token) throw new Error('CSRF unavailable');
        const query = new URLSearchParams({ id: eventId, expectedActor: actorId });
        const response = await fetch('/api/core/' + action + '?' + query, {
            method: 'POST', cache: 'no-store', redirect: 'error', credentials: 'same-origin', signal: controller.signal,
            headers: { 'Content-Type': 'application/json', 'X-CSRF-TOKEN': token }, body: JSON.stringify(payload)
        });
        if ([400, 403, 409].includes(response.status)) return { status: response.status, value: null };
        if (response.status !== 200) throw new Error('Uncertain response');
        return { status: 200, value: await readJson(response, MAX_RESPONSE_BYTES) };
    } finally { clearTimeout(timer); }
}
function beforeUnload(event) { if (navigationWarnings.size > 0) { event.preventDefault(); event.returnValue = ''; } }
export function setNavigationWarning(owner, enabled) {
    if (enabled) navigationWarnings.set(owner, true);
    else navigationWarnings.delete(owner);
    window.removeEventListener('beforeunload', beforeUnload);
    if (navigationWarnings.size > 0) window.addEventListener('beforeunload', beforeUnload);
}
