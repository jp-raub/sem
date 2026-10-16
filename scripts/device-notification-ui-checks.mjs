import assert from 'node:assert/strict';
import { readFile } from 'node:fs/promises';
import { pathToFileURL } from 'node:url';
import { resolve } from 'node:path';

const moduleUrl = pathToFileURL(resolve('src/Sem.Web/wwwroot/device-notifications.js')).href;
const ui = await import(moduleUrl);
const actorId = 'actor-test-42';
const endpoint = 'https://push.example/subscription-secret';
const p256dh = 'public-key-material';
const authKey = 'auth-key-material';
const publicKeyBytes = Buffer.from('04' + '6b17d1f2e12c4247f8bce6e563a440f277037d812deb33a0f4a13945d898c296' + '4fe342e2fe1a7f9b8ee7eb4a7c0f9e162bce33576b315ececbb6406837bf51f5', 'hex');
const publicKey = publicKeyBytes.toString('base64url');
const realSetTimeout = globalThis.setTimeout.bind(globalThis);
const realClearTimeout = globalThis.clearTimeout.bind(globalThis);

class FakeRoot {
  constructor() {
    this.isConnected = true;
    this.nodes = new Map(['capability','permission','status','error','register','unregister','refresh'].map(name => [`#device-notifications-${name}`, { id: name, textContent: '', disabled: true }]));
    this.listeners = new Map();
  }
  querySelector(selector) { return this.nodes.get(selector) ?? null; }
  addEventListener(type, listener) { this.listeners.set(type, listener); }
  removeEventListener(type, listener) { if (this.listeners.get(type) === listener) this.listeners.delete(type); }
  click(name) { const node = this.nodes.get(`#device-notifications-${name}`); this.listeners.get('click')?.({ target: node }); }
  text() { return [...this.nodes.values()].map(node => node.textContent).join('\n'); }
}

const wait = ms => new Promise(resolve => setTimeout(resolve, ms));
async function until(predicate, label) {
  for (let attempt = 0; attempt < 250; attempt++) {
    if (predicate()) return;
    await wait(4);
  }
  assert.fail(`timed out waiting for ${label}`);
}
function setGlobal(name, value) {
  Object.defineProperty(globalThis, name, { value, writable: true, configurable: true });
}
function fakeRequestTimeouts() {
  const timers = new Map();
  let nextId = 1;
  setGlobal('setTimeout', (callback, delay, ...args) => {
    if (delay !== 25_000) return realSetTimeout(callback, delay, ...args);
    const id = `fake-request-timeout-${nextId++}`;
    timers.set(id, () => callback(...args));
    return id;
  });
  setGlobal('clearTimeout', id => {
    if (timers.delete(id)) return;
    realClearTimeout(id);
  });
  return {
    get size() { return timers.size; },
    fireOne() {
      const entry = timers.entries().next().value;
      assert.ok(entry, 'expected an active request-timeout timer');
      timers.delete(entry[0]);
      entry[1]();
    }
  };
}
function json(value, status = 200) {
  return new Response(JSON.stringify(value), { status, headers: { 'Content-Type': 'application/json; charset=utf-8' } });
}
function subscription({ unsubscribeResult = true, unsubscribeThrows = false } = {}) {
  return {
    endpoint,
    toJSON: () => ({ endpoint, keys: { p256dh, auth: authKey } }),
    unsubscribe: () => {
      logs.push('browser-unsubscribe');
      if (deferredUnsubscribe) {
        const pending = deferredUnsubscribe;
        return pending.promise.then(result => {
          if (result) localSub = null;
          return result;
        });
      }
      if (unsubscribeThrows) throw new Error('synthetic local unsubscribe failure');
      if (unsubscribeResult) localSub = null;
      return unsubscribeResult;
    }
  };
}
let logs = [];
let localSub = null;
let currentRegistered = true;
let failUnregisterOnce = false;
let permissionRequests = 0;
let swRegistrations = 0;
let subscribeCalls = 0;
let lastSubscription;
let fetchCalls = [];
let delayedGetSubscription = null;
let failGetSubscriptionOnce = false;
let deferredServiceWorkerRegister = null;
let deferredUnsubscribe = null;
let deferredSubscribe = null;
let testRoot;
let ownerCounter = 0;

