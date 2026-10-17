const returnTargets = new WeakMap();
const cancelHandlers = new WeakMap();
export function sync(dialog, open) {
  if (!(dialog instanceof HTMLDialogElement) || !dialog.isConnected) return;
  if (!cancelHandlers.has(dialog)) {
    // Native dialog cancel does not bubble. Handle it on the dialog itself,
    // then use the existing Blazor close action so server and DOM state agree.
    const cancel = event => {
      event.preventDefault();
      const close = dialog.querySelector('[data-dialog-close]');
      if (close && !close.matches(':disabled')) close.click();
    };
    dialog.addEventListener('cancel', cancel);
    cancelHandlers.set(dialog, cancel);
  }
  if (open && !dialog.open) {
    returnTargets.set(dialog, document.activeElement);
    dialog.showModal();
    dialog.querySelector('textarea')?.focus();
  } else if (!open && dialog.open) {
    dialog.close();
    const target = returnTargets.get(dialog);
    returnTargets.delete(dialog);
    if (target?.isConnected) target.focus();
  }
}
export function release(dialog) {
  if (!dialog) return;
  const cancel = cancelHandlers.get(dialog);
  if (cancel) dialog.removeEventListener('cancel', cancel);
  cancelHandlers.delete(dialog);
  returnTargets.delete(dialog);
  if (dialog.open) dialog.close();
}
