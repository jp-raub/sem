const BASIC_PROFILE_LIMIT = 2 * 1024 * 1024;
const HISTORY_LIMIT = 4 * 1024 * 1024;
const REQUEST_TIMEOUT_MS = 25_000;

function validateId(id) {
    if (typeof id !== 'string' || id.length === 0 || id.length > 128 || /[\u0000-\u001f\u007f-\u009f]/u.test(id))
        throw new Error('Invalid profile id');
}

async function readBoundedJson(response, byteLimit) {
    if (!response.body) throw new Error('Profile response is unavailable');
    const reader = response.body.getReader();
    const chunks = [];
    let size = 0;
    try {
        while (true) {
            const { done, value } = await reader.read();
            if (done) break;
            size += value.byteLength;
            if (size > byteLimit) {
                await reader.cancel();
                throw new Error('Profile response exceeds its limit');
            }
            chunks.push(value);
        }
    } catch (error) {
        try { await reader.cancel(); } catch { /* already closed */ }
        throw error;
    } finally {
        reader.releaseLock();
    }

    const bytes = new Uint8Array(size);
    let offset = 0;
    for (const chunk of chunks) { bytes.set(chunk, offset); offset += chunk.byteLength; }
    const text = new TextDecoder('utf-8', { fatal: true }).decode(bytes);
    return JSON.parse(text);
}

async function getJson(path, limit) {
    const controller = new AbortController();
    const timer = setTimeout(() => controller.abort(), REQUEST_TIMEOUT_MS);
    try {
        const response = await fetch(path, {
            method: 'GET',
            credentials: 'same-origin',
            cache: 'no-store',
            redirect: 'error',
            signal: controller.signal
        });
        if (response.status === 403) return { status: 403, value: null };
        if (response.status !== 200) throw new Error('Profile response is unavailable');
        return { status: 200, value: await readBoundedJson(response, limit) };
    } finally {
        clearTimeout(timer);
    }
}

export async function profile(id) {
    validateId(id);
    return getJson('/api/core/profile?id=' + encodeURIComponent(id), BASIC_PROFILE_LIMIT);
}

export async function history(id) {
    validateId(id);
    return getJson('/api/core/profile-registration-history?id=' + encodeURIComponent(id), HISTORY_LIMIT);
}

// Blazor wraps typed byte arrays as IJSStreamReference, avoiding the default SignalR message cap.
export async function profileStream(id) { const bytes=new TextEncoder().encode(JSON.stringify(await profile(id)));if(bytes.byteLength>BASIC_PROFILE_LIMIT)throw new Error('Profile transfer exceeds its limit');return bytes; }
export async function historyStream(id) { const bytes=new TextEncoder().encode(JSON.stringify(await history(id)));if(bytes.byteLength>4*1024*1024)throw new Error('History transfer exceeds its limit');return bytes; }