function setup({ initialSubscription = null, initialPermission = 'granted', statusRegistered = true, hasRegistration = true, publicKeyValue = publicKey } = {}) {
  logs = []; localSub = initialSubscription; currentRegistered = statusRegistered;
  failUnregisterOnce = false; permissionRequests = 0; swRegistrations = 0; subscribeCalls = 0; fetchCalls = [];
  lastSubscription = null; delayedGetSubscription = null; failGetSubscriptionOnce = false; deferredServiceWorkerRegister = null; deferredUnsubscribe = null; deferredSubscribe = null;
  let registrationInstalled = hasRegistration;
  const registration = {
    scope: 'https://app.example/',
    pushManager: {
      getSubscription: async () => {
        logs.push('get-subscription');
        if (failGetSubscriptionOnce) { failGetSubscriptionOnce = false; throw new Error('synthetic getSubscription failure'); }
        if (delayedGetSubscription) { const pending = delayedGetSubscription; delayedGetSubscription = null; return pending; }
        return localSub;
      },
      subscribe: async options => {
        subscribeCalls++; logs.push('browser-subscribe');
        assert.equal(options.userVisibleOnly, true);
        assert.equal(options.applicationServerKey.length, 65);
        if (deferredSubscribe) {
          const pending = deferredSubscribe;
          return pending.promise.then(created => {
            localSub = created;
            lastSubscription = created;
            return created;
          });
        }
        localSub = subscription(); lastSubscription = localSub;
        return localSub;
      }
    }
  };
  const serviceWorker = {
    getRegistration: async scope => { logs.push(`get-registration:${scope}`); return registrationInstalled && registration.scope === 'https://app.example/' ? registration : null; },
    register: async (path, options) => {
      swRegistrations++; logs.push('service-worker-register'); assert.equal(path, '/sem-sw.js'); assert.equal(options.scope, '/');
      if (deferredServiceWorkerRegister) {
        const pending = deferredServiceWorkerRegister;
        return pending.promise.then(installed => {
          registrationInstalled = true;
          logs.push('service-worker-register-settled');
          return installed;
        });
      }
      registrationInstalled = true;
      return registration;
    },
    ready: Promise.resolve(registration)
  };
  setGlobal('location', { href: 'https://app.example/notifications', origin: 'https://app.example', hostname: 'app.example' });
  setGlobal('isSecureContext', true);
  setGlobal('navigator', { serviceWorker });
  setGlobal('PushManager', class PushManager {});
  setGlobal('Notification', {
    permission: initialPermission,
    requestPermission: async () => { permissionRequests++; logs.push('permission-request'); globalThis.Notification.permission = 'granted'; return 'granted'; }
  });
  setGlobal('fetch', async (input, options = {}) => {
    const url = new URL(String(input));
    fetchCalls.push({ url, options });
    assert.equal(url.origin, 'https://app.example');
    assert.equal(options.credentials, 'same-origin');
    assert.equal(options.mode, 'same-origin');
    assert.equal(options.cache, 'no-store');
    assert.equal(options.redirect, 'error');
    if (url.pathname === '/api/core/device-notifications' && options.method === 'GET') return json({ actorId, pushConfigured: true, pushPublicKey: publicKeyValue });
    if (url.pathname === '/api/csrf' && options.method === 'GET') return json({ token: 'csrf-secret-token' });
    if (options.method === 'POST') {
      assert.equal(url.searchParams.getAll('expectedActor').length, 1);
      assert.equal(url.searchParams.get('expectedActor'), actorId);
      assert.equal(options.headers['X-CSRF-TOKEN'], 'csrf-secret-token');
      const body = JSON.parse(options.body);
      if (url.pathname.endsWith('/status')) {
        assert.deepEqual(Object.keys(body), ['endpoint']);
        assert.equal(body.endpoint, endpoint);
        logs.push('server-status');
        return json({ actorId, registered: currentRegistered });
      }
      if (url.pathname.endsWith('/register')) {
        assert.deepEqual(Object.keys(body).sort(), ['auth','endpoint','p256dh']);
        assert.equal(body.endpoint, endpoint); assert.equal(body.p256dh, p256dh); assert.equal(body.auth, authKey);
        logs.push('server-register'); currentRegistered = true;
        return json({ actorId, registered: true });
      }
      if (url.pathname.endsWith('/unregister')) {
        assert.deepEqual(Object.keys(body), ['endpoint']); assert.equal(body.endpoint, endpoint);
        logs.push('server-unregister');
        if (failUnregisterOnce) { failUnregisterOnce = false; throw new TypeError('synthetic network interruption'); }
        currentRegistered = false;
        return json({ actorId, registered: false });
      }
    }
    assert.fail(`unexpected API call: ${options.method} ${url.pathname}`);
  });
  testRoot = new FakeRoot();
  return testRoot;
}
async function start(root = setup()) {
  ownerCounter++;
  const owner = `test-owner-${ownerCounter}`;
  await ui.initialize(root, owner);
  return { root, owner };
}
async function stop(owner) { ui.dispose(owner); }

