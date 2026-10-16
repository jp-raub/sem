const MAX_JSON_BYTES = 1024 * 1024;
const MAX_CSRF_BYTES = 16 * 1024;
const MAX_READ_RESPONSE_BYTES = 4 * 1024;
const MAX_REQUEST_BYTES = 4 * 1024;
const REQUEST_TIMEOUT_MS = 25_000;
const PAGE_SIZE = 50;
const MAX_PAGE = 10_000;
const PAGE_LIMIT_MESSAGE = 'APIの最大ページ数10000に達し、残りのメッセージはこの画面から取得できません。';
let liveState = null;
let nextGeneration = 0;

class AuthenticationStateError extends Error {}
class HttpStatusError extends Error {
  constructor(status) { super('HTTP status'); this.status = status; }
}
class StaleOperationError extends Error {}

function isRecord(value) { return value !== null && typeof value === 'object' && !Array.isArray(value); }
function exactKeys(value, expected) {
  if (!isRecord(value)) return false;
  const actual = Object.keys(value).sort();
  const keys = [...expected].sort();
  return actual.length === keys.length && actual.every((key, index) => key === keys[index]);
}
function isCurrent(state) {
  return liveState === state && !state.disposed && !state.pageHidden
    && state.generation === nextGeneration && state.root?.isConnected !== false;
}
function isOperationCurrent(state, operation) { return isCurrent(state) && state.operationGeneration === operation; }
export function inboxPaginationState(page, hasMore) {
  const validPage = Number.isSafeInteger(page) && page >= 1 && page <= MAX_PAGE;
  return Object.freeze({
    canPrevious: validPage && page > 1,
    canNext: validPage && page < MAX_PAGE && hasMore === true,
    limitNotice: validPage && page === MAX_PAGE && hasMore === true ? PAGE_LIMIT_MESSAGE : ''
  });
}
function identifier(value) {
  return typeof value === 'string' && value.length >= 1 && value.length <= 64
    && !/[\u0000-\u001f\u007f-\u009f]/.test(value);
}
function actorIdentifier(value) {
  return typeof value === 'string' && value.length >= 1 && value.length <= 128
    && !/[\u0000-\u001f\u007f-\u009f]/.test(value);
}
function text(value) { return typeof value === 'string' && value.length <= MAX_JSON_BYTES; }
function timestamp(value, nullable = false) {
  if (nullable && value === null) return true;
  return Number.isSafeInteger(value) && value >= 0;
}
function el(state, name) { return state.elements[name]; }
function setText(state, name, value) { el(state, name).textContent = value ?? ''; }
function make(state, tag, value = '') {
  const node = state.root.ownerDocument.createElement(tag);
  node.textContent = value;
  return node;
}
function appendCell(state, row, value, header = false) {
  const cell = make(state, header ? 'th' : 'td', value);
  if (header) cell.setAttribute('scope', 'col');
  row.append(cell);
}
function formatTime(value) {
  if (!Number.isSafeInteger(value) || Math.abs(value * 1000) > 8.64e15) return '日時不明';
  const date = new Date(value * 1000);
  return Number.isNaN(date.getTime()) ? '日時不明' : date.toLocaleString('ja-JP', { timeZone: 'Asia/Tokyo' });
}

async function readBoundedJson(response, maxBytes = MAX_JSON_BYTES) {
  const chunks = [];
  let total = 0;
  if (response.body?.getReader) {
    const reader = response.body.getReader();
    try {
      while (true) {
        const { done, value } = await reader.read();
        if (done) break;
        total += value.byteLength;
        if (total > maxBytes) {
          try { await reader.cancel(); } catch { }
          throw new RangeError('response too large');
        }
        chunks.push(value);
      }
    } finally { try { reader.releaseLock(); } catch { } }
  } else {
    const bytes = new Uint8Array(await response.arrayBuffer());
    if (bytes.byteLength > maxBytes) throw new RangeError('response too large');
    chunks.push(bytes);
    total = bytes.byteLength;
  }
  const bytes = new Uint8Array(total);
  let offset = 0;
  for (const chunk of chunks) { bytes.set(chunk, offset); offset += chunk.byteLength; }
  return JSON.parse(new TextDecoder('utf-8', { fatal: true }).decode(bytes));
}
function cancelBody(response) {
  try { const pending = response.body?.cancel(); pending?.catch?.(() => {}); } catch { }
}

