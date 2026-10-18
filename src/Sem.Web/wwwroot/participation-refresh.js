// One serialized, visibility-aware read loop per mounted participation component.
const loops = new Map();
let nextId = 0;
export function start(sink) {
    const id = ++nextId;
    const state = { stopped: false, running: false, timer: null };
    const schedule = () => {
        clearTimeout(state.timer);
        if (!state.stopped && document.visibilityState === 'visible')
            state.timer = setTimeout(tick, 7500);
    };
    const tick = async () => {
        state.timer = null;
        if (state.stopped || state.running || document.visibilityState !== 'visible') return;
        state.running = true;
        try { await sink.invokeMethodAsync('RefreshParticipation'); }
        catch { /* Disconnected circuits do not retry writes; the next read may reconnect. */ }
        finally { state.running = false; schedule(); }
    };
    const visibility = () => {
        clearTimeout(state.timer);
        state.timer = null;
        if (document.visibilityState === 'visible' && !state.running) schedule();
    };
    const stopLoop = () => {
        state.stopped = true;
        clearTimeout(state.timer);
        document.removeEventListener('visibilitychange', visibility);
    };
    loops.set(id, stopLoop);
    document.addEventListener('visibilitychange', visibility);
    schedule();
    return id;
}
export function stop(id) {
    loops.get(id)?.();
    loops.delete(id);
}
