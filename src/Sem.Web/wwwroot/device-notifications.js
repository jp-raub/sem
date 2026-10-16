const MAX_JSON_BYTES = 16 * 1024;
const REQUEST_TIMEOUT_MS = 25_000;
const P256_FIELD = 0xffffffff00000001000000000000000000000000ffffffffffffffffffffffffn;
const P256_B = 0x5ac635d8aa3a93e7b3ebbd55769886bc651d06b0cc53b0f63bce3c3e27d2604bn;
let liveState = null;
let nextGeneration = 0;
const pendingNativeMutations = new Set();
const unreconciledNativeMutations = new Set();
let nativeMutationVersion = 0;

export class AuthenticationStateError extends Error {}
export class HttpStatusError extends Error {
  constructor(status) { super('HTTP status'); this.status = status; }
}
export class StaleOperationError extends Error {}
export class NativeOperationTimeoutError extends Error {}

export async function readBoundedJson(response, maxBytes = MAX_JSON_BYTES) {
  if (!Number.isSafeInteger(maxBytes) || maxBytes < 1 || maxBytes > MAX_JSON_BYTES) throw new TypeError('invalid response bound');
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
    } finally {
      try { reader.releaseLock(); } catch { }
    }
  } else {
    const value = new Uint8Array(await response.arrayBuffer());
    if (value.byteLength > maxBytes) throw new RangeError('response too large');
    chunks.push(value);
    total = value.byteLength;
  }
  const bytes = new Uint8Array(total);
  let offset = 0;
  for (const chunk of chunks) { bytes.set(chunk, offset); offset += chunk.byteLength; }
  const text = new TextDecoder('utf-8', { fatal: true }).decode(bytes);
  return JSON.parse(text);
}

function modP256(value) {
  const remainder = value % P256_FIELD;
  return remainder < 0n ? remainder + P256_FIELD : remainder;
}

function bytesToBigInt(bytes, start, end) {
  let value = 0n;
  for (let index = start; index < end; index++) value = (value << 8n) | BigInt(bytes[index]);
  return value;
}

