// One explicit, current-cookie read. No mutation, polling, browser storage or log.
export async function history(eventId, actorId, before = null) {
    if (typeof eventId !== 'string' || !eventId || typeof actorId !== 'string' || !actorId)
        throw new Error('Invalid read context');
    const query = new URLSearchParams({ id: eventId, actor: actorId });
    if (before !== null) {
        if (!Number.isSafeInteger(before.acceptedAt) || before.acceptedAt < 0 || before.acceptedAt > 253402300799 ||
            typeof before.requestId !== 'string' || !/^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/.test(before.requestId))
            throw new Error('Invalid history cursor');
        query.set('beforeAt', String(before.acceptedAt)); query.set('beforeId', before.requestId);
    }
    const controller = new AbortController();
    const timer = setTimeout(() => controller.abort(), 25000);
    try {
        const response = await fetch('/api/core/manual-notification-history?' + query, {
            method: 'GET', cache: 'no-store', redirect: 'error', signal: controller.signal
        });
        if ([400, 403].includes(response.status)) return { status: response.status, value: null };
        if (response.status !== 200) throw new Error('History unavailable');
        return { status: 200, value: await response.json() };
    } finally { clearTimeout(timer); }
}
