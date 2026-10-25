// Presentation only. All authority, snapshots, confirmations and transport stay in invitation-management.
export function createInvitationDialogs(root, dismiss) {
  const document = root.ownerDocument;
  const keys = ['issue', 'request', 'review', 'revoke'];
  const entries = keys.map(key => ({ key, dialog: root.querySelector(`#invitation-${key}-dialog`),
    open: root.querySelector(`#invitation-${key}-open`),
    close: root.querySelector(key === 'review' ? '#invitation-request-review-cancel' : `#invitation-${key}-close`) }));
  // Transport unit fixtures do not provide a browser dialog. Actual application markup is strict.
  if (!entries.some(e => e.dialog)) return { sync() {}, capture() {}, completed() {}, closeAll() {}, dispose() {} };
  if (entries.some(e => !e.dialog || !e.close)) throw new TypeError('invitation dialog controls missing');
  let current = null, disposed = false, busy = false, returnTarget = null, pendingTarget = null;
  const listeners = [];
  const on = (node, name, fn) => { node.addEventListener(name, fn); listeners.push(() => node.removeEventListener(name, fn)); };
  const restore = () => {
    if (disposed || !root.isConnected) return;
    // Rows are reconstructed after a response. Resolve the accessible label, never a server-provided selector.
    let target = returnTarget?.node;
    if (!target?.isConnected && returnTarget?.label) target = [...root.querySelectorAll('button[aria-label]')].find(n => n.getAttribute('aria-label') === returnTarget.label);
    if (!target?.isConnected || target.disabled) target = root.querySelector('#invitation-management-refresh');
    if (target && !target.disabled && !target.closest('[hidden]')) target.focus();
  };
  function close(restoreFocus = true) {
    if (!current) return;
    const entry = current; current = null;
    if (entry.dialog.open) entry.dialog.close();
    if (restoreFocus) restore();
  }
  function show(entry) {
    if (disposed || current === entry || !root.isConnected) return;
    close(false);
    const active = pendingTarget ?? document.activeElement;
    pendingTarget = null;
    returnTarget = { node: active, label: active?.getAttribute?.('aria-label') };
    current = entry;
    entry.dialog.showModal();
    // Initial focus belongs to the content, never to a destructive confirmation.
    const first = entry.dialog.querySelector('textarea:not(:disabled), select:not(:disabled), input:not(:disabled), button:not(:disabled)');
    first?.focus();
  }
  function cancel(entry) {
    if (disposed || busy || current !== entry) return;
    if (dismiss(entry.key) !== false) close();
  }
  for (const entry of entries) {
    if (entry.open) on(entry.open, 'click', () => { if (!entry.open.disabled) show(entry); });
    // Review cancel is also handled by the authoritative root listener; dismiss is idempotent.
    on(entry.close, 'click', () => cancel(entry));
    on(entry.dialog, 'cancel', event => { event.preventDefault(); cancel(entry); });
    let outsideDown = false;
    const outside = event => {
      if (event.target !== entry.dialog) return false;
      const r = entry.dialog.getBoundingClientRect();
      return event.clientX < r.left || event.clientX > r.right || event.clientY < r.top || event.clientY > r.bottom;
    };
    on(entry.dialog, 'pointerdown', event => { outsideDown = outside(event); });
    on(entry.dialog, 'pointerup', event => { if (outsideDown && outside(event)) cancel(entry); outsideDown = false; });
  }
  return {
    capture() { pendingTarget = document.activeElement; },
    completed(kind) { if (current?.key === kind) close(); },
    sync(state) {
      if (disposed) return;
      busy = !!state.busy;
      root.setAttribute('data-invitation-admin', String(!!state.admin));
      for (const entry of entries) {
        if (entry.open) entry.open.disabled = state.locked || !state.actor || (entry.key === 'issue' ? !state.admin : state.admin);
        entry.close.disabled = busy;
      }
      if (!state.actor || (!busy && state.locked)) { close(); return; }
      if (state.review && state.admin) show(entries.find(e => e.key === 'review'));
      else if (state.revoke && state.admin) show(entries.find(e => e.key === 'revoke'));
      else if (current && ((current.key === 'review' && !state.review) || (current.key === 'revoke' && !state.revoke)
        || (current.key === 'issue' && !state.admin) || (current.key === 'request' && state.admin))) close();
    },
    closeAll() { close(false); returnTarget = null; },
    dispose() { disposed = true; close(false); returnTarget = null; for (const remove of listeners) remove(); }
  };
}