export function decodeApplicationServerKey(value) {
  if (typeof value !== 'string' || value.length < 1 || value.length > 2048 || !/^[A-Za-z0-9_-]+$/.test(value)) throw new TypeError('invalid public key');
  const normalized = value.replace(/-/g, '+').replace(/_/g, '/');
  const padded = normalized + '='.repeat((4 - normalized.length % 4) % 4);
  let binary;
  try { binary = globalThis.atob(padded); } catch { throw new TypeError('invalid public key'); }
  if (binary.length !== 65 || binary.charCodeAt(0) !== 0x04) throw new TypeError('invalid public key');
  const canonical = globalThis.btoa(binary).replace(/\+/g, '-').replace(/\//g, '_').replace(/=+$/, '');
  if (canonical !== value) throw new TypeError('invalid public key');
  const output = new Uint8Array(binary.length);
  for (let index = 0; index < binary.length; index++) output[index] = binary.charCodeAt(index);
  const x = bytesToBigInt(output, 1, 33);
  const y = bytesToBigInt(output, 33, 65);
  if (x >= P256_FIELD || y >= P256_FIELD) throw new TypeError('invalid public key');
  const left = (y * y) % P256_FIELD;
  const right = modP256((x * x % P256_FIELD) * x - 3n * x + P256_B);
  if (left !== right) throw new TypeError('invalid public key');
  return output;
}

function isRecord(value) { return value !== null && typeof value === 'object' && !Array.isArray(value); }
function safeActor(value) { return typeof value === 'string' && value.length > 0 && value.length <= 256 && !/[\u0000-\u001f\u007f]/.test(value); }
function isCurrent(state) { return liveState === state && !state.disposed && state.generation === nextGeneration && state.root?.isConnected !== false; }
function node(state, name) { return state.nodes[name]; }
function say(state, name, text) { const target = node(state, name); if (target) target.textContent = text; }
function nativeMutationLocked(state) {
  return !!state.nativeRecoveryRequired || pendingNativeMutations.size > 0 || unreconciledNativeMutations.size > 0;
}

function nativeOperationLabel(kind) {
  if (kind === 'unsubscribe') return 'ブラウザーの購読解除';
  if (kind === 'subscribe') return 'ブラウザーの購読作成';
  return 'Service Worker登録';
}

function nativeRecoveryMessage() {
  const pending = [...pendingNativeMutations][0];
  return pending
    ? `${nativeOperationLabel(pending.kind)}はまだ完了していません。完了を待ってから「状態を手動で再確認」を実行してください。登録／解除操作は停止しています。`
    : 'ブラウザーの処理は終了しました。状態を手動で再確認するまで登録／解除操作を停止しています。自動再実行はしません。';
}

function markNativeRecovery(state = liveState) {
  if (!state) return;
  state.nativeRecoveryRequired = true;
  state.statusMessage = nativeRecoveryMessage();
  if (isCurrent(state)) {
    say(state, 'status', state.statusMessage);
    update(state);
  }
}

function settleNativeMutation(record) {
  if (record.settled) return;
  record.settled = true;
  pendingNativeMutations.delete(record);
  nativeMutationVersion++;
  if (record.timedOut || !isCurrent(record.state)) {
    unreconciledNativeMutations.add(record);
    markNativeRecovery(liveState);
  } else if (isCurrent(record.state)) {
    update(record.state);
  }
}

function startNativeMutation(state, kind, action) {
  const record = { state, owner: state.owner, kind, timedOut: false, settled: false, safeWithoutSubscription: true };
  pendingNativeMutations.add(record);
  nativeMutationVersion++;
  let result;
  try { result = action(); }
  catch (error) { result = Promise.reject(error); }
  record.promise = Promise.resolve(result).then(
    value => { settleNativeMutation(record); return value; },
    error => { settleNativeMutation(record); throw error; }
  );
  return record;
}

function noteNativeTimeout(record) {
  record.timedOut = true;
  unreconciledNativeMutations.add(record);
  nativeMutationVersion++;
  markNativeRecovery(liveState);
}

function update(state) {
  if (!isCurrent(state)) return;
  const { supported, configured, permission, actorId, busy, uncertain, subscription, registered, localStateUnknown } = state;
  const ready = !!actorId;
  const nativeLocked = nativeMutationLocked(state);
  node(state, 'register').disabled = !ready || !supported || !configured || busy || !!uncertain || localStateUnknown || nativeLocked || permission === 'denied' || registered === true;
  node(state, 'unregister').disabled = !ready || !subscription || busy || !!uncertain || localStateUnknown || nativeLocked;
  node(state, 'refresh').disabled = !ready || busy;
  const permissionText = permission === 'granted' ? '通知許可：許可済み。' : permission === 'denied' ? '通知許可：拒否されています。ブラウザーのサイト設定から許可を変更してください。' : permission === 'default' ? '通知許可：未決定です。許可の確認は登録ボタンを押したときだけ行います。' : '通知許可：このブラウザーでは確認できません。';
  say(state, 'permission', permissionText);
  if (!supported) say(state, 'capability', 'この環境では安全なPush登録に必要なブラウザー機能を利用できません。HTTPS対応ブラウザーで開いてください。');
  else if (!configured) say(state, 'capability', 'サーバー側のPush配信設定がありません。登録操作は利用できません。');
  else say(state, 'capability', 'Push対応ブラウザーです。このページを開いただけでは許可や登録を変更しません。');
}

function cancelResponse(response) {
  try { const promise = response.body?.cancel(); promise?.catch?.(() => {}); } catch { }
}

function boundedPromise(state, promise, nativeRecord = null) {
  return new Promise((resolve, reject) => {
    let settled = false;
    const finish = (callback, value) => {
      if (settled) return;
      settled = true;
      clearTimeout(timer);
      state.nativeCancels.delete(cancel);
      callback(value);
    };
    const cancel = () => finish(reject, new StaleOperationError());
    const timer = setTimeout(() => {
      if (nativeRecord) {
        noteNativeTimeout(nativeRecord);
        finish(reject, new NativeOperationTimeoutError());
      } else finish(reject, new Error('native operation timed out'));
    }, REQUEST_TIMEOUT_MS);
    state.nativeCancels.add(cancel);
    Promise.resolve(promise).then(
      value => {
        if (settled || !isCurrent(state)) { finish(reject, new StaleOperationError()); return; }
        finish(resolve, value);
      },
      error => finish(reject, error)
    );
  });
}

async function requestJson(state, path, { method = 'GET', actorId = null, body = undefined, csrfToken = null } = {}) {
  if (!isCurrent(state)) throw new StaleOperationError();
  const url = new URL(path, globalThis.location.href);
  if (url.origin !== globalThis.location.origin || url.username || url.password || url.hash) throw new TypeError('same-origin endpoint required');
  if (actorId !== null) {
    if (url.searchParams.has('expectedActor')) throw new TypeError('duplicate actor parameter');
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
      headers['Content-Type'] = 'application/json';
      headers['X-CSRF-TOKEN'] = csrfToken;
      options.body = JSON.stringify(body);
    }
    const response = await globalThis.fetch(url.href, options);
    if (response.status === 401 || response.status === 403) {
      cancelResponse(response);
      throw new AuthenticationStateError();
    }
    const value = await readBoundedJson(response);
    if (!response.ok) throw new HttpStatusError(response.status);
    if (!isCurrent(state)) throw new StaleOperationError();
    return value;
  } finally {
    clearTimeout(timer);
    state.controllers.delete(controller);
  }
}

