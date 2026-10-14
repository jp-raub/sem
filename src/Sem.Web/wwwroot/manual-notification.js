// Explicit, single attempt only. No storage, logs, background invocation or automatic retry.
export async function post(action, eventId, actorId, payload) {
    if (!['manual-notification-preview', 'manual-notification-accept'].includes(action)) throw new Error('Invalid operation');
    const controller = new AbortController();
    const timer = setTimeout(() => controller.abort(), 25000);
    try {
        const csrf = await fetch('/api/csrf', { cache: 'no-store', redirect: 'error', signal: controller.signal });
        if (!csrf.ok) throw new Error('CSRF unavailable');
        const { token } = await csrf.json();
        if (typeof token !== 'string' || !token) throw new Error('CSRF unavailable');
        const query = new URLSearchParams({ id: eventId, actor: actorId });
        const response = await fetch('/api/core/' + action + '?' + query, {
            method: 'POST', cache: 'no-store', redirect: 'error', signal: controller.signal,
            headers: { 'Content-Type': 'application/json', 'X-CSRF-TOKEN': token }, body: JSON.stringify(payload)
        });
        // Only explicit application rejections are classified; timeout, 5xx, 429 and malformed responses are unknown.
        if ([400, 403, 409].includes(response.status)) return { status: response.status, value: null };
        if (response.status !== 200) throw new Error('Uncertain result');
        return { status: 200, value: await response.json() };
    } finally { clearTimeout(timer); }
}

// Current-cookie authority is re-read even when a long-lived Blazor circuit still has an older principal.
export async function context(eventId) {
    const controller = new AbortController();
    const timer = setTimeout(() => controller.abort(), 25000);
    try {
        const response = await fetch('/api/core/event-tabs?' + new URLSearchParams({ id: eventId }), {
            cache: 'no-store', redirect: 'error', signal: controller.signal
        });
        if (response.status === 403) return { status: 403, value: null };
        if (response.status !== 200) throw new Error('Current authority unavailable');
        return { status: 200, value: await response.json() };
    } finally { clearTimeout(timer); }
}