// Parser bounds and strict UTF-8 behavior.
assert.deepEqual(ui.decodeApplicationServerKey(publicKey), new Uint8Array(publicKeyBytes));
const badPrefixKey = Buffer.from(publicKeyBytes); badPrefixKey[0] = 0x05;
const offCurveKey = Buffer.alloc(65); offCurveKey[0] = 0x04;
assert.throws(() => ui.decodeApplicationServerKey(badPrefixKey.toString('base64url')), /invalid public key/);
assert.throws(() => ui.decodeApplicationServerKey(offCurveKey.toString('base64url')), /invalid public key/);
assert.throws(() => ui.decodeApplicationServerKey('not-a-valid-key'), /invalid public key/);
assert.deepEqual(await ui.readBoundedJson(new Response('{"ok":true}')), { ok: true });
await assert.rejects(ui.readBoundedJson(new Response(new Uint8Array([0xc3, 0x28]))), TypeError);
await assert.rejects(ui.readBoundedJson(new Response(Buffer.alloc(16 * 1024 + 1, 0x20))), RangeError);

// Invalid prefix and off-curve VAPID points fail context validation before permission can be requested.
for (const invalidKey of [badPrefixKey.toString('base64url'), offCurveKey.toString('base64url')]) {
  const root = setup({ initialPermission: 'default', hasRegistration: false, publicKeyValue: invalidKey });
  const { owner } = await start(root);
  assert.equal(permissionRequests, 0);
  assert.equal(swRegistrations, 0);
  assert.equal(subscribeCalls, 0);
  assert.equal(root.nodes.get('#device-notifications-register').disabled, true);
  await stop(owner);
}

// Page entry performs only read operations and does not prompt or register.
{
  const root = setup({ initialSubscription: subscription(), statusRegistered: true });
  const { owner } = await start(root);
  assert.equal(permissionRequests, 0);
  assert.equal(swRegistrations, 0);
  assert.equal(subscribeCalls, 0);
  assert.equal(root.nodes.get('#device-notifications-register').disabled, true);
  assert.equal(root.nodes.get('#device-notifications-unregister').disabled, false);
  assert.match(root.nodes.get('#device-notifications-status').textContent, /紐付いてPush登録/);
  assert.ok(fetchCalls.some(call => call.url.pathname.endsWith('/status')));
  assert.equal(root.text().includes(endpoint), false);
  assert.equal(root.text().includes(p256dh), false);
  assert.equal(root.text().includes('csrf-secret-token'), false);
  const statusCall = fetchCalls.find(call => call.url.pathname.endsWith('/status'));
  assert.deepEqual(Object.keys(JSON.parse(statusCall.options.body)), ['endpoint']);
  await stop(owner);
}

