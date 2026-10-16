import assert from 'node:assert/strict';
import { readFile } from 'node:fs/promises';
import { pathToFileURL } from 'node:url';
import { resolve } from 'node:path';

const ui = await import(pathToFileURL(resolve('src/Sem.Web/wwwroot/invitation-management.js')).href);
const invitationMarkup = await readFile(resolve('src/Sem.Web/Components/Pages/InvitationManagement.razor'), 'utf8');
assert.ok(invitationMarkup.includes('@rendermode @(new InteractiveServerRenderMode(prerender: false))'));
assert.match(invitationMarkup, /<fieldset id="invitation-issue-fields" disabled>/);
assert.match(invitationMarkup, /<input id="invitation-issue-link" type="text" readonly/);
assert.doesNotMatch(invitationMarkup, /<input[^>]*id="invitation-issue-link"[^>]*\bname=/);
assert.doesNotMatch(invitationMarkup, /<a[^>]+invitation-issue-link/);
const issueMarkupStart = invitationMarkup.indexOf('<section id="invitation-issue"');
const issueMarkupEnd = invitationMarkup.indexOf('<section id="invitation-management-admin"', issueMarkupStart);
assert.ok(issueMarkupStart >= 0 && issueMarkupEnd > issueMarkupStart);
const issueMarkup = invitationMarkup.slice(issueMarkupStart, issueMarkupEnd);
assert.doesNotMatch(issueMarkup, /\bname\s*=/i);
assert.doesNotMatch(issueMarkup, /\bhref\s*=/i);
const actorId = 'actor-invitation-test';
const csrfToken = 'csrf-test-token';
const realSetTimeout = globalThis.setTimeout.bind(globalThis);

// The API accepts pages only through 10000; either collection may independently report more rows.
const pageLimitNotice = 'APIの最大ページ数10000に達し、次データが残っているため、この画面では取得できません。';
assert.deepEqual(ui.paginationState(9999, true, false), { canAdvance: true, limitReached: false, notice: '' });
assert.deepEqual(ui.paginationState(9999, false, true), { canAdvance: true, limitReached: false, notice: '' });
assert.deepEqual(ui.paginationState(10000, true, false), { canAdvance: false, limitReached: true, notice: pageLimitNotice });
assert.deepEqual(ui.paginationState(10000, false, true), { canAdvance: false, limitReached: true, notice: pageLimitNotice });
assert.deepEqual(ui.paginationState(10000, false, false), { canAdvance: false, limitReached: false, notice: '' });
assert.deepEqual(ui.paginationState(10001, true, true), { canAdvance: false, limitReached: false, notice: '' });

class FakeElement {
  constructor(tagName = 'div', ownerDocument = null) {
    this.tagName = tagName.toUpperCase();
    this.ownerDocument = ownerDocument;
    this.children = [];
    this.attributes = new Map();
    this.listeners = new Map();
    this.textContent = '';
    this.value = '';
    this.checked = false;
    this.disabled = false;
    this.hidden = false;
    this.type = '';
    this.readOnly = false;
    this.focusCount = 0;
    this.selectCount = 0;
  }
  get innerHTML() { return undefined; }
  set innerHTML(_value) { throw new Error('innerHTML must not be used'); }
  append(...nodes) { this.children.push(...nodes); }
  replaceChildren(...nodes) { this.children = [...nodes]; }
  setAttribute(name, value) { this.attributes.set(name, String(value)); }
  addEventListener(type, listener) {
    if (!this.listeners.has(type)) this.listeners.set(type, new Set());
    this.listeners.get(type).add(listener);
  }
  removeEventListener(type, listener) { this.listeners.get(type)?.delete(listener); }
  emit(type, target = this) {
    const event = { target, defaultPrevented: false, preventDefault() { this.defaultPrevented = true; } };
    for (const listener of this.listeners.get(type) ?? []) listener(event);
    return event;
  }
  click() {
    if (!this.disabled) this.emit('click');
  }
  focus() { this.focusCount++; }
  select() { this.selectCount++; }
  allText() {
    return [this.textContent, ...this.children.map(child => child.allText?.() ?? child.textContent ?? '')].join('\n');
  }
}

