const API = '/api/core/admin-accounts';
const MAX_RESPONSE_BYTES = 5 * 1024 * 1024;
const MAX_REQUEST_BYTES = 65_536;
let dialogReturnFocus = null;

async function readJson(response) {
  const reader = response.body?.getReader();
  let bytes;
  if (reader) {
    const chunks = [];
    let length = 0;
    try {
      while (true) {
        const { done, value } = await reader.read();
        if (done) break;
        length += value.byteLength;
        if (length > MAX_RESPONSE_BYTES) {
          await reader.cancel();
          throw new RangeError('応答が大きすぎます。');
        }
        chunks.push(value);
      }
    } finally {
      try { reader.releaseLock(); } catch { }
    }
    bytes = new Uint8Array(length);
    let offset = 0;
    for (const chunk of chunks) { bytes.set(chunk, offset); offset += chunk.byteLength; }
  } else {
    bytes = new Uint8Array(await response.arrayBuffer());
    if (bytes.byteLength > MAX_RESPONSE_BYTES) throw new RangeError('応答が大きすぎます。');
  }
  return JSON.parse(new TextDecoder('utf-8', { fatal: true }).decode(bytes));
}

async function send(url, options) {
  const response = await fetch(url, {
    credentials: 'same-origin',
    mode: 'same-origin',
    cache: 'no-store',
    redirect: 'error',
    referrerPolicy: 'same-origin',
    ...options,
    headers: { Accept: 'application/json', ...(options.headers || {}) }
  });
  const value = await readJson(response);
  if (!response.ok) throw new Error(value?.error || 'アカウント権限を保存できませんでした。');
  return value;
}

export async function load() {
  const value = await send(API, { method: 'GET' });
  if (!value || typeof value.actorId !== 'string' || !Array.isArray(value.accounts))
    throw new Error('アカウント一覧の応答を確認できません。');
  return value;
}

export async function save(actorId, changes) {
  if (typeof actorId !== 'string' || actorId.length < 1 || actorId.length > 128 ||
      !Array.isArray(changes)) throw new TypeError('管理者セッションを確認してください。');
  const csrf = await send('/api/csrf', { method: 'GET' });
  if (!csrf || typeof csrf.token !== 'string' || !csrf.token)
    throw new Error('CSRFトークンを取得できません。ページを再読み込みしてください。');
  const url = new URL(API, globalThis.location.origin);
  url.searchParams.set('expectedActor', actorId);
  const body = JSON.stringify({ changes });
  if (new TextEncoder().encode(body).byteLength > MAX_REQUEST_BYTES)
    throw new RangeError('変更内容が大きすぎます。');
  const result = await send(url.href, {
    method: 'POST',
    headers: { 'Content-Type': 'application/json', 'X-CSRF-TOKEN': csrf.token },
    body
  });
  return result;
}

export function focusDialog() {
  const dialog = document.querySelector('.admin-account-dialog');
  if (!dialog) return;
  if (!dialogReturnFocus) dialogReturnFocus = document.activeElement;
  dialog.focus();
  dialog.addEventListener('keydown', trapDialogTab);
}

function trapDialogTab(event) {
  if (event.key !== 'Tab') return;
  const dialog = event.currentTarget;
  const items = [...dialog.querySelectorAll('a[href],button:not([disabled]),input:not([disabled]),select:not([disabled]),textarea:not([disabled]),[tabindex]:not([tabindex="-1"])')]
    .filter(item => item.getClientRects().length > 0);
  if (items.length === 0) { event.preventDefault(); dialog.focus(); return; }
  const first = items[0];
  const last = items[items.length - 1];
  if (event.shiftKey && (document.activeElement === first || !dialog.contains(document.activeElement))) {
    event.preventDefault(); last.focus();
  } else if (!event.shiftKey && (document.activeElement === last || !dialog.contains(document.activeElement))) {
    event.preventDefault(); first.focus();
  }
}

export function restoreDialogFocus() {
  const dialog = document.querySelector('.admin-account-dialog');
  if (dialog) dialog.removeEventListener('keydown', trapDialogTab);
  const target = dialogReturnFocus;
  dialogReturnFocus = null;
  if (target?.isConnected && typeof target.focus === 'function') target.focus();
}

export function releaseDialog() {
  const dialog = document.querySelector('.admin-account-dialog');
  if (dialog) dialog.removeEventListener('keydown', trapDialogTab);
  dialogReturnFocus = null;
}