async function readContext(state) {
  const value = await requestJson(state, '/api/core/device-notifications');
  if (!isRecord(value) || !safeActor(value.actorId) || typeof value.pushConfigured !== 'boolean' || value.pushConfigured && (typeof value.pushPublicKey !== 'string' || value.pushPublicKey.length > 2048)) throw new TypeError('invalid context');
  if (value.pushConfigured) decodeApplicationServerKey(value.pushPublicKey);
  return value;
}

async function readCsrf(state) {
  const value = await requestJson(state, '/api/csrf');
  if (!isRecord(value) || typeof value.token !== 'string' || value.token.length < 1 || value.token.length > 8192 || /[\u0000-\u001f\u007f]/.test(value.token)) throw new TypeError('invalid csrf response');
  return value.token;
}

async function assertCurrentActor(state, expectedActor) {
  const context = await readContext(state);
  if (!isCurrent(state)) throw new StaleOperationError();
  if (context.actorId !== expectedActor) throw new AuthenticationStateError();
  state.configured = context.pushConfigured;
  state.pushPublicKey = context.pushConfigured ? context.pushPublicKey : null;
  update(state);
  return context;
}

function getBrowserCapabilities() {
  const secure = globalThis.isSecureContext === true || globalThis.location?.hostname === 'localhost' || globalThis.location?.hostname === '127.0.0.1';
  const notificationAvailable = typeof globalThis.Notification !== 'undefined' && typeof globalThis.Notification.requestPermission === 'function';
  const workerAvailable = !!globalThis.navigator?.serviceWorker && typeof globalThis.navigator.serviceWorker.getRegistration === 'function' && typeof globalThis.navigator.serviceWorker.register === 'function';
  const pushAvailable = typeof globalThis.PushManager !== 'undefined';
  return { supported: secure && notificationAvailable && workerAvailable && pushAvailable, permission: notificationAvailable ? globalThis.Notification.permission : 'unavailable' };
}

async function currentSubscription(state) {
  if (!state.supported) return { registration: null, subscription: null };
  const registration = await boundedPromise(state, globalThis.navigator.serviceWorker.getRegistration('/'));
  const expectedScope = new URL('/', globalThis.location.origin).href;
  if (!registration || registration.scope !== expectedScope || !registration.pushManager) return { registration: null, subscription: null };
  const subscription = await boundedPromise(state, registration.pushManager.getSubscription());
  return { registration, subscription };
}

async function fetchStatus(state, subscription) {
  const endpoint = subscription?.endpoint;
  if (typeof endpoint !== 'string' || endpoint.length < 1 || endpoint.length > 2048) throw new TypeError('no current endpoint');
  const csrfToken = await readCsrf(state);
  if (!isCurrent(state)) throw new StaleOperationError();
  const value = await requestJson(state, '/api/core/device-notifications/status', {
    method: 'POST', actorId: state.actorId, csrfToken, body: { endpoint }
  });
  if (!isRecord(value) || value.actorId !== state.actorId || typeof value.registered !== 'boolean') throw new TypeError('invalid status response');
  return value.registered;
}