class FakeRoot extends FakeElement {
  constructor() {
    const document = { createElement: tag => new FakeElement(tag, document) };
    super('main', document);
    this.isConnected = true;
    this.nodes = new Map();
    const ids = [
      'scope','status','error','module-error','refresh','admin','selected','selected-summary',
      'revoke-form','reason','confirm','revoke','unknown','invitations-head','invitations-body',
      'requests-head','requests-body','previous','next','page'
    ];
    for (const id of ids) this.nodes.set(`#invitation-management-${id}`, new FakeElement(id.includes('body') || id.includes('head') ? 'tbody' : 'div', document));
    this.nodes.set('#invitation-issue', new FakeElement('section', document));
    for (const id of ['form','fields','purpose','count','days','confirm','submit','result','link','copy','copy-status','status','error']) {
      this.nodes.set(`#invitation-issue-${id}`, new FakeElement(id === 'fields' ? 'fieldset' : 'div', document));
    }
    this.get('issue-count').value = '1';
    this.get('issue-days').value = '1';
    this.get('issue-link').readOnly = true;
  }
  querySelector(selector) { return this.nodes.get(selector) ?? null; }
  emitFromRoot(type, target) { return this.emit(type, target); }
  get(id) {
    if (id === 'issue') return this.nodes.get('#invitation-issue');
    if (id.startsWith('issue-')) return this.nodes.get(`#invitation-${id}`);
    return this.nodes.get(`#invitation-management-${id}`);
  }
  allText() { return [...this.nodes.values()].map(node => node.allText()).join('\n'); }
}

function invitation(overrides = {}) {
  return {
    id: 'invitation-1', ownerId: 'user-1', ownerName: 'Owner', issuerId: 'admin-1', issuerName: 'Admin',
    requestId: null, purpose: null, issuanceSource: null, limit: 3, uses: 0, created: 1_700_000_000, expires: null, disabled: false,
    revokedAt: null, revokedBy: null, revokerName: null, revocationReason: null, ...overrides
  };
}
function request(overrides = {}) {
  return {
    id: 'request-1', userId: 'user-1', userName: 'Requester', reason: 'A reason', count: 2, days: 7,
    status: 'Pending', created: 1_700_000_000, decidedAt: null, decidedBy: null, deciderName: null, ...overrides
  };
}
function page(overrides = {}) {
  return {
    actorId, isAdmin: true, page: 1, pageSize: 50, hasMoreInvitations: false, hasMoreRequests: false,
    invitations: [invitation()], requests: [request()], ...overrides
  };
}
function json(value, status = 200) {
  return new Response(JSON.stringify(value), { status, headers: { 'Content-Type': 'application/json; charset=utf-8' } });
}
function setGlobal(name, value) {
  Object.defineProperty(globalThis, name, { value, writable: true, configurable: true });
}
async function until(predicate, label) {
  for (let i = 0; i < 250; i++) {
    if (predicate()) return;
    await new Promise(resolve => realSetTimeout(resolve, 4));
  }
  assert.fail(`timed out waiting for ${label}`);
}

