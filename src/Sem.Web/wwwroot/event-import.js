// Memory-only import transport. Every write is a single explicit network attempt.
const MAX_BYTES = 1048576;
const MAX_RESPONSE_BYTES = 7 * 1024 * 1024;
const navigationWarnings = new Map();
const composers = new Map();
const composerRevisions = new Map();
let navigationGuardAttached = false;

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

// IJSStreamReference return types are wrapped by the Blazor runtime itself.
// Return the typed byte array, not an already-created JS stream-reference object.
export async function fetchSpreadsheetCsvStream(input, fetchImpl = globalThis.fetch) {
    return new TextEncoder().encode(await fetchSpreadsheetCsv(input, fetchImpl));
}

// Validate UTF-16 before TextEncoder, whose default behavior replaces lone surrogates.
// Stop as soon as the byte cap is exceeded so a huge paste is not fully encoded.
function inspectPaste(text) {
    if (typeof text !== 'string') return { valid: false, byteLength: 0 };
    let byteLength = 0;
    for (let index = 0; index < text.length; index++) {
        const unit = text.charCodeAt(index);
        if (unit >= 0xd800 && unit <= 0xdbff) {
            if (index + 1 >= text.length) return { valid: false, byteLength };
            const next = text.charCodeAt(index + 1);
            if (next < 0xdc00 || next > 0xdfff) return { valid: false, byteLength };
            index++;
            byteLength += 4;
        } else if (unit >= 0xdc00 && unit <= 0xdfff) {
            return { valid: false, byteLength };
        } else if (unit <= 0x7f) byteLength++;
        else if (unit <= 0x7ff) byteLength += 2;
        else byteLength += 3;
        if (byteLength > MAX_BYTES) return { valid: false, byteLength };
    }
    return { valid: true, byteLength };
}

function notifyPasteChanged(entry) {
    const text = entry.textarea.value;
    const inspected = inspectPaste(text);
    const hasInput = text.length > 0;
    // Protect navigation synchronously in the DOM event, before the .NET callback.
    setOwnerWarning(entry.owner, entry.serverBlocked || hasInput || entry.locked);
    try {
        const pending = entry.dotnet.invokeMethodAsync(
            'OnPasteChanged', entry.generation, entry.revision, hasInput, inspected.valid);
        pending?.catch?.(() => { /* component may have been disposed */ });
    } catch { /* component may have been disposed */ }
}

export function attachComposer(owner, textarea, fileInput, dotnet, generation) {
    const current = composers.get(owner);
    if (current && current.textarea === textarea && current.fileInput === fileInput
        && current.dotnet === dotnet && current.generation === generation)
        return current.revision;
    detachComposer(owner);
    if (!textarea || !fileInput || !dotnet || !Number.isSafeInteger(generation)) return;
    const entry = { owner, textarea, fileInput, dotnet, generation, revision: composerRevisions.get(owner) ?? 0, locked: false, serverBlocked: false };
    entry.onInput = () => {
        if (entry.locked) return;
        entry.revision++;
        composerRevisions.set(owner, entry.revision);
        notifyPasteChanged(entry);
    };
    entry.onFileChange = () => {
        if (entry.locked) return;
        if (fileInput.files?.length) {
            entry.locked = true;
            textarea.readOnly = true;
            fileInput.disabled = true;
            setOwnerWarning(owner, true);
        }
    };
    textarea.addEventListener('input', entry.onInput);
    fileInput.addEventListener('change', entry.onFileChange);
    composers.set(owner, entry);
    return entry.revision;
}

export function detachComposer(owner, forgetRevision = false) {
    const entry = composers.get(owner);
    if (entry) {
        entry.textarea.removeEventListener('input', entry.onInput);
        entry.fileInput.removeEventListener('change', entry.onFileChange);
        composerRevisions.set(owner, entry.revision);
        composers.delete(owner);
    }
    if (forgetRevision) composerRevisions.delete(owner);
}

export function lockComposer(owner, locked) {
    const entry = composers.get(owner);
    if (!entry) return;
    entry.locked = Boolean(locked);
    entry.textarea.readOnly = entry.locked;
    entry.fileInput.disabled = entry.locked;
    setOwnerWarning(owner, entry.serverBlocked || entry.locked || entry.textarea.value.length > 0);
}

