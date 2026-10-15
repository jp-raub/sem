// Memory-only import transport. Every write is a single explicit network attempt.
const MAX_BYTES = 1048576;
const MAX_RESPONSE_BYTES = 7 * 1024 * 1024;
const navigationWarnings = new Map();
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