// Explicit registration prompts only on click and posts the exact current subscription once.
{
  const root = setup({ initialSubscription: null, initialPermission: 'default', hasRegistration: false });
  const { owner } = await start(root);
  assert.equal(permissionRequests, 0);
  assert.equal(swRegistrations, 0);
  assert.equal(subscribeCalls, 0);
  assert.equal(root.nodes.get('#device-notifications-register').disabled, false);
  root.click('register');
  await until(() => logs.includes('server-register'), 'register POST');
  await until(() => root.nodes.get('#device-notifications-status').textContent.includes('受け付けました'), 'register completion');
  assert.equal(permissionRequests, 1);
  assert.equal(swRegistrations, 1);
  assert.equal(subscribeCalls, 1);
  const registerCall = fetchCalls.find(call => call.url.pathname.endsWith('/register'));
  assert.equal(registerCall.url.searchParams.getAll('expectedActor').length, 1);
  assert.deepEqual(Object.keys(JSON.parse(registerCall.options.body)).sort(), ['auth','endpoint','p256dh']);
  assert.equal(root.text().includes(endpoint), false);
  assert.equal(root.text().includes(p256dh), false);
  await stop(owner);
}

// Unknown unregister outcomes lock writes until an explicit read-only status refresh.
{
  const root = setup({ initialSubscription: subscription(), statusRegistered: true });
  const { owner } = await start(root);
  failUnregisterOnce = true;
  root.click('unregister');
  await until(() => root.nodes.get('#device-notifications-status').textContent.includes('操作結果を確認できません'), 'unknown-write lock');
  assert.equal(fetchCalls.filter(call => call.url.pathname.endsWith('/unregister')).length, 1);
  assert.equal(root.nodes.get('#device-notifications-unregister').disabled, true);
  assert.equal(root.nodes.get('#device-notifications-refresh').disabled, false);
  assert.equal(logs.includes('browser-unsubscribe'), false);
  currentRegistered = false;
  root.click('refresh');
  await until(() => root.nodes.get('#device-notifications-status').textContent.includes('サーバー登録は確認できません'), 'manual reconciliation');
  assert.equal(fetchCalls.filter(call => call.url.pathname.endsWith('/unregister')).length, 1);
  assert.equal(root.nodes.get('#device-notifications-unregister').disabled, false);
  assert.equal(logs.includes('browser-unsubscribe'), false);
  // A new explicit click may now perform the server-first/local-second unregister.
  root.click('unregister');
  await until(() => logs.includes('browser-unsubscribe'), 'local unsubscribe after server confirmation');
  assert.ok(logs.indexOf('server-unregister') < logs.lastIndexOf('browser-unsubscribe'));
  assert.equal(root.nodes.get('#device-notifications-status').textContent.includes('ブラウザーのPush購読を解除'), true);
  await stop(owner);
}

// A late getSubscription result after unsubscribe reconciliation cannot update a replaced page.
for (const mode of ['unsubscribe-false', 'unsubscribe-throws']) {
  const originalSubscription = subscription({ unsubscribeResult: false, unsubscribeThrows: mode === 'unsubscribe-throws' });
  const root = setup({ initialSubscription: originalSubscription, statusRegistered: true });
  const { owner } = await start(root);
  const oldStatus = root.nodes.get('#device-notifications-status');
  let resolveDelayedRead;
  delayedGetSubscription = new Promise(resolve => { resolveDelayedRead = resolve; });
  const readsBefore = logs.filter(item => item === 'get-subscription').length;
  root.click('unregister');
  await until(() => logs.includes('server-unregister'), `${mode} server unbind`);
  await until(() => logs.filter(item => item === 'get-subscription').length > readsBefore, `${mode} delayed browser read`);
  const frozenOldText = oldStatus.textContent;

  // Replacing the active owner disposes the old root before the native read resolves.
  const replacement = new FakeRoot();
  ownerCounter++;
  const replacementOwner = `replacement-owner-${ownerCounter}`;
  await ui.initialize(replacement, replacementOwner);
  const replacementText = replacement.nodes.get('#device-notifications-status').textContent;
  resolveDelayedRead(subscription());
  await wait(0);
  assert.equal(oldStatus.textContent, frozenOldText);
  assert.equal(replacement.nodes.get('#device-notifications-status').textContent, replacementText);
  assert.equal(fetchCalls.filter(call => call.url.pathname.endsWith('/unregister')).length, 1);
  assert.equal(fetchCalls.filter(call => call.url.pathname.endsWith('/register')).length, 0);
  ui.dispose(replacementOwner);
}