let currentPage = page();
let pageResponseOverride = null;
let revokeMode = 'success';
let issueMode = 'success';
let calls = [];
let revokeCalls = [];
let issueCalls = [];
let resolveIssueResponse = null;
let lifecycleListeners = new Map();
let ownerCounter = 0;
function setup({ initialPage = page(), responseOverride = null, mode = 'success', issuanceMode = 'success' } = {}) {
  currentPage = initialPage;
  pageResponseOverride = responseOverride;
  revokeMode = mode;
  issueMode = issuanceMode;
  calls = [];
  revokeCalls = [];
  issueCalls = [];
  resolveIssueResponse = null;
  lifecycleListeners = new Map();
  setGlobal('addEventListener', (type, listener) => {
    if (!lifecycleListeners.has(type)) lifecycleListeners.set(type, new Set());
    lifecycleListeners.get(type).add(listener);
  });
  setGlobal('removeEventListener', (type, listener) => lifecycleListeners.get(type)?.delete(listener));
  setGlobal('location', { href: 'https://app.example/invitations', origin: 'https://app.example' });
  setGlobal('navigator', {});
  setGlobal('fetch', async (input, options = {}) => {
    const url = new URL(String(input));
    calls.push({ url, options });
    assert.equal(url.origin, 'https://app.example');
    assert.equal(options.credentials, 'same-origin');
    assert.equal(options.mode, 'same-origin');
    assert.equal(options.cache, 'no-store');
    assert.equal(options.redirect, 'error');
    if (url.pathname === '/api/core/invitations' && options.method === 'GET') {
      if (pageResponseOverride) return pageResponseOverride(url);
      return json({ ...currentPage, page: Number(url.searchParams.get('page') ?? 1) });
    }
    if (url.pathname === '/api/csrf' && options.method === 'GET') return json({ token: csrfToken });
    if (url.pathname === '/api/core/invitations/issue' && options.method === 'POST') {
      assert.equal(url.searchParams.getAll('expectedActor').length, 1);
      assert.equal(url.searchParams.get('expectedActor'), actorId);
      assert.equal(url.searchParams.size, 1);
      assert.equal(options.headers['X-CSRF-TOKEN'], csrfToken);
      const body = JSON.parse(options.body);
      assert.deepEqual(Object.keys(body).sort(), ['count','days','purpose']);
      assert.ok(Buffer.byteLength(options.body, 'utf8') <= 4096);
      issueCalls.push(body);
      if (issueMode === 'network-failure') throw new TypeError('synthetic network failure');
      if (issueMode === 'deferred') return await new Promise(resolve => { resolveIssueResponse = resolve; });
      const link = issueMode === 'invalid-link'
        ? 'https://attacker.example/join#invite=must-not-render'
        : 'https://app.example/join#invite=secret-invite-code';
      return json({
        actorId, id: 'issued-invitation-1', purpose: body.purpose, issuanceSource: 'Direct',
        limit: body.count, uses: 0, created: 1_700_000_000, expires: 1_700_086_400,
        link
      });
    }
    if (url.pathname === '/api/core/invitations/revoke' && options.method === 'POST') {
      assert.equal(url.searchParams.getAll('expectedActor').length, 1);
      assert.equal(url.searchParams.get('expectedActor'), actorId);
      assert.equal(options.headers['X-CSRF-TOKEN'], csrfToken);
      const body = JSON.parse(options.body);
      assert.deepEqual(Object.keys(body).sort(), ['explicitlyConfirmed','id','reason']);
      assert.ok(Buffer.byteLength(options.body, 'utf8') <= 4096);
      revokeCalls.push(body);
      if (revokeMode === 'network-failure') throw new TypeError('synthetic network failure');
      currentPage = { ...currentPage, invitations: currentPage.invitations.map(row => row.id === body.id
        ? { ...row, disabled: true, revokedAt: 1_700_000_100, revokedBy: actorId, revokerName: 'Admin', revocationReason: body.reason }
        : row) };
      if (revokeMode === 'oversized-response') return new Response(JSON.stringify({ actorId, id: body.id, disabled: true, padding: 'x'.repeat(20_000) }));
      return json({ actorId, id: body.id, disabled: true });
    }
    assert.fail(`unexpected request ${options.method} ${url.pathname}`);
  });
  return new FakeRoot();
}
function dispatchLifecycle(type, event = {}) {
  for (const listener of lifecycleListeners.get(type) ?? []) listener(event);
}
async function start(root = setup()) {
  const owner = `invitation-owner-${++ownerCounter}`;
  await ui.init(root, owner);
  return { root, owner };
}
async function stop(owner) { ui.cleanup(owner); }
function rowAction(root, index = 0) {
  const row = root.get('invitations-body').children[index];
  return row.children.at(-1).children[0];
}
function selectAndConfirm(root, reasonText = '  User requested revocation  ') {
  rowAction(root).click();
  const reasonNode = root.get('reason');
  reasonNode.value = reasonText;
  reasonNode.emit('input');
  const confirm = root.get('confirm');
  confirm.checked = true;
  confirm.emit('change');
}
function formSubmit(root) { root.emitFromRoot('submit', root.get('revoke-form')); }
function prepareIssue(root, { purpose = '  Local event  ', count = '2', days = '5' } = {}) {
  root.get('issue-purpose').value = purpose;
  root.get('issue-purpose').emit('input');
  root.get('issue-count').value = count;
  root.get('issue-count').emit('input');
  root.get('issue-days').value = days;
  root.get('issue-days').emit('input');
  root.get('issue-confirm').checked = true;
  root.get('issue-confirm').emit('change');
}
function issueSubmit(root) { root.emitFromRoot('submit', root.get('issue-form')); }