function showAuthenticationFailure(state) {
  state.actorId = null;
  state.configured = false;
  state.pushPublicKey = null;
  state.registration = null;
  state.subscription = null;
  state.registered = null;
  state.uncertain = null;
  state.statusMessage = 'ログイン状態またはアカウントが変わりました。操作を停止しました。再読み込みして、ログイン状態を確認してください。';
  say(state, 'error', '');
  update(state);
  say(state, 'status', state.statusMessage);
}

function handleError(state, error, phase) {
  if (!isCurrent(state) || error instanceof StaleOperationError) return;
  if (error instanceof AuthenticationStateError) { showAuthenticationFailure(state); return; }
  if ((phase === 'write' || phase === 'status') && state.uncertain) {
    state.statusMessage = '操作結果を確認できません。自動再送はしていません。下の「状態を手動で再確認」を押して、現在のサーバー登録状態を照合してください。';
    say(state, 'error', '');
    say(state, 'status', state.statusMessage);
    update(state);
    return;
  }
  const message = phase === 'status'
    ? '登録状態を確認できませんでした。通信状態を確認して、手動で再確認してください。'
    : phase === 'write'
      ? '操作を完了できませんでした。サーバーへの変更は開始していません。'
      : 'ログイン状態と通知設定を読み込めませんでした。ページを再読み込みしてください。';
  say(state, 'error', message);
  if (phase !== 'initial') state.statusMessage = message;
  say(state, 'status', state.statusMessage || message);
}

function finishManualNativeReconciliation(state, snapshot, { hasSubscription }) {
  if (!snapshot) return;
  const noSubscriptionIsSafe = hasSubscription || (!state.uncertain && [...unreconciledNativeMutations].every(record => record.safeWithoutSubscription));
  const unchanged = snapshot.pendingAtStart === 0
    && pendingNativeMutations.size === 0
    && nativeMutationVersion === snapshot.version;
  if (unchanged && noSubscriptionIsSafe) {
    unreconciledNativeMutations.clear();
    state.nativeRecoveryRequired = false;
    return;
  }
  if (pendingNativeMutations.size || unreconciledNativeMutations.size || state.nativeRecoveryRequired) {
    state.nativeRecoveryRequired = true;
  }
}

function appendNativeRecoveryMessage(state) {
  if (!nativeMutationLocked(state)) return;
  const recovery = nativeRecoveryMessage();
  if (!state.statusMessage.includes(recovery)) state.statusMessage = `${state.statusMessage} ${recovery}`;
}

async function reconcile(state, { refreshContext = false, manual = false } = {}) {
  const capturedActor = state.actorId;
  const nativeSnapshot = manual
    ? { version: nativeMutationVersion, pendingAtStart: pendingNativeMutations.size }
    : null;
  if (refreshContext) {
    const context = await readContext(state);
    if (!isCurrent(state)) throw new StaleOperationError();
    if (context.actorId !== capturedActor) throw new AuthenticationStateError();
    state.configured = context.pushConfigured;
    state.pushPublicKey = context.pushConfigured ? context.pushPublicKey : null;
  }
  const caps = getBrowserCapabilities();
  state.supported = caps.supported;
  state.permission = caps.permission;
  const local = await currentSubscription(state);
  if (!isCurrent(state)) throw new StaleOperationError();
  state.registration = local.registration;
  state.subscription = local.subscription;
  state.localStateUnknown = false;
  if (!state.subscription) {
    state.registered = null;
    state.statusMessage = 'このブラウザーに有効なPush登録がありません。サーバー側の端末登録状態は、照合できるブラウザー登録がないため確認できません。';
    if (state.uncertain) state.statusMessage += ' 結果不明の操作は未照合のままです。';
    finishManualNativeReconciliation(state, nativeSnapshot, { hasSubscription: false });
    appendNativeRecoveryMessage(state);
    say(state, 'error', '');
    say(state, 'status', state.statusMessage);
    update(state);
    return false;
  }
  state.statusMessage = 'このブラウザーのサーバー登録状態を確認しています。';
  say(state, 'status', state.statusMessage);
  update(state);
  const registered = await fetchStatus(state, state.subscription);
  if (!isCurrent(state)) throw new StaleOperationError();
  state.registered = registered;
  state.uncertain = null;
  state.statusMessage = registered
    ? 'このブラウザーは現在のログイン状態に紐付いてPush登録されています。'
    : 'このブラウザーにPush購読はありますが、現在のログイン状態に紐付いたサーバー登録は確認できません。自動では再登録しません。';
  finishManualNativeReconciliation(state, nativeSnapshot, { hasSubscription: true });
  appendNativeRecoveryMessage(state);
  say(state, 'error', '');
  say(state, 'status', state.statusMessage);
  update(state);
  return true;
}