async function requestJson(state, path, { method = 'GET', actorId = null, body = undefined, csrfToken = null, maxBytes = method === 'POST' ? MAX_READ_RESPONSE_BYTES : MAX_JSON_BYTES } = {}) {
  if (!isCurrent(state)) throw new StaleOperationError();
  const url = new URL(path, globalThis.location.href);
  if (url.origin !== globalThis.location.origin || url.username || url.password || url.hash) throw new TypeError('same-origin endpoint required');
  if (actorId !== null) {
    if (!actorIdentifier(actorId) || url.searchParams.has('expectedActor')) throw new TypeError('invalid actor parameter');
    url.searchParams.set('expectedActor', actorId);
    if (url.searchParams.getAll('expectedActor').length !== 1) throw new TypeError('duplicate actor parameter');
  }
  const controller = new AbortController();
  state.controllers.add(controller);
  const timer = setTimeout(() => controller.abort(), REQUEST_TIMEOUT_MS);
  try {
    const headers = { Accept: 'application/json' };
    const options = {
      method, headers, credentials: 'same-origin', mode: 'same-origin', cache: 'no-store',
      redirect: 'error', referrerPolicy: 'same-origin', signal: controller.signal
    };
    if (method === 'POST') {
      const serialized = JSON.stringify(body);
      if (new TextEncoder().encode(serialized).byteLength > MAX_REQUEST_BYTES) throw new RangeError('request too large');
      headers['Content-Type'] = 'application/json';
      headers['X-CSRF-TOKEN'] = csrfToken;
      options.body = serialized;
    }
    const response = await globalThis.fetch(url.href, options);
    if (response.status === 401 || response.status === 403) {
      cancelBody(response);
      throw new AuthenticationStateError();
    }
    const value = await readBoundedJson(response, maxBytes);
    if (!response.ok) throw new HttpStatusError(response.status);
    if (!isCurrent(state)) throw new StaleOperationError();
    return value;
  } finally {
    clearTimeout(timer);
    state.controllers.delete(controller);
  }
}

function validateMessage(value) {
  if (!exactKeys(value, ['id', 'title', 'created', 'readAt']) || !identifier(value.id)
      || !text(value.title) || !timestamp(value.created) || !timestamp(value.readAt, true)) throw new TypeError('invalid inbox message');
  return Object.freeze({ id: value.id, title: value.title, created: value.created, readAt: value.readAt });
}
function validateList(value, requestedPage) {
  if (!exactKeys(value, ['actorId', 'page', 'pageSize', 'hasMore', 'messages'])
      || !actorIdentifier(value.actorId) || value.page !== requestedPage || value.pageSize !== PAGE_SIZE
      || typeof value.hasMore !== 'boolean' || !Array.isArray(value.messages) || value.messages.length > PAGE_SIZE) {
    throw new TypeError('invalid inbox list');
  }
  const messages = value.messages.map(validateMessage);
  if (new Set(messages.map(message => message.id)).size !== messages.length) throw new TypeError('duplicate inbox message');
  return Object.freeze({ actorId: value.actorId, page: value.page, pageSize: value.pageSize, hasMore: value.hasMore, messages });
}
function validateDetail(value, actorId, messageId) {
  if (!exactKeys(value, ['actorId', 'id', 'title', 'body', 'created', 'readAt'])
      || !actorIdentifier(value.actorId) || !identifier(value.id)
      || !text(value.title) || !text(value.body) || !timestamp(value.created) || !timestamp(value.readAt, true)) {
    throw new TypeError('invalid inbox detail');
  }
  if (value.actorId !== actorId) throw new AuthenticationStateError();
  if (value.id !== messageId) throw new TypeError('inbox detail target mismatch');
  return Object.freeze({ id: value.id, title: value.title, body: value.body, created: value.created, readAt: value.readAt });
}
function validateReadResponse(value, actorId, messageId) {
  if (!exactKeys(value, ['actorId', 'id', 'readAt']) || !actorIdentifier(value.actorId) || !identifier(value.id)
      || !timestamp(value.readAt)) throw new TypeError('invalid inbox read response');
  if (value.actorId !== actorId) throw new AuthenticationStateError();
  if (value.id !== messageId) throw new TypeError('inbox read target mismatch');
  return value.readAt;
}