// The returned Uint8Array is wrapped by Blazor as IJSStreamReference.
export async function snapshotPasteStream(owner) {
    const entry = composers.get(owner);
    if (!entry) throw new Error('CSV input is not ready');
    entry.locked = true;
    entry.textarea.readOnly = true;
    entry.fileInput.disabled = true;
    setOwnerWarning(owner, true);
    const text = entry.textarea.value;
    const inspected = inspectPaste(text);
    try {
        await entry.dotnet.invokeMethodAsync(
            'OnPasteChanged', entry.generation, entry.revision, text.length > 0, inspected.valid);
    } catch { /* snapshot validation below is still authoritative */ }
    if (!text.length) throw new Error('CSV is empty');
    if (!inspected.valid) throw new Error('CSV is invalid or exceeds 1 MiB');
    const bytes = new TextEncoder().encode(text);
    if (bytes.byteLength !== inspected.byteLength || bytes.byteLength > MAX_BYTES)
        throw new Error('CSV exceeds 1 MiB');
    return bytes;
}

// Used only to mirror bytes already validated by the component; never dispatch input.
export function syncTextarea(owner, text) {
    const entry = composers.get(owner);
    if (!entry || typeof text !== 'string') return -1;
    entry.textarea.value = text;
    entry.revision++;
    composerRevisions.set(owner, entry.revision);
    setOwnerWarning(owner, entry.serverBlocked || entry.locked || text.length > 0);
    return entry.revision;
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
export async function readCsvInputStream(input) {
    const file = input?.files?.[0];
    if (!file) throw new Error('ファイルを選択してください。');
    if (file.size > MAX_BYTES) { input.value = ''; throw new Error('CSVは1 MiB以下にしてください。'); }
    try {
        const bytes = await file.arrayBuffer();
        input.value = '';
        if (bytes.byteLength > MAX_BYTES) throw new Error('CSVは1 MiB以下にしてください。');
        // Validate early for a clear failure; .NET decodes the streamed bytes strictly too.
        new TextDecoder('utf-8', { fatal: true }).decode(bytes);
        return new Uint8Array(bytes);
    } catch (error) { input.value = ''; throw error; }
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
function guardBusySameOriginAnchor(event) {
    if (navigationWarnings.size === 0 || event.defaultPrevented || event.button !== 0
        || event.altKey || event.ctrlKey || event.metaKey || event.shiftKey) return;
    const anchor = event.target?.closest?.('a[href]');
    if (!anchor) return;
    const target = (anchor.getAttribute('target') || '').trim().toLowerCase();
    if (target && target !== '_self') return;
    let destination;
    try { destination = new URL(anchor.href, window.location.href); } catch { return; }
    if (destination.origin !== window.location.origin) return;
    // This authenticated attachment does not replace the current page.
    if (anchor.id === 'event-import-sample' && anchor.hasAttribute?.('download')
        && destination.pathname === '/api/core/event-import-sample') return;
    event.preventDefault();
    event.stopImmediatePropagation();
}
function syncNavigationGuards() {
    const enabled = navigationWarnings.size > 0;
    if (enabled && !navigationGuardAttached) {
        document.addEventListener('click', guardBusySameOriginAnchor, true);
        navigationGuardAttached = true;
    } else if (!enabled && navigationGuardAttached) {
        document.removeEventListener('click', guardBusySameOriginAnchor, true);
        navigationGuardAttached = false;
    }
    window.removeEventListener('beforeunload', beforeUnload);
    if (enabled) window.addEventListener('beforeunload', beforeUnload);
}
export function setNavigationWarning(owner, enabled) {
    const entry = composers.get(owner);
    if (entry) entry.serverBlocked = Boolean(enabled);
    setOwnerWarning(owner, enabled);
}

function setOwnerWarning(owner, enabled) {
    if (enabled) navigationWarnings.set(owner, true);
    else navigationWarnings.delete(owner);
    syncNavigationGuards();
}