// A realistic 50+50 page with long reasons exceeds 16 KiB and remains valid under the 512 KiB list bound.
{
  const bulky = page({
    hasMoreInvitations: true,
    invitations: Array.from({ length: 50 }, (_, i) => invitation({ id: `invite-${i}`, revocationReason: 'R'.repeat(500) })),
    requests: Array.from({ length: 50 }, (_, i) => request({ id: `request-${i}`, reason: 'Q'.repeat(500) }))
  });
  const bytes = Buffer.byteLength(JSON.stringify(bulky));
  assert.ok(bytes > 16 * 1024 && bytes < 512 * 1024, `fixture should be between the POST and list bounds (${bytes} bytes)`);
  const root = setup({ initialPage: bulky });
  const { owner } = await start(root);
  assert.equal(root.get('invitations-body').children.length, 50);
  assert.equal(root.get('requests-body').children.length, 50);
  assert.match(root.get('page').textContent, /ページ 1/);
  root.emitFromRoot('click', root.get('next'));
  await until(() => root.get('page').textContent.includes('ページ 2'), 'next page with shared pagination');
  assert.equal(root.get('previous').disabled, false);
  assert.equal(calls.filter(call => call.url.pathname === '/api/core/invitations').at(-1).url.searchParams.get('page'), '2');
  await stop(owner);
}

// Missing display names are shown as unknown; arbitrary text stays text and does not become markup.
{
  const malicious = '<img src=x onerror=alert(1)>';
  const root = setup({ initialPage: page({ invitations: [invitation({ ownerName: '', ownerId: 'user-1', issuerName: malicious, purpose: malicious })] }) });
  const { owner } = await start(root);
  assert.match(root.allText(), /不明（user-1）/);
  assert.ok(root.allText().includes(malicious));
  assert.equal(root.innerHTML, undefined);
  await stop(owner);
}

// A response over 512 KiB is rejected without parsing partial JSON and disables stale-row writes.
{
  const root = setup();
  const { owner } = await start(root);
  assert.equal(rowAction(root).disabled, false);
  pageResponseOverride = () => new Response(`{"actorId":"${actorId}","padding":"${'x'.repeat(512 * 1024)}"}`);
  root.emitFromRoot('click', root.get('refresh'));
  await until(() => root.get('error').textContent.includes('上限'), 'oversized list response lock');
  assert.equal(rowAction(root).disabled, true);
  assert.equal(root.get('revoke').disabled, true);
  assert.equal(root.get('refresh').disabled, false);
  assert.equal(revokeCalls.length, 0);
  assert.equal(root.allText().includes('padding'), false);
  await stop(owner);
}

// Non-admin inventory has no revocation affordance.
{
  const root = setup({ initialPage: page({ isAdmin: false }) });
  const { owner } = await start(root);
  assert.equal(root.get('admin').hidden, true);
  assert.equal(root.get('issue').hidden, true);
  assert.equal(root.get('issue-fields').disabled, true);
  assert.equal(root.get('invitations-body').children[0].children.length, 12);
  await stop(owner);
}

// Legacy rows expose nullable purpose/source as unrecorded rather than inferred values.
{
  const root = setup({ initialPage: page({ invitations: [invitation({ purpose: null, issuanceSource: null })] }) });
  const { owner } = await start(root);
  const row = root.get('invitations-body').children[0];
  assert.equal(row.children[4].textContent, '未記録');
  assert.equal(row.children[5].textContent, '未記録');
  assert.equal(row.children.length, 13);
  await stop(owner);
}

