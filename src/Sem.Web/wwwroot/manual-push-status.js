// Explicit current-cookie read only. No polling, storage, log, or operation replay.
export async function status(eventId, actorId, requestId) {
    if (typeof eventId !== 'string' || !eventId || typeof actorId !== 'string' || !actorId ||
        typeof requestId !== 'string' || !/^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/.test(requestId))
        throw new Error('Invalid read context');
    const controller = new AbortController();
    const timer = setTimeout(() => controller.abort(), 25000);
    try {
        const response = await fetch('/api/core/manual-notification-push-status?' + new URLSearchParams({ id: eventId, actor: actorId, requestId }), {
            method: 'GET', cache: 'no-store', redirect: 'error', signal: controller.signal
        });
        if ([400, 403].includes(response.status)) return { status: response.status, value: null };
        if (response.status !== 200) throw new Error('Status unavailable');
        return { status: 200, value: await response.json() };
    } finally { clearTimeout(timer); }
}
