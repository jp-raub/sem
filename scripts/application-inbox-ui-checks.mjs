import assert from 'node:assert/strict';
import { readFile } from 'node:fs/promises';
import { pathToFileURL } from 'node:url';
import { resolve } from 'node:path';

const ui = await import(pathToFileURL(resolve('src/Sem.Web/wwwroot/inbox.js')).href);
const razor = await readFile(resolve('src/Sem.Web/Components/Pages/Inbox.razor'), 'utf8');
const navigation = await readFile(resolve('src/Sem.Web/Components/Layout/ServiceNavigation.razor'), 'utf8');
const docs = await readFile(resolve('deploy/APPLICATION-INBOX-UI.md'), 'utf8');
assert.match(razor, /@page\s+"\/inbox"/);
assert.match(razor, /InteractiveServerRenderMode\(prerender: false\)/);
assert.match(razor, /Microsoft\.AspNetCore\.Authorization\.Authorize/);
for (const id of ['inbox-refresh', 'inbox-previous', 'inbox-next', 'inbox-detail-refresh', 'inbox-mark-read']) {
  assert.match(razor, new RegExp(`<button id="${id}"[^>]*disabled`));
}
assert.match(razor, /inbox-module-error" role="alert"/);
assert.match(navigation, /id="navigation-inbox" href="\/inbox"/);
assert.doesNotMatch(navigation, /id="navigation-inbox" href="\/\?messages=1"/);
assert.match(docs, /inbox-mark-read/);

const actorId = 'inbox-actor-1';
const csrfToken = 'csrf-token-test';
const unreadId = 'message-unread';
const readId = 'message-read';
const bodyText = '<script>not executable</script>\n<a href="https://example.invalid">plain text only</a>';
const pageLimitNotice = 'APIの最大ページ数10000に達し、残りのメッセージはこの画面から取得できません。';
assert.deepEqual(ui.inboxPaginationState(1, true), { canPrevious: false, canNext: true, limitNotice: '' });
assert.deepEqual(ui.inboxPaginationState(9999, true), { canPrevious: true, canNext: true, limitNotice: '' });
assert.deepEqual(ui.inboxPaginationState(10000, true), { canPrevious: true, canNext: false, limitNotice: pageLimitNotice });
assert.deepEqual(ui.inboxPaginationState(10000, false), { canPrevious: true, canNext: false, limitNotice: '' });
assert.deepEqual(ui.inboxPaginationState(10001, true), { canPrevious: false, canNext: false, limitNotice: '' });

class FakeElement {
  constructor(tag, ownerDocument = null) {
    this.tagName = tag.toUpperCase();
    this.ownerDocument = ownerDocument;
    this.children = [];
    this.attributes = new Map();
    this.listeners = new Map();
    this._text = '';
    this.disabled = true;
    this.hidden = false;
    this.isConnected = true;
  }
  get textContent() { return this._text + this.children.map(child => child.textContent).join(''); }
  set textContent(value) { this._text = String(value ?? ''); this.children = []; }
  get innerHTML() { return undefined; }
  set innerHTML(_value) { throw new Error('innerHTML must not be used'); }
  append(...nodes) { this.children.push(...nodes); }
  replaceChildren(...nodes) { this._text = ''; this.children = [...nodes]; }
  setAttribute(name, value) { this.attributes.set(name, String(value)); }
  addEventListener(type, listener) {
    if (!this.listeners.has(type)) this.listeners.set(type, new Set());
    this.listeners.get(type).add(listener);
  }
  removeEventListener(type, listener) { this.listeners.get(type)?.delete(listener); }
  allText() { return this.textContent; }
}

class FakeRoot extends FakeElement {
  constructor() {
    const document = { createElement: tag => new FakeElement(tag, document) };
    super('main', document);
    this.nodes = new Map();
    const ids = [
      'status', 'error', 'refresh', 'list-body', 'previous', 'page', 'next', 'unknown', 'detail',
      'detail-status', 'detail-error', 'detail-title', 'detail-created', 'detail-read-state', 'detail-body',
      'detail-refresh', 'mark-read'
    ];
    for (const id of ids) this.nodes.set(`#inbox-${id}`, new FakeElement(id.includes('body') ? 'tbody' : 'div', document));
  }
  querySelector(selector) { return this.nodes.get(selector) ?? null; }
  click(idOrNode) {
    const target = typeof idOrNode === 'string' ? this.nodes.get(`#inbox-${idOrNode}`) : idOrNode;
    if (target?.disabled) return;
    const event = { target, defaultPrevented: false, preventDefault() { this.defaultPrevented = true; } };
    for (const listener of this.listeners.get('click') ?? []) listener(event);
  }
  emitLifecycle(type) { for (const listener of lifecycle.get(type) ?? []) listener({ persisted: true }); }
}

function setGlobal(name, value) {
  Object.defineProperty(globalThis, name, { value, writable: true, configurable: true });
}
function json(value, status = 200) {
  return new Response(JSON.stringify(value), { status, headers: { 'Content-Type': 'application/json; charset=utf-8' } });
}
async function until(predicate, label) {
  for (let index = 0; index < 250; index++) {
    if (predicate()) return;
    await new Promise(resolve => setTimeout(resolve, 4));
  }
  assert.fail(`timed out waiting for ${label}`);
}

let lifecycle = new Map();
let currentList;
let detailRecords;
let mutationMode;
let readCalls;
let fetchCalls;
let pendingRead;
let ownerCounter = 0;
function setup({ mode = 'success' } = {}) {
  lifecycle = new Map();
  setGlobal('addEventListener', (type, listener) => {
    if (!lifecycle.has(type)) lifecycle.set(type, new Set());
    lifecycle.get(type).add(listener);
  });
  setGlobal('removeEventListener', (type, listener) => lifecycle.get(type)?.delete(listener));
  setGlobal('location', { href: 'https://app.example/inbox', origin: 'https://app.example' });
  readCalls = [];
  fetchCalls = [];
  pendingRead = null;
  mutationMode = mode;
  currentList = {
    actorId, page: 1, pageSize: 50, hasMore: true,
    messages: [
      { id: unreadId, title: '未読の件名', created: 1_700_000_000, readAt: null },
      { id: readId, title: '<img src=x onerror=alert(1)>', created: 1_700_000_100, readAt: 1_700_000_200 }
    ]
  };
  detailRecords = new Map([
    [unreadId, { actorId, id: unreadId, title: '未読の件名', body: bodyText, created: 1_700_000_000, readAt: null }],
    [readId, { actorId, id: readId, title: '<img src=x onerror=alert(1)>', body: 'Already read', created: 1_700_000_100, readAt: 1_700_000_200 }]
  ]);
  setGlobal('fetch', async (input, options = {}) => {
    const url = new URL(String(input));
    fetchCalls.push({ url, options });
    assert.equal(url.origin, 'https://app.example');
    assert.equal(options.credentials, 'same-origin');
    assert.equal(options.mode, 'same-origin');
    assert.equal(options.cache, 'no-store');
    assert.equal(options.redirect, 'error');
    if (url.pathname === '/api/core/inbox' && options.method === 'GET') {
      assert.deepEqual([...url.searchParams.keys()], ['page']);
      const page = Number(url.searchParams.get('page'));
      return json({ ...currentList, page });
    }
    if (url.pathname === '/api/core/inbox/message' && options.method === 'GET') {
      assert.equal(url.searchParams.getAll('expectedActor').length, 1);
      assert.equal(url.searchParams.get('expectedActor'), actorId);
      assert.equal(url.searchParams.getAll('id').length, 1);
      const message = detailRecords.get(url.searchParams.get('id'));
      return message ? json(message) : json({ error: 'missing' }, 404);
    }
    if (url.pathname === '/api/csrf' && options.method === 'GET') return json({ token: csrfToken });
    if (url.pathname === '/api/core/inbox/read' && options.method === 'POST') {
      assert.equal(url.searchParams.getAll('expectedActor').length, 1);
      assert.equal(url.searchParams.get('expectedActor'), actorId);
      assert.equal(options.headers['X-CSRF-TOKEN'], csrfToken);
      const body = JSON.parse(options.body);
      assert.deepEqual(Object.keys(body), ['id']);
      assert.equal(Buffer.byteLength(options.body, 'utf8') <= 4096, true);
      readCalls.push(body);
      const existing = detailRecords.get(body.id);
      if (!existing) return json({ error: 'missing' }, 404);
      const readAt = 1_700_000_300;
      if (mutationMode === 'unknown-unread') throw new TypeError('synthetic request outcome unknown before application');
      detailRecords.set(body.id, { ...existing, readAt });
      if (mutationMode === 'unknown') throw new TypeError('synthetic response lost after server applied the change');
      currentList = { ...currentList, messages: currentList.messages.map(message => message.id === body.id ? { ...message, readAt } : message) };
      if (mutationMode === 'deferred') {
        // Deliberately ignore abort so late success/failure also exercises the operation guard.
        return new Promise((resolve, reject) => {
          pendingRead = {
            signal: options.signal,
            resolve: () => resolve(json({ actorId, id: body.id, readAt })),
            reject: () => reject(new TypeError('synthetic late transport failure'))
          };
        });
      }
      return json({ actorId, id: body.id, readAt });
    }
    assert.fail(`unexpected request ${options.method} ${url.pathname}`);
  });
  return new FakeRoot();
}
async function start(root) {
  const owner = `inbox-owner-${++ownerCounter}`;
  await ui.initialize(root, owner);
  return { root, owner };
}
function stop(owner) { ui.dispose(owner); }

// Initial list is read-only; text is rendered as text, and opening the message does not mark it read.
{
  const root = setup();
  const { owner } = await start(root);
  assert.equal(readCalls.length, 0);
  assert.equal(root.nodes.get('#inbox-list-body').children.length, 2);
  assert.equal(root.nodes.get('#inbox-list-body').children[1].textContent.includes('<img src=x onerror=alert(1)>'), true);
  const unreadRow = root.nodes.get('#inbox-list-body').children[0];
  const openButton = unreadRow.children[4].children[0];
  root.click(openButton);
  await until(() => root.nodes.get('#inbox-detail-body').textContent === bodyText, 'read-only message detail');
  assert.equal(root.nodes.get('#inbox-detail-body').innerHTML, undefined);
  assert.equal(root.nodes.get('#inbox-detail-body').textContent.includes('<script>'), true);
  assert.equal(readCalls.length, 0, 'opening a message must not mark it read');
  assert.equal(fetchCalls.filter(call => call.url.pathname === '/api/core/inbox/message').length, 1);
  assert.equal(fetchCalls.some(call => call.url.href.includes('csrf-token-test')), false);
  assert.equal(root.nodes.get('#inbox-mark-read').disabled, false);
  root.click('mark-read');
  await until(() => root.nodes.get('#inbox-detail-read-state').textContent === '既読', 'explicit mark-read action');
  assert.deepEqual(readCalls, [{ id: unreadId }]);
  assert.equal(root.nodes.get('#inbox-list-body').children[0].children[3].textContent, '既読');
  assert.match(root.nodes.get('#inbox-status').textContent, /未読数は古い場合/);
  stop(owner);
  assert.equal(lifecycle.get('pagehide')?.size ?? 0, 0);
  assert.equal(lifecycle.get('pageshow')?.size ?? 0, 0);
}

// Unknown write is never replayed; only an explicit read-only detail refresh can confirm it.
{
  const root = setup({ mode: 'unknown-unread' });
  const { owner } = await start(root);
  root.click(root.nodes.get('#inbox-list-body').children[0].children[4].children[0]);
  await until(() => root.nodes.get('#inbox-detail-body').textContent === bodyText, 'detail before uncertain read');
  root.click('mark-read');
  await until(() => !root.nodes.get('#inbox-unknown').hidden, 'unknown read outcome');
  assert.equal(readCalls.length, 1);
  assert.equal(root.nodes.get('#inbox-mark-read').disabled, true);
  root.click('detail-refresh');
  await until(() => root.nodes.get('#inbox-detail-read-state').textContent === '未読', 'unread detail reconciliation');
  assert.equal(root.nodes.get('#inbox-unknown').hidden, false, 'an unread detail must keep the uncertain write locked');
  assert.equal(root.nodes.get('#inbox-mark-read').disabled, true);
  detailRecords.set(unreadId, { ...detailRecords.get(unreadId), readAt: 1_700_000_300 });
  root.click('detail-refresh');
  await until(() => root.nodes.get('#inbox-unknown').hidden && root.nodes.get('#inbox-detail-read-state').textContent === '既読', 'manual detail reconciliation');
  assert.equal(readCalls.length, 1, 'manual detail refresh must not replay the POST');
  stop(owner);
}

// A committed read with a lost response remains locked until explicit GET reconciliation.
{
  const root = setup({ mode: 'unknown' });
  const { owner } = await start(root);
  root.click(root.nodes.get('#inbox-list-body').children[0].children[4].children[0]);
  await until(() => root.nodes.get('#inbox-detail-body').textContent === bodyText, 'detail before committed response loss');
  const otherReadAt = detailRecords.get(readId).readAt;
  root.click('mark-read');
  await until(() => !root.nodes.get('#inbox-unknown').hidden && !root.nodes.get('#inbox-detail-refresh').disabled,
    'committed response loss locked');
  const committedReadAt = detailRecords.get(unreadId).readAt;
  assert.equal(committedReadAt, 1_700_000_300, 'server applied the read before response loss');
  assert.equal(root.nodes.get('#inbox-detail-read-state').textContent, '未読', 'UI cannot assume a lost response succeeded');
  assert.equal(root.nodes.get('#inbox-mark-read').disabled, true);
  assert.equal(readCalls.length, 1);
  const beforeReconcile = fetchCalls.length;
  root.click('mark-read');
  assert.equal(readCalls.length, 1, 'locked button cannot resend');
  root.click('detail-refresh');
  await until(() => root.nodes.get('#inbox-unknown').hidden
    && root.nodes.get('#inbox-detail-read-state').textContent === '既読'
    && !root.nodes.get('#inbox-detail-refresh').disabled, 'committed read reconciled by explicit GET');
  assert.deepEqual(fetchCalls.slice(beforeReconcile).map(call => [call.options.method, call.url.pathname]),
    [['GET', '/api/core/inbox/message']], 'reconciliation makes only one read-only request');
  root.click('detail-refresh');
  await until(() => !root.nodes.get('#inbox-detail-refresh').disabled, 'repeat read-only reconciliation');
  assert.equal(detailRecords.get(unreadId).readAt, committedReadAt, 'original committed timestamp preserved');
  assert.equal(detailRecords.get(readId).readAt, otherReadAt, 'other message timestamp preserved');
  assert.equal(readCalls.length, 1, 'no automatic or manual replay');
  stop(owner);
}

// Pagehide aborts a pending mutation; late success AND failure cannot restore the old view.
for (const completion of ['resolve', 'reject']) {
  const root = setup({ mode: 'deferred' });
  const { owner } = await start(root);
  root.click(root.nodes.get('#inbox-list-body').children[0].children[4].children[0]);
  await until(() => root.nodes.get('#inbox-detail-body').textContent === bodyText, 'detail before pending mutation');
  root.click('mark-read');
  await until(() => pendingRead !== null, 'read POST in flight');
  const pending = pendingRead;
  assert.equal(pending.signal.aborted, false);
  assert.equal(readCalls.length, 1);
  const requestsBeforeHide = fetchCalls.length;
  root.emitLifecycle('pagehide');
  assert.equal(pending.signal.aborted, true, 'pagehide aborts the actual POST signal');
  assert.equal(root.nodes.get('#inbox-detail-body').textContent, '');
  assert.equal(root.nodes.get('#inbox-detail-title').textContent, '');
  assert.equal(root.nodes.get('#inbox-detail').hidden, true);
  assert.equal(root.nodes.get('#inbox-list-body').textContent.includes('未読の件名'), false);
  assert.equal(root.nodes.get('#inbox-mark-read').disabled, true);
  root.emitLifecycle('pageshow');
  await until(() => root.nodes.get('#inbox-list-body').children.length === 2
    && !root.nodes.get('#inbox-refresh').disabled, 'read-only first-page restore');
  assert.deepEqual(fetchCalls.slice(requestsBeforeHide).map(call => [call.options.method, call.url.pathname]),
    [['GET', '/api/core/inbox']], 'restoration never replays the POST');
  assert.equal(root.nodes.get('#inbox-unknown').hidden, false, 'GET list alone cannot resolve a pending write');
  const restoredStatus = root.nodes.get('#inbox-status').textContent;
  pending[completion]();
  await new Promise(resolve => setTimeout(resolve, 20));
  assert.equal(root.nodes.get('#inbox-detail-body').textContent, '', 'late operation cannot refill message body');
  assert.equal(root.nodes.get('#inbox-detail').hidden, true);
  assert.equal(root.nodes.get('#inbox-status').textContent, restoredStatus, 'late catch/finally cannot change restored state');
  assert.equal(root.nodes.get('#inbox-unknown').hidden, false);
  assert.equal(root.nodes.get('#inbox-mark-read').disabled, true);
  assert.equal(readCalls.length, 1);
  stop(owner);
  assert.equal(lifecycle.get('pagehide')?.size ?? 0, 0);
  assert.equal(lifecycle.get('pageshow')?.size ?? 0, 0);
}

// Pagehide removes message content; BFCache restore performs a read-only first-page refresh.
{
  const root = setup();
  const { owner } = await start(root);
  root.click(root.nodes.get('#inbox-list-body').children[0].children[4].children[0]);
  await until(() => root.nodes.get('#inbox-detail-body').textContent === bodyText, 'detail before pagehide');
  const listCallsBeforeHide = fetchCalls.filter(call => call.url.pathname === '/api/core/inbox').length;
  root.emitLifecycle('pagehide');
  assert.equal(root.nodes.get('#inbox-list-body').textContent.includes('未読の件名'), false);
  assert.equal(root.nodes.get('#inbox-detail-body').textContent, '');
  root.emitLifecycle('pageshow');
  await until(() => fetchCalls.filter(call => call.url.pathname === '/api/core/inbox').length > listCallsBeforeHide, 'read-only list refresh after pageshow');
  assert.equal(readCalls.length, 0);
  stop(owner);
}

console.log('application-inbox-ui-checks: PASS');