// Confirmed server unbind remains distinct from a failed local unsubscribe plus failed local-state read.
{
  const root = setup({ initialSubscription: subscription({ unsubscribeThrows: true }), statusRegistered: true });
  const { owner } = await start(root);
  failGetSubscriptionOnce = true;
  root.click('unregister');
  await until(() => root.nodes.get('#device-notifications-status').textContent.includes('購読状態は未確認'), 'server-unbound/local-state-unknown message');
  assert.equal(fetchCalls.filter(call => call.url.pathname.endsWith('/unregister')).length, 1);
  assert.equal(root.nodes.get('#device-notifications-unregister').disabled, true);
  assert.equal(root.nodes.get('#device-notifications-register').disabled, true);
  assert.equal(root.nodes.get('#device-notifications-refresh').disabled, false);
  assert.equal(root.nodes.get('#device-notifications-status').textContent.includes('操作結果を確認できません'), false);
  root.click('refresh');
  await until(() => root.nodes.get('#device-notifications-status').textContent.includes('サーバー登録は確認できません'), 'manual local-state reconciliation');
  assert.equal(fetchCalls.filter(call => call.url.pathname.endsWith('/unregister')).length, 1);
  assert.equal(root.nodes.get('#device-notifications-unregister').disabled, false);
  await stop(owner);
}

// A timed-out native unsubscribe remains locked through owner replacement until a post-settlement manual read.
{
  const root = setup({ initialSubscription: subscription(), statusRegistered: true });
  const { owner } = await start(root);
  let resolveUnsubscribe;
  deferredUnsubscribe = { promise: new Promise(resolve => { resolveUnsubscribe = resolve; }) };
  const fakeTimers = fakeRequestTimeouts();
  let replacementOwner = null;
  try {
    root.click('unregister');
    await until(() => logs.includes('browser-unsubscribe'), 'native unsubscribe start');
    assert.equal(fakeTimers.size, 1);
    fakeTimers.fireOne();
    await until(() => root.nodes.get('#device-notifications-status').textContent.includes('ブラウザーの購読解除'), 'timeout recovery lock');
    await until(() => root.nodes.get('#device-notifications-refresh').disabled === false, 'timeout handler completion');
    assert.equal(root.nodes.get('#device-notifications-register').disabled, true);
    assert.equal(root.nodes.get('#device-notifications-unregister').disabled, true);

    const statusReadsBeforeRefresh = logs.filter(item => item === 'server-status').length;
    root.click('refresh');
    await until(() => logs.filter(item => item === 'server-status').length > statusReadsBeforeRefresh, 'manual read while native unsubscribe remains pending');
    assert.equal(root.nodes.get('#device-notifications-register').disabled, true);
    assert.equal(root.nodes.get('#device-notifications-unregister').disabled, true);
    assert.equal(localSub !== null, true, 'the pending unsubscribe must not be assumed complete');
    root.click('register');
    await wait(0);
    assert.equal(subscribeCalls, 0);
    assert.equal(fetchCalls.filter(call => call.url.pathname.endsWith('/register')).length, 0);

    const oldText = root.nodes.get('#device-notifications-status').textContent;
    const replacement = new FakeRoot();
    ownerCounter++;
    replacementOwner = `pending-native-owner-${ownerCounter}`;
    await ui.initialize(replacement, replacementOwner);
    assert.equal(replacement.nodes.get('#device-notifications-register').disabled, true);
    assert.equal(replacement.nodes.get('#device-notifications-unregister').disabled, true);
    replacement.click('register');
    await wait(0);
    assert.equal(subscribeCalls, 0);
    assert.equal(fetchCalls.filter(call => call.url.pathname.endsWith('/register')).length, 0);

    resolveUnsubscribe(true);
    await until(() => replacement.nodes.get('#device-notifications-status').textContent.includes('ブラウザーの処理は終了しました'), 'late native settlement on replacement owner');
    assert.equal(root.nodes.get('#device-notifications-status').textContent, oldText);
    assert.equal(replacement.nodes.get('#device-notifications-register').disabled, true);
    assert.equal(replacement.nodes.get('#device-notifications-unregister').disabled, true);

    const localReadsBeforeReconcile = logs.filter(item => item === 'get-subscription').length;
    replacement.click('refresh');
    await until(() => logs.filter(item => item === 'get-subscription').length > localReadsBeforeReconcile
      && replacement.nodes.get('#device-notifications-register').disabled === false, 'manual read after native settlement');
    assert.equal(localSub, null);
    assert.equal(fetchCalls.filter(call => call.url.pathname.endsWith('/unregister')).length, 1);
    assert.equal(fetchCalls.filter(call => call.url.pathname.endsWith('/register')).length, 0);
    await stop(replacementOwner);
    replacementOwner = null;
  } finally {
    setGlobal('setTimeout', realSetTimeout);
    setGlobal('clearTimeout', realClearTimeout);
    deferredUnsubscribe = null;
    if (replacementOwner) await stop(replacementOwner);
    else await stop(owner);
  }
}