function clearDetail(state) {
  state.selectedId = null;
  state.detail = null;
  state.detailStatus = '';
  state.detailError = '';
  setText(state, 'detailTitle', '');
  setText(state, 'detailCreated', '');
  setText(state, 'detailReadState', '');
  setText(state, 'detailBody', '');
  setText(state, 'detailStatus', '');
  setText(state, 'detailError', '');
}
function renderList(state) {
  const body = el(state, 'listBody');
  body.replaceChildren();
  state.rowButtons = new WeakMap();
  if (state.messages.length === 0) {
    const row = make(state, 'tr');
    const cell = make(state, 'td', state.loaded ? '受信箱は空です。' : '一覧を準備しています。');
    cell.setAttribute('colspan', '5');
    row.append(cell);
    body.append(row);
    return;
  }
  for (const message of state.messages) {
    const row = make(state, 'tr');
    appendCell(state, row, message.id);
    appendCell(state, row, message.title);
    appendCell(state, row, formatTime(message.created));
    appendCell(state, row, message.readAt === null ? '未読' : '既読');
    const action = make(state, 'td');
    const button = make(state, 'button', '本文を表示');
    button.type = 'button';
    button.disabled = state.busy;
    state.rowButtons.set(button, message.id);
    action.append(button);
    row.append(action);
    body.append(row);
  }
}
function render(state) {
  if (!isCurrent(state)) return;
  const pagination = inboxPaginationState(state.page, state.hasMore);
  el(state, 'refresh').disabled = state.busy || (!state.actorId && !state.readErrorLock);
  el(state, 'previous').disabled = !state.actorId || state.busy || state.readErrorLock || !pagination.canPrevious;
  el(state, 'next').disabled = !state.actorId || state.busy || state.readErrorLock || !pagination.canNext;
  setText(state, 'status', state.statusMessage);
  setText(state, 'error', state.errorMessage);
  setText(state, 'page', `ページ ${state.loaded ? state.page : '—'}（最大${PAGE_SIZE}件ずつ。全件取得済みとは限りません）${pagination.limitNotice ? ` ${pagination.limitNotice}` : ''}`);
  el(state, 'unknown').hidden = !state.unknown;
  setText(state, 'unknown', state.unknown?.message ?? '');
  el(state, 'detail').hidden = state.selectedId === null;
  setText(state, 'detailStatus', state.detailStatus);
  setText(state, 'detailError', state.detailError);
  setText(state, 'detailTitle', state.detail?.title ?? '');
  setText(state, 'detailCreated', state.detail ? formatTime(state.detail.created) : '');
  setText(state, 'detailReadState', state.detail ? state.detail.readAt === null ? '未読' : '既読' : '');
  setText(state, 'detailBody', state.detail?.body ?? '');
  el(state, 'detailRefresh').disabled = !state.actorId || !state.selectedId || state.busy;
  el(state, 'markRead').disabled = !state.actorId || !state.detail || state.detail.readAt !== null
    || state.busy || state.readErrorLock || !!state.unknown;
  renderList(state);
}

function clearActorState(state) {
  state.operationGeneration++;
  state.actorId = null;
  state.messages = [];
  state.loaded = false;
  state.hasMore = false;
  state.unknown = null;
  state.readErrorLock = true;
  state.busy = false;
  state.errorMessage = '';
  state.statusMessage = 'ログイン状態またはアカウントが変わりました。表示と操作を停止しました。状態を再読込してください。';
  clearDetail(state);
  render(state);
}