function findNodes(root) {
  const select = id => root.querySelector(`#device-notifications-${id}`);
  return {
    capability: select('capability'), permission: select('permission'), status: select('status'), error: select('error'),
    register: select('register'), unregister: select('unregister'), refresh: select('refresh')
  };
}

async function initialLoad(state) {
  const capabilities = getBrowserCapabilities();
  state.supported = capabilities.supported;
  state.permission = capabilities.permission;
  update(state);
  const context = await readContext(state);
  if (!isCurrent(state)) return;
  state.actorId = context.actorId;
  state.configured = context.pushConfigured;
  state.pushPublicKey = context.pushConfigured ? context.pushPublicKey : null;
  say(state, 'error', '');
  try { await reconcile(state); }
  catch (error) { displayUnexpected(state, 'status', error); }
}

function displayUnexpected(state, phase, error) {
  if (!isCurrent(state)) return;
  handleError(state, error, phase);
}

async function registerFromClick(state) {
  if (!isCurrent(state) || !state.actorId || !state.supported || !state.configured || state.busy || state.uncertain || nativeMutationLocked(state) || state.registered === true || state.permission === 'denied') return;
  state.busy = true;
  state.statusMessage = 'ブラウザーの許可とPush登録を確認しています。';
  say(state, 'error', ''); say(state, 'status', state.statusMessage); update(state);
  try {
    const expectedActor = state.actorId;
    // Validate the cached VAPID key before any browser permission prompt.
    let applicationServerKey = decodeApplicationServerKey(state.pushPublicKey);
    let permission = globalThis.Notification.permission;
    if (permission === 'default') {
      // Keep the permission request directly inside the explicit user gesture.
      const permissionPromise = globalThis.Notification.requestPermission();
      permission = await boundedPromise(state, permissionPromise);
      if (!isCurrent(state)) return;
    }
    if (!isCurrent(state)) return;
    state.permission = permission;
    if (permission !== 'granted') throw new Error('permission not granted');
    const context = await assertCurrentActor(state, expectedActor);
    if (!isCurrent(state)) return;
    if (!context.pushConfigured) throw new Error('push configuration unavailable');
    applicationServerKey = decodeApplicationServerKey(context.pushPublicKey);
    let registration = state.registration;
    const expectedScope = new URL('/', globalThis.location.origin).href;
    if (!registration || registration.scope !== expectedScope) {
      const workerMutation = startNativeMutation(state, 'service-worker-register', () => globalThis.navigator.serviceWorker.register('/sem-sw.js', { scope: '/' }));
      registration = await boundedPromise(state, workerMutation.promise, workerMutation);
      if (!isCurrent(state)) return;
      if (registration.scope !== expectedScope) throw new Error('unexpected worker scope');
      await boundedPromise(state, globalThis.navigator.serviceWorker.ready);
      if (!isCurrent(state)) return;
    }
    state.registration = registration;
    await assertCurrentActor(state, expectedActor);
    if (!isCurrent(state)) return;
    let subscription = await boundedPromise(state, registration.pushManager.getSubscription());
    if (!isCurrent(state)) return;
    await assertCurrentActor(state, expectedActor);
    if (!isCurrent(state)) return;
    if (!subscription) {
      const subscriptionMutation = startNativeMutation(state, 'subscribe', () => registration.pushManager.subscribe({ userVisibleOnly: true, applicationServerKey }));
      subscription = await boundedPromise(state, subscriptionMutation.promise, subscriptionMutation);
      if (!isCurrent(state)) return;
      // Account identity is checked again after non-abortable native work and before server binding.
      await assertCurrentActor(state, expectedActor);
      if (!isCurrent(state)) return;
    }
    state.subscription = subscription;
    const data = subscription?.toJSON?.();
    const endpoint = data?.endpoint;
    const p256dh = data?.keys?.p256dh;
    const auth = data?.keys?.auth;
    if (typeof endpoint !== 'string' || endpoint.length < 1 || endpoint.length > 2048 || typeof p256dh !== 'string' || p256dh.length < 1 || p256dh.length > 256 || typeof auth !== 'string' || auth.length < 1 || auth.length > 256) throw new Error('invalid subscription');
    const csrfToken = await readCsrf(state);
    if (!isCurrent(state)) return;
    state.uncertain = 'register';
    state.statusMessage = '登録結果をサーバーから確認しています。';
    say(state, 'status', state.statusMessage); update(state);
    const value = await requestJson(state, '/api/core/device-notifications/register', {
      method: 'POST', actorId: state.actorId, csrfToken, body: { endpoint, p256dh, auth }
    });
    if (!isRecord(value) || value.actorId !== state.actorId || value.registered !== true) throw new TypeError('invalid register response');
    state.uncertain = null;
    state.registered = true;
    state.statusMessage = 'このブラウザーのPush登録を受け付けました。ブラウザーの通知許可は登録解除後も残ります。';
    say(state, 'error', ''); say(state, 'status', state.statusMessage);
  } catch (error) {
    if (error instanceof NativeOperationTimeoutError && isCurrent(state)) {
      state.nativeRecoveryRequired = true;
      state.statusMessage = nativeRecoveryMessage();
      say(state, 'error', ''); say(state, 'status', state.statusMessage); update(state);
    } else displayUnexpected(state, state.uncertain ? 'write' : 'register', error);
  } finally {
    if (isCurrent(state)) { state.busy = false; update(state); }
  }
}

