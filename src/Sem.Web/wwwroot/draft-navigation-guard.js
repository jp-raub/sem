const guards = new Map();
function changesPage(value) {
  const url = new URL(value, location.href);
  return url.origin === location.origin && (url.pathname !== location.pathname || url.search !== location.search);
}
export function sync(key, protectedDraft, reference) {
  let state = guards.get(key);
  if (!state) {
    state = { protectedDraft, reference };
    const blocked = () => state.reference.invokeMethodAsync('BlockNavigation').catch(() => {});
    state.click = event => {
      if (!state.protectedDraft || event.defaultPrevented || event.button !== 0 || event.ctrlKey || event.metaKey || event.shiftKey || event.altKey) return;
      const link = event.target?.closest?.('a[href]');
      if (!link || link.hasAttribute('download') || (link.target && link.target !== '_self') || !changesPage(link.href)) return;
      event.preventDefault(); event.stopImmediatePropagation(); blocked();
    };
    state.navigate = event => {
      if (!state.protectedDraft || !event.cancelable || event.navigationType === 'reload' || !changesPage(event.destination.url)) return;
      event.preventDefault(); blocked();
    };
    state.unload = event => { if (state.protectedDraft) { event.preventDefault(); event.returnValue = ''; } };
    globalThis.addEventListener('beforeunload', state.unload);
    document.addEventListener('click', state.click, { capture: true });
    globalThis.navigation?.addEventListener('navigate', state.navigate);
    guards.set(key, state);
  }
  state.protectedDraft = protectedDraft; state.reference = reference;
}
export function release(key) {
  const state = guards.get(key);
  if (!state) return;
  globalThis.removeEventListener('beforeunload', state.unload);
  document.removeEventListener('click', state.click, { capture: true });
  globalThis.navigation?.removeEventListener('navigate', state.navigate);
  guards.delete(key);
}
