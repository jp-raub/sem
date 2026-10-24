const states = new WeakMap();
const lifecycles = new Map();
export function sync(dialog, confirmation, returnFocusSelector, lifecycleKey) {
  if (!(dialog instanceof HTMLDialogElement) || !dialog.isConnected) return;
  let state = states.get(dialog);
  if (!state) {
    state = { target: (returnFocusSelector ? document.querySelector(returnFocusSelector) : null) ?? document.activeElement, confirmation: null, previous: null };
    const cancel = event => { event.preventDefault(); dialog.querySelector('[data-draft-cancel]')?.click(); };
    const outside = event => {
      if (event.target !== dialog) return false;
      const r = dialog.getBoundingClientRect();
      return event.clientX < r.left || event.clientX > r.right || event.clientY < r.top || event.clientY > r.bottom;
    };
    const down = event => { state.outside = outside(event); };
    const up = event => { if (state.outside && outside(event)) dialog.querySelector('[data-draft-cancel]')?.click(); state.outside = false; };
    state.cancel = cancel; state.down = down; state.up = up;
    dialog.addEventListener('cancel', cancel);
    dialog.addEventListener('pointerdown', down); dialog.addEventListener('pointerup', up);
    states.set(dialog, state);
    if (lifecycleKey) { state.key = lifecycleKey; lifecycles.set(lifecycleKey, dialog); }
  }
  if (!dialog.open) dialog.showModal();
  if (state.confirmation !== confirmation) {
    if (confirmation) { state.previous = document.activeElement; dialog.querySelector('[data-draft-keep]')?.focus(); }
    else { (state.previous?.isConnected ? state.previous : dialog.querySelector('input,select,textarea,button:not([hidden])'))?.focus(); }
    state.confirmation = confirmation;
  }
}
export function release(dialog) {
  const state = states.get(dialog);
  if (!state) return;
  dialog.removeEventListener('cancel', state.cancel);
  dialog.removeEventListener('pointerdown', state.down); dialog.removeEventListener('pointerup', state.up);
  states.delete(dialog);
  if (state.key) lifecycles.delete(state.key);
  if (dialog.open) dialog.close();
  // Parent renders away the modal; the original trigger may be replaced on save.
  if (state.target?.isConnected) state.target.focus();
}

// Blazor may remove the DOM element before DisposeAsync; retain it only until release.
export function releaseKey(key) {
  const dialog = lifecycles.get(key);
  if (dialog) release(dialog);
}
