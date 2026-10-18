// No browser authentication state persistence. Every refresh is an authoritative read.
let owner = null, generation = 0, view = null, logoutBusy = false, authUncertain = false, previewActive = false;
const root = () => document.getElementById('service-navigation');
const el = id => root()?.querySelector('#' + id);
function closeMenu(restore = false) {
  const toggle = el('navigation-toggle'), panel = el('navigation-panel');
  if (!toggle || !panel) return;
  const wasOpen = !panel.hidden;
  panel.hidden = true; toggle.setAttribute('aria-expanded', 'false');
  toggle.setAttribute('aria-label', 'メニューを開く');
  if (restore && wasOpen) toggle.focus();
}
function clearPrivate() {
  closeMenu(); view = null;
  if (el('navigation-controls')) el('navigation-controls').hidden = true;
  if (el('navigation-service')) el('navigation-service').hidden = true;
  if (el('navigation-audit')) el('navigation-audit').hidden = true;
  if (el('navigation-user')) el('navigation-user').textContent = '';
  if (el('navigation-unread')) el('navigation-unread').textContent = '—';
  if (el('navigation-name')) el('navigation-name').textContent = 'SEM';
  if (el('navigation-description')) el('navigation-description').textContent = '';
  if (el('navigation-draft')) el('navigation-draft').hidden = true;
}
function applyPreview() {
  if (!view || !view.isAdmin) { previewActive = false; if (el('navigation-draft')) el('navigation-draft').hidden = true; return; }
  const name = document.getElementById('service-name'), description = document.getElementById('service-description');
  previewActive = !!name;
  el('navigation-name').textContent = name ? name.value : view.serviceName;
  el('navigation-description').textContent = description ? description.value : view.description;
  el('navigation-draft').hidden = !name;
}
async function refresh() {
  const captured = root(), attempt = ++generation;
  if (!captured || logoutBusy || authUncertain) return false;
  try {
    const response = await fetch('/api/core/navigation', {cache:'no-store', credentials:'same-origin', headers:{Accept:'application/json'}});
    const data = response.ok ? await response.json() : null;
    if (captured !== root() || attempt !== generation) return false;
    if (!data || typeof data.userId !== 'string' || typeof data.isAdmin !== 'boolean' || !Number.isSafeInteger(data.unreadCount) || data.unreadCount < 0) {
      clearPrivate(); return false;
    }
    if (view && view.userId !== data.userId) closeMenu();
    view = data;
    el('navigation-name').textContent = data.serviceName;
    el('navigation-description').textContent = data.description;
    el('navigation-controls').hidden = false;
    el('navigation-service').hidden = !data.isAdmin;
    el('navigation-audit').hidden = !data.isAdmin;
    el('navigation-user').textContent = data.displayName;
    el('navigation-unread').textContent = String(data.unreadCount);
    el('navigation-inbox').setAttribute('aria-label', `メッセージボックス、未読${data.unreadCount}件`);
    applyPreview(); return true;
  } catch {
    if (captured === root() && attempt === generation) clearPrivate();
    return false;
  }
}
async function toggleMenu() {
  if (!el('navigation-panel')?.hidden) { closeMenu(true); return; }
  const captured = root(); const ok = await refresh();
  if (!ok || captured !== root() || logoutBusy) return;
  el('navigation-panel').hidden = false;
  el('navigation-toggle').setAttribute('aria-expanded','true');
  el('navigation-toggle').setAttribute('aria-label','メニューを閉じる');
  el('navigation-panel').querySelector('a:not([hidden])')?.focus();
}
async function logout() {
  if (logoutBusy) return;
  logoutBusy = true; closeMenu(); ++generation; clearPrivate();
  try {
    const csrfResponse = await fetch('/api/csrf', {cache:'no-store'});
    if (!csrfResponse.ok) throw new Error();
    const csrf = await csrfResponse.json();
    const response = await fetch('/api/logout',{method:'POST',credentials:'same-origin',headers:{'Content-Type':'application/json','X-CSRF-TOKEN':csrf.token},body:'{}'});
    if (!response.ok) throw new Error();
    // End the old circuit and remove its private DOM. Do not reuse or save cookies/state.
    location.assign('/');
  } catch {
    authUncertain = true;
    if (el('navigation-status')) el('navigation-status').textContent = 'ログアウト結果を確認できません。再読込して認証状態を確認してください。自動再送はしていません。';
  } finally { logoutBusy = false; }
}
function initialize() {
  owner = root(); ++generation; previewActive = false; clearPrivate(); refresh();
}
document.addEventListener('click', event => {
  const target = event.target instanceof Element ? event.target : null;
  if (target?.closest('#navigation-toggle')) { toggleMenu(); return; }
  if (target?.closest('#navigation-logout')) { logout(); return; }
  if (target?.closest('#navigation-panel a')) { closeMenu(); return; }
  if (!target?.closest('#navigation-panel') && !target?.closest('#navigation-toggle')) closeMenu(true);
});
document.addEventListener('keydown', event => {
  if (event.key === 'Escape' && el('navigation-panel') && !el('navigation-panel').hidden) { event.preventDefault(); closeMenu(true); }
});
document.addEventListener('input', event => {
  if (['service-name','service-description'].includes(event.target?.id)) applyPreview();
});
document.addEventListener('sem:notifications-changed', () => refresh());
document.addEventListener('visibilitychange', () => { if (document.hidden) { ++generation; clearPrivate(); } else refresh(); });
window.addEventListener('pageshow', initialize);
// Enhanced navigation replaces the header. Old response ownership cannot update its successor.
window.Blazor?.addEventListener('enhancedload', initialize);
new MutationObserver(() => {
  if (root() !== owner) { initialize(); return; }
  const hasDraft = !!document.getElementById('service-name');
  if (view?.isAdmin && hasDraft !== previewActive) { applyPreview(); if (!hasDraft) refresh(); }
}).observe(document.body, {childList:true, subtree:true});
initialize();
