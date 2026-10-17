import { notify, release } from './operation-feedback.js';
// The adapter is intentionally synchronous, bounded to its owner and presentation-only.
// A notification failure must never turn a completed write into a failed/retried write.
export function createResultPresenter(owner, isCurrent, present = notify, clear = release) {
    return {
        show(kind, text) { if (!isCurrent() || !owner?.isConnected) return; try { present(owner, kind, text); } catch { /* durable inline fallback */ } },
        clear() { try { clear(owner); } catch { } }
    };
}