// Direct issuance requires a valid purpose, range-checked values, and fresh explicit confirmation.
{
  const root = setup();
  const { owner } = await start(root);
  assert.equal(issueCalls.length, 0);
  assert.equal(root.get('issue').hidden, false);
  assert.equal(root.get('issue-submit').disabled, true);
  prepareIssue(root, { purpose: '\u0001invalid purpose' });
  assert.equal(root.get('issue-submit').disabled, true);
  prepareIssue(root, { count: '6' });
  assert.equal(root.get('issue-submit').disabled, true);
  prepareIssue(root, { days: '8' });
  assert.equal(root.get('issue-submit').disabled, true);
  prepareIssue(root);
  root.get('issue-purpose').value = 'Changed after confirmation';
  root.get('issue-purpose').emit('input');
  assert.equal(root.get('issue-confirm').checked, false);
  assert.equal(root.get('issue-submit').disabled, true);
  prepareIssue(root);
  assert.equal(root.get('issue-submit').disabled, false);
  issueSubmit(root);
  await until(() => root.get('status').textContent.includes('発行をサーバーが確認'), 'successful direct issuance');
  assert.deepEqual(issueCalls, [{ purpose: 'Local event', count: 2, days: 5 }]);
  assert.equal(root.get('issue-link').value, 'https://app.example/join#invite=secret-invite-code');
  assert.equal(root.get('issue-link').readOnly, true);
  assert.equal(root.allText().includes('secret-invite-code'), false);
  assert.equal(root.allText().includes('https://app.example/join#invite='), false);
  assert.equal(calls.some(call => call.url.href.includes('secret-invite-code')), false);
  assert.equal(root.get('issue-result').hidden, false);
  assert.equal(root.get('issue-confirm').checked, false);

  const copied = [];
  setGlobal('navigator', { clipboard: { writeText: async value => { copied.push(value); } } });
  assert.equal(copied.length, 0, 'link is not copied without an explicit button click');
  root.emitFromRoot('click', root.get('issue-copy'));
  await until(() => copied.length === 1, 'explicit invitation link copy');
  assert.equal(copied[0], 'https://app.example/join#invite=secret-invite-code');
  setGlobal('navigator', {});
  root.emitFromRoot('click', root.get('issue-copy'));
  assert.equal(root.get('issue-link').focusCount, 1);
  assert.equal(root.get('issue-link').selectCount, 1);
  assert.match(root.get('issue-copy-status').textContent, /手動でコピー/);

  root.get('issue-purpose').value = 'Replacement purpose';
  root.get('issue-purpose').emit('input');
  assert.equal(root.get('issue-confirm').checked, false);
  assert.equal(root.get('issue-link').value, '');
  assert.equal(root.get('issue-result').hidden, true);
  assert.equal(root.get('issue-submit').disabled, true);
  assert.equal(issueCalls.length, 1);
  await stop(owner);
  assert.equal(root.get('issue-link').value, '');
}

// Unknown issue outcomes remain locked after a read-only refresh and are never replayed.
{
  const root = setup({ issuanceMode: 'network-failure' });
  const { owner } = await start(root);
  prepareIssue(root);
  issueSubmit(root);
  await until(() => root.get('unknown').hidden === false, 'unknown issuance outcome');
  assert.equal(root.get('issue-submit').disabled, true);
  assert.equal(issueCalls.length, 1);
  const readCount = calls.filter(call => call.url.pathname === '/api/core/invitations' && call.options.method === 'GET').length;
  root.emitFromRoot('click', root.get('refresh'));
  await until(() => calls.filter(call => call.url.pathname === '/api/core/invitations' && call.options.method === 'GET').length > readCount, 'read-only issuance reconciliation');
  assert.equal(issueCalls.length, 1);
  assert.equal(root.get('unknown').hidden, false);
  assert.equal(root.get('issue-submit').disabled, true);
  issueSubmit(root);
  assert.equal(issueCalls.length, 1);
  await stop(owner);
}

// A changed actor clears the temporary secret-link field before any successor state is rendered.
{
  const root = setup();
  const { owner } = await start(root);
  prepareIssue(root);
  issueSubmit(root);
  await until(() => root.get('issue-link').value !== '', 'issued link before actor change');
  currentPage = page({ actorId: 'different-actor' });
  root.emitFromRoot('click', root.get('refresh'));
  await until(() => root.get('issue-link').value === '' && root.get('issue').hidden, 'actor-bound link cleanup');
  assert.equal(root.get('issue-link').value, '');
  await stop(owner);
}