async function refreshSubscriptionAfterUnregister(state) {
  try {
    const currentSubscription = await boundedPromise(state, state.registration.pushManager.getSubscription());
    if (!isCurrent(state)) return false;
    state.subscription = currentSubscription;
    state.localStateUnknown = false;
    return true;
  } catch (error) {
    if (!isCurrent(state) || error instanceof StaleOperationError) return false;
    state.subscription = null;
    state.localStateUnknown = true;
    state.registered = false;
    state.statusMessage = 'サーバー側の解除は完了しましたが、このブラウザーの購読状態は未確認です。状態を手動で再確認してください。';
    say(state, 'error', ''); say(state, 'status', state.statusMessage); update(state);
    return false;
  }
}

async function unregisterFromClick(state) {
  if (!isCurrent(state) || !state.actorId || !state.subscription || state.busy || state.uncertain || nativeMutationLocked(state)) return;
  const subscription = state.subscription;
  const endpoint = subscription.endpoint;
  if (typeof endpoint !== 'string' || endpoint.length < 1 || endpoint.length > 2048) return;
  state.busy = true;
  state.statusMessage = 'サーバー側の端末紐付けを解除しています。';
  say(state, 'error', ''); say(state, 'status', state.statusMessage); update(state);
  try {
    const csrfToken = await readCsrf(state);
    if (!isCurrent(state)) return;
    await assertCurrentActor(state, state.actorId);
    if (!isCurrent(state)) return;
    state.uncertain = 'unregister';
    const value = await requestJson(state, '/api/core/device-notifications/unregister', {
      method: 'POST', actorId: state.actorId, csrfToken, body: { endpoint }
    });
    if (!isRecord(value) || value.actorId !== state.actorId || value.registered !== false) throw new TypeError('invalid unregister response');
    state.uncertain = null;
    state.registered = false;
    // Only remove the browser subscription after the server has confirmed unbinding.
    let removed;
    const unsubscribeMutation = startNativeMutation(state, 'unsubscribe', () => subscription.unsubscribe());
    try { removed = await boundedPromise(state, unsubscribeMutation.promise, unsubscribeMutation); }
    catch (error) {
      if (!isCurrent(state) || error instanceof StaleOperationError) return;
      const checked = await refreshSubscriptionAfterUnregister(state);
      if (!isCurrent(state) || !checked) return;
      if (error instanceof NativeOperationTimeoutError) {
        state.nativeRecoveryRequired = true;
        state.statusMessage = nativeRecoveryMessage();
        say(state, 'error', ''); say(state, 'status', state.statusMessage); update(state);
        return;
      }
      state.statusMessage = state.subscription
        ? 'サーバー側の端末紐付けは解除済みですが、ブラウザー購読は残っています。明示操作で再試行してください。'
        : 'サーバー側の解除は完了し、このブラウザーに有効な購読はありません。ブラウザーの通知許可は残っています。';
      say(state, 'error', ''); say(state, 'status', state.statusMessage);
      return;
    }
    if (!isCurrent(state)) return;
    if (removed) {
      state.subscription = null;
      state.localStateUnknown = false;
    } else {
      const checked = await refreshSubscriptionAfterUnregister(state);
      if (!isCurrent(state) || !checked) return;
    }
    if (!isCurrent(state)) return;
    state.statusMessage = removed
      ? 'サーバー側の登録とこのブラウザーのPush購読を解除しました。ブラウザーの通知許可自体は残っています。'
      : state.subscription
        ? 'サーバー側の端末紐付けは解除しましたが、ブラウザー購読は残っています。明示操作で再試行してください。'
        : 'サーバー側の解除は完了し、このブラウザーに有効な購読はありません。ブラウザーの通知許可は残っています。';
    say(state, 'error', ''); say(state, 'status', state.statusMessage);
  } catch (error) {
    displayUnexpected(state, state.uncertain ? 'write' : 'unregister', error);
  } finally {
    if (isCurrent(state)) { state.busy = false; update(state); }
  }
}