// A timed-out native subscribe stays locked through manual refresh until late settlement and reconciliation.
{
  const root = setup({ initialSubscription: null, initialPermission: 'granted', statusRegistered: false, hasRegistration: true });
  const { owner } = await start(root);
  let resolveSubscribe;
  deferredSubscribe = { promise: new Promise(resolve => { resolveSubscribe = resolve; }) };
  const fakeTimers = fakeRequestTimeouts();
  try {
    assert.equal(root.nodes.get('#device-notifications-register').disabled, false);
    root.click('register');
    await until(() => logs.includes('browser-subscribe'), 'native subscribe start');
    assert.equal(subscribeCalls, 1, 'the test must execute PushManager.subscribe');
    assert.equal(fakeTimers.size, 1, 'the deferred native subscribe must be guarded by the 25-second timeout');

    fakeTimers.fireOne();
    await until(() => root.nodes.get('#device-notifications-status').textContent.includes('ブラウザーの購読作成はまだ完了していません'), 'subscribe timeout lock');
    await until(() => root.nodes.get('#device-notifications-refresh').disabled === false, 'manual refresh enabled after timeout');
    assert.equal(root.nodes.get('#device-notifications-register').disabled, true);
    assert.equal(root.nodes.get('#device-notifications-unregister').disabled, true);

    const localReadsBeforeRefresh = logs.filter(item => item === 'get-subscription').length;
    root.click('refresh');
    await until(() => logs.filter(item => item === 'get-subscription').length > localReadsBeforeRefresh
      && root.nodes.get('#device-notifications-status').textContent.includes('このブラウザーに有効なPush登録がありません'), 'read-only refresh while native subscribe remains pending');
    assert.equal(root.nodes.get('#device-notifications-register').disabled, true);
    assert.equal(root.nodes.get('#device-notifications-unregister').disabled, true);
    assert.equal(localSub, null, 'refresh must not assume the pending subscription completed');
    assert.equal(fetchCalls.filter(call => call.url.pathname.endsWith('/register')).length, 0);

    root.click('register');
    await wait(0);
    assert.equal(subscribeCalls, 1, 'registration stays locked while the native subscribe Promise is pending');
    assert.equal(fetchCalls.filter(call => call.url.pathname.endsWith('/register')).length, 0);

    resolveSubscribe(subscription());
    await until(() => localSub !== null
      && root.nodes.get('#device-notifications-status').textContent.includes('ブラウザーの処理は終了しました'), 'late native subscribe settlement');
    assert.equal(root.nodes.get('#device-notifications-register').disabled, true, 'late settlement still requires manual reconciliation');

    const serverStatusBeforeReconcile = fetchCalls.filter(call => call.url.pathname.endsWith('/status')).length;
    root.click('refresh');
    await until(() => fetchCalls.filter(call => call.url.pathname.endsWith('/status')).length > serverStatusBeforeReconcile
      && root.nodes.get('#device-notifications-register').disabled === false, 'manual reconciliation after native subscribe settlement');
    assert.equal(localSub, lastSubscription);
    assert.equal(currentRegistered, false);
    assert.equal(fetchCalls.filter(call => call.url.pathname.endsWith('/register')).length, 0, 'manual reconciliation must not register automatically');
    assert.equal(subscribeCalls, 1);
    await stop(owner);
  } finally {
    setGlobal('setTimeout', realSetTimeout);
    setGlobal('clearTimeout', realClearTimeout);
    deferredSubscribe = null;
    await stop(owner);
  }
}

