const returnTargets = new WeakMap();
export function sync(dialog, open) {
  if (!(dialog instanceof HTMLDialogElement) || !dialog.isConnected) return;
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
  returnTargets.delete(dialog);
  if (dialog.open) dialog.close();
}