async function refreshFromClick(state) {
  if (!isCurrent(state) || !state.actorId || state.busy) return;
  state.busy = true;
  state.statusMessage = '状態を手動で再確認しています。';
  say(state, 'error', ''); say(state, 'status', state.statusMessage); update(state);
  try {
    await reconcile(state, { refreshContext: true, manual: true });
  } catch (error) {
    displayUnexpected(state, 'status', error);
  } finally {
    if (isCurrent(state)) { state.busy = false; update(state); }
  }
}

export async function initialize(root, owner) {
  if (!root || typeof root.querySelector !== 'function' || typeof owner !== 'string' || owner.length < 1) throw new TypeError('root and owner required');
  if (liveState?.owner === owner && liveState.root === root && !liveState.disposed) return;
  if (liveState) disposeState(liveState);
  const state = {
    root, owner, generation: ++nextGeneration, disposed: false, controllers: new Set(), nativeCancels: new Set(),
    nodes: findNodes(root), supported: false, configured: false, permission: 'unavailable', actorId: null,
    pushPublicKey: null, registration: null, subscription: null, registered: null, uncertain: null,
    busy: false, localStateUnknown: false, nativeRecoveryRequired: pendingNativeMutations.size > 0 || unreconciledNativeMutations.size > 0,
    statusMessage: 'ログイン状態とこのブラウザーの登録状態を確認しています。', listener: null
  };
  if (Object.values(state.nodes).some(value => !value)) throw new TypeError('notification UI elements missing');
  liveState = state;
  state.listener = event => {
    const target = event.target;
    if (target === node(state, 'register')) void registerFromClick(state);
    else if (target === node(state, 'unregister')) void unregisterFromClick(state);
    else if (target === node(state, 'refresh')) void refreshFromClick(state);
  };
  root.addEventListener('click', state.listener);
  update(state);
  try { await initialLoad(state); }
  catch (error) { displayUnexpected(state, 'initial', error); }
}

function disposeState(state) {
  if (!state || state.disposed) return;
  state.disposed = true;
  if (state.root && state.listener) state.root.removeEventListener('click', state.listener);
  for (const controller of state.controllers) { try { controller.abort(); } catch { } }
  state.controllers.clear();
  for (const cancel of state.nativeCancels) { try { cancel(); } catch { } }
  state.nativeCancels.clear();
  state.actorId = null;
  state.pushPublicKey = null;
  state.subscription = null;
  state.registration = null;
  state.registered = null;
  state.nodes = Object.create(null);
  if (liveState === state) { liveState = null; nextGeneration++; }
}

export function dispose(owner) {
  if (liveState?.owner === owner) disposeState(liveState);
}
