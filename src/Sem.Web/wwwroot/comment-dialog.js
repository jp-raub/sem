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
    // native dialogだけではTab周回中に文書のBODYへフォーカスが抜ける場合がある。
    // 端で明示的に折り返し、説明・投稿・取消をキーボードで続けて操作できるようにする。
    const keydown = event => {
      if (!dialog.open || event.key !== 'Tab') return;
      const controls = [...dialog.querySelectorAll('button,input,select,textarea,a[href],[tabindex]')]
        .filter(el => el.tabIndex >= 0 && !el.matches(':disabled,[hidden],[inert]') &&
          el.getClientRects().length > 0 && getComputedStyle(el).visibility !== 'hidden');
      const first = controls[0], last = controls.at(-1), active = document.activeElement;
      if (!first) { event.preventDefault(); dialog.focus(); return; }
      if (event.shiftKey && (active === first || !controls.includes(active))) {
        event.preventDefault(); last.focus();
      } else if (!event.shiftKey && (active === last || !controls.includes(active))) {
        event.preventDefault(); first.focus();
      }
    };
    dialog.addEventListener('cancel', cancel);
    dialog.addEventListener('keydown', keydown);
    cancelHandlers.set(dialog, { cancel, keydown });
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
  const handlers = cancelHandlers.get(dialog);
  if (handlers) {
    dialog.removeEventListener('cancel', handlers.cancel);
    dialog.removeEventListener('keydown', handlers.keydown);
  }
  cancelHandlers.delete(dialog);
  returnTargets.delete(dialog);
  if (dialog.open) dialog.close();
}