// A malformed or cross-origin success link is rejected and never rendered as a link.
{
  const root = setup({ issuanceMode: 'invalid-link' });
  const { owner } = await start(root);
  prepareIssue(root);
  issueSubmit(root);
  await until(() => root.get('unknown').hidden === false, 'invalid returned secret link');
  assert.equal(root.get('issue-link').value, '');
  assert.equal(root.allText().includes('must-not-render'), false);
  assert.equal(root.get('issue-submit').disabled, true);
  assert.equal(issueCalls.length, 1);
  await stop(owner);
}

// Leaving the page clears the temporary secret-link input and in-memory owner state.
{
  const root = setup();
  const { owner } = await start(root);
  prepareIssue(root);
  issueSubmit(root);
  await until(() => root.get('issue-link').value !== '', 'issued link before page cleanup');
  await stop(owner);
  assert.equal(root.get('issue-link').value, '');
  assert.equal(root.get('issue-result').hidden, true);
}

// A response arriving after cleanup cannot repopulate a detached page's secret-link field.
{
  const root = setup({ issuanceMode: 'deferred' });
  const { owner } = await start(root);
  prepareIssue(root);
  issueSubmit(root);
  await until(() => resolveIssueResponse !== null, 'pending issuance response');
  const resolvePending = resolveIssueResponse;
  await stop(owner);
  resolvePending(json({
    actorId, id: 'late-issued-invitation', purpose: 'Local event', issuanceSource: 'Direct',
    limit: 2, uses: 0, created: 1_700_000_000, expires: 1_700_086_400,
    link: 'https://app.example/join#invite=late-secret'
  }));
  await new Promise(resolve => realSetTimeout(resolve, 10));
  assert.equal(root.get('issue-link').value, '');
  assert.equal(root.get('issue-result').hidden, true);
}

// BFCache/pagehide clears the secret and invalidates a deferred issue POST; pageshow performs only a fresh read.
{
  const root = setup({ issuanceMode: 'deferred' });
  const { owner } = await start(root);
  prepareIssue(root);
  issueSubmit(root);
  await until(() => resolveIssueResponse !== null, 'issue POST pending before pagehide');
  assert.equal(issueCalls.length, 1);
  const resolvePending = resolveIssueResponse;
  const readsBeforeHide = calls.filter(call => call.url.pathname === '/api/core/invitations' && call.options.method === 'GET').length;
  dispatchLifecycle('pagehide', { persisted: true });
  assert.equal(root.get('issue-link').value, '');
  assert.equal(root.get('issue-result').hidden, true);
  assert.equal(root.get('issue-confirm').checked, false);
  assert.equal(root.get('admin').hidden, true);
  const hiddenInvitationRows = root.get('invitations-body').children;
  assert.equal(hiddenInvitationRows.length, 1, 'cleared inventory should render its empty-state row');
  assert.equal(hiddenInvitationRows[0].children.length, 1);
  assert.equal(hiddenInvitationRows[0].children[0].textContent, 'このページに招待はありません。');
  assert.equal(hiddenInvitationRows[0].children[0].attributes.get('colspan'), '12');
  assert.equal(root.get('requests-body').children.length, 1);
  assert.equal(root.get('requests-body').children[0].children[0].textContent, 'このページに依頼はありません。');
  assert.equal(root.allText().includes('invitation-1'), false, 'previous inventory row must not survive pagehide');
  assert.equal(root.allText().includes('request-1'), false, 'previous request row must not survive pagehide');
  assert.equal(root.allText().includes('secret-invite-code'), false);
  assert.equal(root.get('issue-submit').disabled, true);
  assert.equal(calls.find(call => call.url.pathname === '/api/core/invitations/issue')?.options.signal.aborted, true);
  dispatchLifecycle('pageshow', { persisted: true });
  await until(() => calls.filter(call => call.url.pathname === '/api/core/invitations' && call.options.method === 'GET').length > readsBeforeHide, 'BFCache read-only refresh');
  assert.equal(issueCalls.length, 1, 'pageshow must not replay issue POST');
  assert.equal(root.get('issue-submit').disabled, true, 'restored page requires fresh confirmation');
  resolvePending(json({
    actorId, id: 'late-issued-invitation', purpose: 'Local event', issuanceSource: 'Direct',
    limit: 2, uses: 0, created: 1_700_000_000, expires: 1_700_086_400,
    link: 'https://app.example/join#invite=late-secret'
  }));
  await new Promise(resolve => realSetTimeout(resolve, 10));
  assert.equal(root.get('issue-link').value, '');
  assert.equal(root.get('issue-result').hidden, true);
  assert.equal(root.get('unknown').hidden, false, 'unknown issue outcome stays locked after restoration');
  assert.equal(root.get('issue-submit').disabled, true);
  assert.equal(issueCalls.length, 1);
  await stop(owner);
  assert.equal(lifecycleListeners.get('pagehide')?.size ?? 0, 0);
  assert.equal(lifecycleListeners.get('pageshow')?.size ?? 0, 0);
}

