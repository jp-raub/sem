import { sync as syncDialog, release as releaseDialog } from './comment-dialog.js';
// Reuse native modal/Escape lifecycle; never submit or acknowledge on a timer.
export function sync(dialog, open) {
    if (!(dialog instanceof HTMLDialogElement) || !dialog.isConnected) return;
    const opening = open && !dialog.open;
    const closing = !open && dialog.open;
    syncDialog(dialog, open);
    if (opening) dialog.querySelector('[data-dialog-close]')?.focus();
    if (closing) {
        const active = document.activeElement;
        if (!active?.isConnected || active === document.body || dialog.contains(active) || active.matches?.(':disabled')) {
            const root = dialog.closest('[data-participation-interactive]');
            const refresh = root?.querySelector('#participation-refresh');
            (refresh && !refresh.matches(':disabled') ? refresh : root?.querySelector('#participation-phase'))?.focus();
        }
    }
}
export function release(dialog) { releaseDialog(dialog); }