async function loadList(state, page) {
  if (!isCurrent(state) || state.busy || !Number.isSafeInteger(page) || page < 1 || page > MAX_PAGE) return;
  const operation = ++state.operationGeneration;
  state.busy = true;
  state.errorMessage = '';
  state.statusMessage = `ページ ${page} を読み込んでいます。`;
  clearDetail(state);
  render(state);
  try {
    const url = new URL('/api/core/inbox', globalThis.location.href);
    url.searchParams.set('page', String(page));
    const value = await requestJson(state, `${url.pathname}${url.search}`, { maxBytes: MAX_JSON_BYTES });
    if (!isOperationCurrent(state, operation)) return;
    const data = validateList(value, page);
    if (state.actorId !== null && data.actorId !== state.actorId) throw new AuthenticationStateError();
    state.actorId = data.actorId;
    state.page = data.page;
    state.hasMore = data.hasMore;
    state.messages = data.messages;
    state.loaded = true;
    state.readErrorLock = false;
    state.statusMessage = state.unknown
      ? '一覧を更新しました。既読操作の結果は未確認です。対象本文を開いて読み取り専用で再確認してください。'
      : `ページ ${page} を表示しています。メッセージ ${data.messages.length} 件。`;
    state.errorMessage = '';
    render(state);
  } catch (error) {
    if (!isOperationCurrent(state, operation)) return;
    if (error instanceof AuthenticationStateError) clearActorState(state);
    else {
      state.readErrorLock = true;
      state.errorMessage = error instanceof RangeError
        ? '受信箱の応答が上限を超えたか、形式を確認できませんでした。部分データは使用せず、既読操作を停止しました。'
        : '受信箱を安全に読み込めませんでした。通信状態を確認し、明示的に再読込してください。';
      state.statusMessage = '受信箱を読み込めませんでした。';
      render(state);
    }
  } finally {
    if (isOperationCurrent(state, operation)) { state.busy = false; render(state); }
  }
}

async function loadDetail(state, messageId) {
  if (!isCurrent(state) || state.busy || !state.actorId || !identifier(messageId)
      || !state.messages.some(message => message.id === messageId)) return;
  const actorId = state.actorId;
  const operation = ++state.operationGeneration;
  state.busy = true;
  state.selectedId = messageId;
  state.detail = null;
  state.detailStatus = '本文を読み込んでいます。開くだけでは既読になりません。';
  state.detailError = '';
  render(state);
  try {
    const url = new URL('/api/core/inbox/message', globalThis.location.href);
    url.searchParams.set('id', messageId);
    const value = await requestJson(state, `${url.pathname}${url.search}`, { actorId, maxBytes: MAX_JSON_BYTES });
    if (!isOperationCurrent(state, operation) || state.actorId !== actorId || state.selectedId !== messageId) return;
    const detail = validateDetail(value, actorId, messageId);
    state.detail = detail;
    state.detailStatus = '本文を読み取り専用で表示しています。既読にする場合は専用ボタンを押してください。';
    state.messages = state.messages.map(message => message.id === messageId ? Object.freeze({ ...message, readAt: detail.readAt }) : message);
    if (state.unknown?.id === messageId) {
      if (detail.readAt !== null) {
        state.unknown = null;
        state.statusMessage = `メッセージ ${messageId} の既読状態を読み取り専用で確認しました。`;
        state.detailStatus = '既読状態を再確認しました。追加の書き込みは行っていません。';
      } else {
        state.unknown.message = `メッセージ ${messageId} はまだ未読として返されました。書き込み結果は未確定のため、既読操作を自動再送しません。必要に応じて本文の状態を再確認してください。`;
        state.detailStatus = 'このメッセージは未読です。結果不明の操作はロック中です。';
      }
    }
    render(state);
  } catch (error) {
    if (!isOperationCurrent(state, operation)) return;
    if (error instanceof AuthenticationStateError) clearActorState(state);
    else {
      state.detailError = '本文を安全に読み込めませんでした。既読操作は行っていません。';
      state.detailStatus = '';
      render(state);
    }
  } finally {
    if (isOperationCurrent(state, operation)) { state.busy = false; render(state); }
  }
}