// A timed-out ServiceWorker.register Promise stays locked across refresh and owner replacement until manual reconciliation.
{
  const root = setup({ initialSubscription: null, initialPermission: 'granted', statusRegistered: false, hasRegistration: false });
  const { owner } = await start(root);
  let resolveWorkerRegister;
  let workerRegisterSettled = false;
  deferredServiceWorkerRegister = {
    promise: new Promise(resolve => {
      resolveWorkerRegister = value => { workerRegisterSettled = true; resolve(value); };
    })
  };
  const fakeTimers = fakeRequestTimeouts();
  let replacementOwner = null;
  try {
    assert.equal(root.nodes.get('#device-notifications-register').disabled, false);
    root.click('register');
    await until(() => logs.includes('service-worker-register'), 'explicit ServiceWorker.register start');
    assert.equal(swRegistrations, 1, 'the test must invoke ServiceWorker.register');
    await until(() => fakeTimers.size === 1, 'service-worker registration timeout installed');
    fakeTimers.fireOne();
    await until(() => root.nodes.get('#device-notifications-status').textContent.includes('Service Worker登録はまだ完了していません')
      && root.nodes.get('#device-notifications-refresh').disabled === false, 'service-worker registration timeout lock');
    assert.equal(root.nodes.get('#device-notifications-register').disabled, true);
    assert.equal(root.nodes.get('#device-notifications-unregister').disabled, true);
    assert.equal(fetchCalls.filter(call => call.options.method === 'POST').length, 0);

    const readsBeforeRefresh = logs.filter(item => item === 'get-registration:/').length;
    root.click('refresh');
    await until(() => logs.filter(item => item === 'get-registration:/').length > readsBeforeRefresh
      && root.nodes.get('#device-notifications-refresh').disabled === false, 'read-only refresh while service-worker registration is pending');
    assert.equal(root.nodes.get('#device-notifications-register').disabled, true);
    assert.equal(root.nodes.get('#device-notifications-unregister').disabled, true);
    assert.equal(swRegistrations, 1);
    assert.equal(subscribeCalls, 0);
    assert.equal(fetchCalls.filter(call => call.options.method === 'POST').length, 0);

    const replacement = new FakeRoot();
    ownerCounter++;
    replacementOwner = `pending-worker-owner-${ownerCounter}`;
    await ui.initialize(replacement, replacementOwner);
    assert.equal(replacement.nodes.get('#device-notifications-register').disabled, true);
    assert.equal(replacement.nodes.get('#device-notifications-unregister').disabled, true);
    replacement.click('register');
    await wait(0);
    assert.equal(swRegistrations, 1, 'owner replacement must not repeat ServiceWorker.register');
    assert.equal(subscribeCalls, 0);
    assert.equal(fetchCalls.filter(call => call.options.method === 'POST').length, 0);

    resolveWorkerRegister({ scope: 'https://app.example/' });
    await until(() => logs.includes('service-worker-register-settled')
      && replacement.nodes.get('#device-notifications-status').textContent.includes('ブラウザーの処理は終了しました'), 'late service-worker registration settlement');
    assert.equal(replacement.nodes.get('#device-notifications-register').disabled, true, 'settlement alone must not unlock registration');
    assert.equal(subscribeCalls, 0, 'late registration must not continue into PushManager.subscribe');

    const readsBeforeReconcile = logs.filter(item => item === 'get-registration:/').length;
    replacement.click('refresh');
    await until(() => logs.filter(item => item === 'get-registration:/').length > readsBeforeReconcile
      && replacement.nodes.get('#device-notifications-register').disabled === false, 'manual reconciliation after service-worker settlement');
    assert.equal(swRegistrations, 1);
    assert.equal(subscribeCalls, 0);
    assert.equal(fetchCalls.filter(call => call.options.method === 'POST').length, 0, 'read-only reconciliation must not bind or register');
  } finally {
    if (resolveWorkerRegister && !workerRegisterSettled) {
      resolveWorkerRegister({ scope: 'https://app.example/' });
      await wait(0);
    }
    setGlobal('setTimeout', realSetTimeout);
    setGlobal('clearTimeout', realClearTimeout);
    deferredServiceWorkerRegister = null;
    await stop(replacementOwner ?? owner);
  }
}