// A late explicit clipboard promise cannot repopulate the link or status after hide/restore.
{
  const root = setup();
  const { owner } = await start(root);
  prepareIssue(root);
  issueSubmit(root);
  await until(() => root.get('issue-link').value !== '', 'issued link before deferred clipboard');
  let resolveClipboard;
  let clipboardCalls = 0;
  setGlobal('navigator', { clipboard: { writeText: async () => {
    clipboardCalls++;
    await new Promise(resolve => { resolveClipboard = resolve; });
  } } });
  root.emitFromRoot('click', root.get('issue-copy'));
  await until(() => resolveClipboard !== undefined, 'explicit clipboard write pending');
  assert.equal(clipboardCalls, 1);
  dispatchLifecycle('pagehide', { persisted: true });
  assert.equal(root.get('issue-link').value, '');
  assert.equal(root.get('issue-result').hidden, true);
  dispatchLifecycle('pageshow', { persisted: true });
  await until(() => calls.filter(call => call.url.pathname === '/api/core/invitations' && call.options.method === 'GET').length >= 2, 'clipboard case read-only restore');
  resolveClipboard();
  await new Promise(resolve => realSetTimeout(resolve, 10));
  assert.equal(root.get('issue-link').value, '');
  assert.equal(root.get('issue-copy-status').textContent, '');
  assert.equal(clipboardCalls, 1);
  await stop(owner);
}

// Admin revocation requires a fresh reason and confirmation, and sends only the documented fields.
{
  const root = setup();
  const { owner } = await start(root);
  rowAction(root).click();
  const reasonNode = root.get('reason');
  reasonNode.value = '';
  reasonNode.emit('input');
  root.get('confirm').checked = true;
  root.get('confirm').emit('change');
  assert.equal(root.get('revoke').disabled, true);
  reasonNode.value = '  User requested revocation  ';
  reasonNode.emit('input');
  assert.equal(root.get('confirm').checked, false);
  root.get('confirm').checked = true;
  root.get('confirm').emit('change');
  assert.equal(root.get('revoke').disabled, false);
  formSubmit(root);
  await until(() => root.get('status').textContent.includes('失効をサーバーが確認'), 'successful revocation');
  assert.deepEqual(revokeCalls, [{ id: 'invitation-1', reason: 'User requested revocation', explicitlyConfirmed: true }]);
  assert.equal(root.get('confirm').checked, false);
  assert.equal(root.get('selected').hidden, true);
  assert.equal(root.get('invitations-body').children[0].children[10].textContent, '無効');
  await stop(owner);
}

// Unknown write outcomes lock mutations and are only reconciled by an explicit read; no replay occurs.
for (const mode of ['network-failure', 'oversized-response']) {
  const root = setup({ mode });
  const { owner } = await start(root);
  selectAndConfirm(root);
  formSubmit(root);
  await until(() => root.get('unknown').hidden === false, `${mode} unknown outcome`);
  assert.equal(root.get('revoke').disabled, true);
  assert.equal(revokeCalls.length, 1);
  if (mode === 'network-failure') {
    currentPage = { ...currentPage, invitations: currentPage.invitations.map(row => ({ ...row, disabled: true })) };
  }
  root.emitFromRoot('click', root.get('refresh'));
  await until(() => root.get('unknown').hidden === true, `${mode} manual reconcile`);
  assert.equal(revokeCalls.length, 1);
  await stop(owner);
}

console.log('invitation-management-ui-checks: PASS');