async function readCsrf(state) {
  const value = await requestJson(state, '/api/csrf', { maxBytes: MAX_CSRF_BYTES });
  if (!isCurrent(state)) throw new StaleOperationError();
  if (!exactKeys(value, ['token']) || typeof value.token !== 'string' || value.token.length < 1
      || value.token.length > 8192 || /[\u0000-\u001f\u007f-\u009f]/.test(value.token)) throw new TypeError('invalid csrf response');
  return value.token;
}
async function markRead(state, event) {
  event.preventDefault();
  const detail = state.detail;
  if (!isCurrent(state) || !state.actorId || state.busy || state.readErrorLock || state.unknown
      || !detail || detail.readAt !== null || detail.id !== state.selectedId) return;
  const actorId = state.actorId;
  const messageId = detail.id;
  const operation = ++state.operationGeneration;
  state.busy = true;
  state.errorMessage = '';
  state.statusMessage = `メッセージ ${messageId} を既読にしています。`;
  render(state);
  try {
    const csrfToken = await readCsrf(state);
    if (!isOperationCurrent(state, operation) || state.actorId !== actorId || state.detail?.id !== messageId) return;
    state.unknown = {
      id: messageId,
      message: `メッセージ ${messageId} の既読結果は未確認です。自動再送しません。対象本文を開き、読み取り専用で既読状態を再確認してください。`
    };
    render(state);
    const value = await requestJson(state, '/api/core/inbox/read', {
      method: 'POST', actorId, body: { id: messageId }, csrfToken, maxBytes: MAX_READ_RESPONSE_BYTES
    });
    if (!isOperationCurrent(state, operation) || state.actorId !== actorId || state.detail?.id !== messageId) return;
    const readAt = validateReadResponse(value, actorId, messageId);
    state.unknown = null;
    state.detail = Object.freeze({ ...detail, readAt });
    state.messages = state.messages.map(message => message.id === messageId ? Object.freeze({ ...message, readAt }) : message);
    state.statusMessage = `メッセージ ${messageId} を既読にしました。共通メニューの未読数は古い場合があるため、必要ならページを再読み込みして更新してください。`;
    state.detailStatus = '既読として記録されました。';
    state.errorMessage = '';
    render(state);
  } catch (error) {
    if (!isOperationCurrent(state, operation)) return;
    if (error instanceof AuthenticationStateError) clearActorState(state);
    else if (state.unknown?.id === messageId) {
      state.unknown.message = `メッセージ ${messageId} の既読結果は未確認です。自動再送しません。対象本文を開き、読み取り専用で既読状態を再確認してください。`;
      state.statusMessage = '既読結果は未確認です。既読操作をロックしました。';
      state.detailStatus = '状態を確認するまでは既読操作を再実行しません。';
      render(state);
    } else {
      state.errorMessage = '既読操作を開始できませんでした。書き込み要求は送信していません。';
      render(state);
    }
  } finally {
    if (isOperationCurrent(state, operation)) { state.busy = false; render(state); }
  }
}