// Authentication rejection clears actor-bound actions rather than retaining stale state.
{
  const root = setup({ initialSubscription: subscription(), statusRegistered: true });
  const originalFetch = globalThis.fetch;
  setGlobal('fetch', async (input, options = {}) => {
    const url = new URL(String(input));
    if (url.pathname.endsWith('/status')) return json({ error: 'denied' }, 403);
    return originalFetch(input, options);
  });
  const { owner } = await start(root);
  assert.equal(root.nodes.get('#device-notifications-register').disabled, true);
  assert.equal(root.nodes.get('#device-notifications-unregister').disabled, true);
  assert.match(root.nodes.get('#device-notifications-status').textContent, /ログイン状態またはアカウントが変わりました/);
  await stop(owner);
}

// Disposal aborts pending HTTP work, removes listeners, and prevents stale DOM writes.
{
  const root = setup();
  let contextStarted = false;
  const ordinaryFetch = globalThis.fetch;
  setGlobal('fetch', (input, options = {}) => {
    const url = new URL(String(input));
    if (url.pathname === '/api/core/device-notifications') {
      contextStarted = true;
      return new Promise((resolve, reject) => {
        options.signal.addEventListener('abort', () => reject(new DOMException('aborted', 'AbortError')), { once: true });
      });
    }
    return ordinaryFetch(input, options);
  });
  ownerCounter++;
  const owner = `dispose-owner-${ownerCounter}`;
  const pending = ui.initialize(root, owner);
  await until(() => contextStarted, 'pending context fetch');
  const oldText = root.nodes.get('#device-notifications-status').textContent;
  ui.dispose(owner);
  await pending;
  assert.equal(root.listeners.has('click'), false);
  assert.equal(root.nodes.get('#device-notifications-status').textContent, oldText);
}

// Razor/navigation/docs integration guardrails and required full-document route.
const razor = await readFile('src/Sem.Web/Components/Pages/DeviceNotifications.razor', 'utf8');
const navigation = await readFile('src/Sem.Web/Components/Layout/ServiceNavigation.razor', 'utf8');
const markdown = await readFile('deploy/DEVICE-NOTIFICATIONS-UI.md', 'utf8');
const japanese = await readFile('deploy/DEVICE-NOTIFICATIONS-UI.ja.md', 'utf8');
assert.match(razor, /@page\s+"\/notifications"/);
assert.match(razor, /InteractiveServerRenderMode\(prerender: false\)/);
assert.match(razor, /Microsoft\.AspNetCore\.Authorization\.Authorize/);
assert.match(razor, /device-notifications-module-error/);
assert.match(navigation, /id="navigation-notifications-link" href="\/notifications" data-enhance-nav="false"/);
assert.match(markdown, /no test-push button/i);
assert.match(japanese, /テスト通知ボタンはありません/);

console.log('device-notification-ui-checks: PASS');