function abortRequests(state) {
  for (const controller of state.controllers) { try { controller.abort(); } catch { } }
}
function onPageHide(state) {
  if (liveState !== state || state.disposed || state.pageHidden || state.generation !== nextGeneration) return;
  state.operationGeneration++;
  state.actorId = null;
  state.messages = [];
  state.loaded = false;
  state.hasMore = false;
  state.page = 1;
  state.readErrorLock = true;
  state.busy = false;
  state.statusMessage = 'ページを離れたため、受信箱の内容を消去しました。復帰時に読み取り専用で再確認します。';
  state.errorMessage = '';
  clearDetail(state);
  render(state);
  el(state, 'unknown').hidden = true;
  setText(state, 'unknown', '');
  state.pageHidden = true;
  abortRequests(state);
}
function onPageShow(state) {
  if (!state.pageHidden || liveState !== state || state.disposed || state.generation !== nextGeneration || state.root?.isConnected === false) return;
  state.pageHidden = false;
  state.operationGeneration++;
  state.actorId = null;
  state.messages = [];
  state.loaded = false;
  state.hasMore = false;
  state.page = 1;
  state.readErrorLock = true;
  state.busy = false;
  state.statusMessage = '受信箱へ復帰しました。変更操作の前に一覧を読み取り専用で再確認しています。';
  state.errorMessage = '';
  clearDetail(state);
  render(state);
  void loadList(state, 1);
}
function clearVisible(state) {
  el(state, 'listBody').replaceChildren();
  clearDetail(state);
  setText(state, 'unknown', '');
}
function disposeState(state) {
  if (!state || state.disposed) return;
  state.disposed = true;
  state.root.removeEventListener('click', state.listeners.click);
  globalThis.removeEventListener?.('pagehide', state.listeners.pageHide);
  globalThis.removeEventListener?.('pageshow', state.listeners.pageShow);
  abortRequests(state);
  state.controllers.clear();
  state.operationGeneration++;
  state.actorId = null;
  state.messages = [];
  state.detail = null;
  state.unknown = null;
  clearVisible(state);
  if (liveState === state) { liveState = null; nextGeneration++; }
}

function elements(root) {
  const names = ['status', 'error', 'refresh', 'listBody', 'previous', 'page', 'next', 'unknown', 'detail',
    'detailStatus', 'detailError', 'detailTitle', 'detailCreated', 'detailReadState', 'detailBody', 'detailRefresh', 'markRead'];
  return Object.fromEntries(names.map(name => [name, root.querySelector(`#inbox-${name.replace(/[A-Z]/g, value => `-${value.toLowerCase()}`)}`)]));
}

export async function initialize(root, owner) {
  if (!root || typeof root.querySelector !== 'function' || typeof root.addEventListener !== 'function'
      || typeof owner !== 'string' || owner.length < 1) throw new TypeError('root and owner required');
  if (liveState?.owner === owner && liveState.root === root && !liveState.disposed) return;
  if (liveState) disposeState(liveState);
  const state = {
    root, owner, generation: ++nextGeneration, operationGeneration: 0, disposed: false, pageHidden: false, controllers: new Set(),
    elements: elements(root), listeners: null, actorId: null, page: 1, messages: [], hasMore: false, loaded: false,
    selectedId: null, detail: null, unknown: null, readErrorLock: false, busy: false, errorMessage: '',
    statusMessage: '受信箱を準備しています。', detailStatus: '', detailError: '', rowButtons: new WeakMap()
  };
  if (Object.values(state.elements).some(node => !node)) throw new TypeError('inbox UI elements missing');
  liveState = state;
  state.listeners = {
    click: event => {
      const target = event.target;
      if (target === el(state, 'refresh')) void loadList(state, state.page || 1);
      else if (target === el(state, 'previous')) void loadList(state, state.page - 1);
      else if (target === el(state, 'next')) void loadList(state, state.page + 1);
      else if (target === el(state, 'detailRefresh')) void loadDetail(state, state.selectedId);
      else if (target === el(state, 'markRead')) void markRead(state, event);
      else {
        const messageId = state.rowButtons.get(target);
        if (messageId) void loadDetail(state, messageId);
      }
    },
    pageHide: () => onPageHide(state),
    pageShow: () => onPageShow(state)
  };
  root.addEventListener('click', state.listeners.click);
  globalThis.addEventListener?.('pagehide', state.listeners.pageHide);
  globalThis.addEventListener?.('pageshow', state.listeners.pageShow);
  render(state);
  await loadList(state, 1);
}
export function dispose(owner) {
  if (liveState?.owner === owner) disposeState(liveState);
}