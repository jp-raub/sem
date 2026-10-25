import assert from 'node:assert/strict';
import { readFile } from 'node:fs/promises';

const base = new URL('../', import.meta.url);
const adminSource = await readFile(new URL('src/Sem.Web/wwwroot/passkey-recovery-admin.js', base), 'utf8');
const publicSource = await readFile(new URL('src/Sem.Web/wwwroot/passkey-recovery.js', base), 'utf8');
const adminPage = await readFile(new URL('src/Sem.Web/Components/Pages/PasskeyRecoveryAdmin.razor', base), 'utf8');
const publicPage = await readFile(new URL('src/Sem.Web/Components/Pages/PasskeyRecovery.razor', base), 'utf8');
assert.doesNotMatch(adminSource + publicSource, /\b(?:localStorage|sessionStorage)\b|\.innerHTML\b|console\.(?:log|debug|info)\b/);
assert.match(adminPage, /id="recovery-admin-root"/);
assert.match(adminPage, /id="recovery-admin-target-id" maxlength="128"/);
assert.match(adminPage, /id="recovery-admin-lookup-form" method="post"/);
assert.match(adminPage, /初回に記録された理由が保持され、新しい理由は受け付けません/);
assert.match(adminPage, /recovery-admin-reason" minlength="3" maxlength="500"/);
assert.doesNotMatch(adminPage.match(/<input id="recovery-admin-target-id"[^>]*>/)?.[0] ?? '', /\bname=/);
assert.match(adminPage, /復旧画面を読み込めませんでした/);
assert.match(publicPage, /id="recovery-root"/);
assert.match(publicPage, /id="recovery-login-link" href="\/"/);
assert.match(publicPage, /復旧画面を読み込めませんでした/);

class Element {
    constructor(id) { Object.assign(this, { id, hidden: false, disabled: false, value: '', checked: false, textContent: '', listeners: new Map() }); }
    addEventListener(type, fn) { this.listeners.set(type, fn); }
    removeEventListener(type) { this.listeners.delete(type); }
    emit(type) { this.listeners.get(type)?.({ preventDefault() {} }); }
}
function makeRoot(id, names) {
    const elements = new Map(names.map(name => [name, new Element(name)]));
    return { id, isConnected: true, elements, getAttribute() { return null; }, querySelector(selector) { return elements.get(selector.replace(/^#/, '')) ?? null; } };
}
function putGlobal(name, value) { Object.defineProperty(globalThis, name, { configurable: true, writable: true, value }); }
let currentUrl = new URL('https://sem.test/');
let activeRoot = null;
let calls = [];
let chronology = [];
let handler;
const windowListeners = new Map();
const windowObject = {
    PublicKeyCredential: {
        parseRequestOptionsFromJSON: () => ({ challenge: Uint8Array.of(1).buffer, timeout: 30_000 }),
        parseCreationOptionsFromJSON: () => ({ challenge: Uint8Array.of(2).buffer, user: { id: Uint8Array.of(3).buffer }, timeout: 30_000 })
    },
    addEventListener(type, fn) { windowListeners.set(type, fn); },
    removeEventListener(type) { windowListeners.delete(type); }
};
putGlobal('window', windowObject);
putGlobal('location', {
    get origin() { return currentUrl.origin; },
    get pathname() { return currentUrl.pathname; },
    get search() { return currentUrl.search; },
    get hash() { return currentUrl.hash; }
});
putGlobal('history', {
    state: null, replacements: [],
    replaceState(state, _title, url) { this.state = state; this.replacements.push(String(url)); currentUrl = new URL(String(url), currentUrl.origin); }
});
putGlobal('document', { getElementById(id) { return activeRoot?.id === id ? activeRoot : null; } });
putGlobal('navigator', { credentials: {
    async get() { chronology.push('native-get'); return { toJSON: () => ({ type: 'public-key', id: 'assertion' }) }; },
    async create() { chronology.push('native-create'); return { toJSON: () => ({ type: 'public-key', id: 'attestation' }) }; }
} });
function json(value, status = 200) { return new Response(JSON.stringify(value), { status, headers: { 'Content-Type': 'application/json; charset=utf-8' } }); }
function installFetch(fn) {
    handler = fn;
    putGlobal('fetch', async (input, init = {}) => {
        const url = new URL(input instanceof URL ? input.href : String(input), currentUrl.origin);
        const call = { url, init, method: init.method || 'GET' };
        calls.push(call);
        return handler(call);
    });
}
async function moduleFor(source, name) { return import(`data:text/javascript;base64,${Buffer.from(source).toString('base64')}#${name}`); }
async function until(test, message, timeout = 2500) {
    const end = Date.now() + timeout;
    while (Date.now() < end) { if (test()) return; await new Promise(resolve => setTimeout(resolve, 5)); }
    assert.fail(message);
}
function adminRoot() {
    return makeRoot('recovery-admin-root', [
        'recovery-admin-status', 'recovery-admin-error', 'recovery-admin-refresh', 'recovery-admin-panel',
        'recovery-admin-lookup-form', 'recovery-admin-target-id', 'recovery-admin-load-target',
        'recovery-admin-target-summary', 'recovery-admin-target-name', 'recovery-admin-target-recipient',
        'recovery-admin-target-enabled', 'recovery-admin-target-recovery', 'recovery-admin-target-expiry',
        'recovery-admin-actions-form', 'recovery-admin-reason', 'recovery-admin-confirm',
        'recovery-admin-start', 'recovery-admin-resend', 'recovery-admin-readonly'
    ]);
}

// Admin flow: explicit actor, target and reason; resend retains original reason; expiry requires fresh confirmation.
currentUrl = new URL('https://sem.test/service/passkey-recovery');
activeRoot = adminRoot();
calls = [];
chronology = [];
let currentTarget = { id: 'target-user', displayName: 'Target', maskedRecipient: 't***@example.test', enabled: true, recovering: false };
let starts = 0;
let resends = 0;
installFetch(call => {
    const { url, method, init } = call;
    if (url.pathname === '/api/csrf') return json({ token: 'csrf-admin' });
    if (url.pathname === '/api/passkey-recovery-admin-context' && method === 'GET') {
        if (!url.search) return json({ actorId: 'admin-actor' });
        assert.deepEqual(url.searchParams.getAll('id'), ['target-user']);
        return json({ actorId: 'admin-actor', target: { ...currentTarget } });
    }
    if (url.pathname === '/api/passkey-recovery-admin-options') {
        assert.deepEqual(url.searchParams.getAll('expectedActor'), ['admin-actor']);
        assert.deepEqual(JSON.parse(init.body), {
            targetUserId: 'target-user', reason: starts === 0 ? 'abc' : 'xyz'
        });
        assert.equal(init.credentials, 'same-origin'); assert.equal(init.mode, 'same-origin');
        assert.equal(init.cache, 'no-store'); assert.equal(init.redirect, 'error');
        assert.equal(init.headers['X-CSRF-TOKEN'], 'csrf-admin');
        chronology.push('admin-options');
        return json({ publicKey: { challenge: 'AQ' } });
    }
    if (url.pathname === '/api/passkey-recovery-start') {
        assert.deepEqual(url.searchParams.getAll('expectedActor'), ['admin-actor']);
        const body = JSON.parse(init.body);
        assert.equal(body.targetUserId, 'target-user'); assert.equal(typeof body.credential, 'string');
        assert.equal(body.explicitlyConfirmed, true);
        assert.equal(body.reason, starts === 0 ? 'abc' : 'xyz');
        assert.equal(chronology.at(-1), 'native-get');
        chronology.push('admin-start'); starts++;
        return json({ operationId: `operation-${starts}`, expiresAt: new Date(Date.now() + 60_000).toISOString(), maskedRecipient: 't***@example.test' });
    }
    if (url.pathname === '/api/passkey-recovery-resend-options') {
        assert.deepEqual(url.searchParams.getAll('expectedActor'), ['admin-actor']);
        assert.deepEqual(JSON.parse(init.body), { operationId: 'operation-1' });
        chronology.push('resend-options');
        return json({ publicKey: { challenge: 'AQ' } });
    }
    if (url.pathname === '/api/passkey-recovery-resend') {
        assert.deepEqual(url.searchParams.getAll('expectedActor'), ['admin-actor']);
        const body = JSON.parse(init.body);
        assert.deepEqual(Object.keys(body).sort(), ['credential', 'operationId']);
        assert.equal(body.operationId, 'operation-1');
        chronology.push('resend'); resends++;
        return json({ operationId: 'operation-1', expiresAt: new Date(Date.now() + 60_000).toISOString(), maskedRecipient: 't***@example.test' });
    }
    assert.fail(`Unexpected admin request: ${method} ${url}`);
});
const admin = await moduleFor(adminSource, 'admin');
activeRoot.getAttribute = name => name === 'data-selected-target' ? 'target-user' : null;
await admin.init(activeRoot);
assert.equal(activeRoot.elements.get('recovery-admin-target-id').value, 'target-user', '選択対象は入力候補へ引き継ぐ');
assert.equal(calls.length, 1, '入口表示では管理者文脈の読取だけを実行する');
assert.equal(chronology.length, 0, '入口表示で本人認証・復旧を開始しない');
assert.equal(activeRoot.elements.get('recovery-admin-target-summary').hidden, true, '対象は明示照会するまで未確認');
assert.match(activeRoot.elements.get('recovery-admin-status').textContent, /対象ユーザーID/);
const targetId = activeRoot.elements.get('recovery-admin-target-id');
targetId.value = 'target-user'; targetId.emit('input');
activeRoot.elements.get('recovery-admin-lookup-form').emit('submit');
await until(() => !activeRoot.elements.get('recovery-admin-target-summary').hidden, 'admin lookup did not finish');
const reason = activeRoot.elements.get('recovery-admin-reason');
const confirm = activeRoot.elements.get('recovery-admin-confirm');
const start = activeRoot.elements.get('recovery-admin-start');
const resend = activeRoot.elements.get('recovery-admin-resend');
reason.value = ' ab '; reason.emit('input'); confirm.checked = true; confirm.emit('change');
assert.equal(start.disabled, true, 'trimmed two-character reason is rejected');
reason.value = 'abc'; reason.emit('input');
assert.equal(start.disabled, false);
activeRoot.elements.get('recovery-admin-actions-form').emit('submit');
await until(() => starts === 1 && activeRoot.elements.get('recovery-admin-status').textContent.includes('キューに登録'), 'start did not finish');
assert.equal(confirm.checked, false, 'successful start clears confirmation');
assert.equal(reason.value, '', 'successful start clears the editable reason so a later restart needs a new entry');
assert.equal(resend.disabled, false, 'active disabled recovery permits resend');
assert.equal(reason.disabled, true, 'active resend does not accept a new reason');
assert.equal(resend.disabled, false, 'resend is independent of a fresh reason');
resend.emit('click');
await until(() => resends === 1, 'resend did not finish');
assert.equal(confirm.checked, false, 'successful resend clears confirmation');
assert.equal(reason.value, '', 'resend retains its audit reason server-side without leaving a replacement in the editor');

// Let a previously checked box survive until the active recovery expires; the timer must clear it.
currentTarget = { id: 'target-user', displayName: 'Target', maskedRecipient: 't***@example.test', enabled: false,
    recovering: true, operationId: 'expired-operation', expiresAt: new Date(Date.now() + 1000).toISOString() };
activeRoot.elements.get('recovery-admin-lookup-form').emit('submit');
await until(() => !activeRoot.elements.get('recovery-admin-target-summary').hidden && resend.disabled === false, 'active expiring target did not load');
confirm.checked = true; confirm.emit('change');
await until(() => activeRoot.elements.get('recovery-admin-target-recovery').textContent.includes('期限切れ') && !reason.disabled,
    'expiry transition did not arrive');
assert.equal(confirm.checked, false, 'expiry clears stale confirmation');
assert.equal(start.disabled, true, 'expired restart requires fresh confirmation');
assert.equal(resend.disabled, true, 'expired operation cannot be resent');
reason.value = ' xy '; reason.emit('input');
assert.equal(start.disabled, true, 'reason alone cannot authorize restart');
reason.value = 'xyz'; reason.emit('input');
assert.equal(start.disabled, true, 'exactly three trimmed characters are accepted but confirmation is still required');
confirm.checked = true; confirm.emit('change');
assert.equal(start.disabled, false, 'fresh confirmation enables expired restart');
activeRoot.elements.get('recovery-admin-actions-form').emit('submit');
await until(() => starts === 2, 'expired restart did not finish');
assert.equal(confirm.checked, false, 'successful expired restart clears confirmation');
assert.deepEqual(chronology, ['admin-options', 'native-get', 'admin-start', 'resend-options', 'native-get', 'resend', 'admin-options', 'native-get', 'admin-start']);
assert.ok(calls.some(call => call.url.pathname === '/api/passkey-recovery-admin-context' && call.method === 'GET' && call.url.search === ''));
await admin.cleanup(activeRoot);

// Anonymous recovery strips the fragment before the first request and keeps the grant out of URLs.
currentUrl = new URL('https://sem.test/recovery#op=operation-public&token=secret-fragment');
history.replacements = [];
activeRoot = makeRoot('recovery-root', ['recovery-status', 'recovery-error', 'recovery-grant-panel', 'recovery-expiry', 'recovery-register', 'recovery-complete', 'recovery-login-link']);
calls = []; chronology = [];
installFetch(call => {
    const { url, method, init } = call;
    if (url.pathname === '/api/csrf') return json({ token: 'csrf-public' });
    assert.equal(url.search, '', 'public endpoint must not receive query parameters');
    assert.equal(currentUrl.hash, '', 'recovery fragment must be removed before any request');
    assert.equal(init.credentials, 'same-origin'); assert.equal(init.mode, 'same-origin');
    assert.equal(init.cache, 'no-store'); assert.equal(init.redirect, 'error');
    assert.equal(init.headers['X-CSRF-TOKEN'], 'csrf-public');
    if (url.pathname === '/api/passkey-recovery-confirm' && method === 'POST') {
        assert.deepEqual(JSON.parse(init.body), { operationId: 'operation-public', token: 'secret-fragment' });
        chronology.push('confirm');
        return json({ userId: 'target-user', grant: 'memory-grant', expiresAt: new Date(Date.now() + 4 * 60_000).toISOString() });
    }
    if (url.pathname === '/api/passkey-recovery-options') {
        assert.deepEqual(JSON.parse(init.body), { userId: 'target-user', grant: 'memory-grant' });
        chronology.push('options'); return json({ publicKey: { challenge: 'Ag', user: { id: 'Aw' } } });
    }
    if (url.pathname === '/api/passkey-recovery-complete') {
        const body = JSON.parse(init.body);
        assert.equal(body.userId, 'target-user'); assert.equal(body.grant, 'memory-grant');
        assert.equal(typeof body.credential, 'string'); chronology.push('complete');
        return json({ message: '新しいパスキーを登録しました。' });
    }
    assert.fail(`Unexpected public request: ${method} ${url}`);
});
const publicModule = await moduleFor(publicSource, 'public');
await publicModule.init(activeRoot);
await until(() => !activeRoot.elements.get('recovery-register').disabled, 'public confirmation did not enable registration');
assert.deepEqual(history.replacements, ['/recovery']);
assert.equal(calls.some(call => call.url.href.includes('secret-fragment') || call.url.href.includes('memory-grant')), false);
activeRoot.elements.get('recovery-register').emit('click');
await until(() => !activeRoot.elements.get('recovery-login-link').hidden, 'new-key registration did not finish');
assert.deepEqual(chronology, ['confirm', 'options', 'native-create', 'complete']);
assert.equal(activeRoot.elements.get('recovery-grant-panel').hidden, true);
assert.match(activeRoot.elements.get('recovery-status').textContent, /新しいパスキーを登録しました/);
await publicModule.cleanup(activeRoot);

// A short server expiry clears in-memory grant and disables the new-key action.
currentUrl = new URL('https://sem.test/recovery#op=short-operation&token=short-token');
history.replacements = [];
activeRoot = makeRoot('recovery-root', ['recovery-status', 'recovery-error', 'recovery-grant-panel', 'recovery-expiry', 'recovery-register', 'recovery-complete', 'recovery-login-link']);
calls = [];
installFetch(call => {
    const { url, method, init } = call;
    if (url.pathname === '/api/csrf') return json({ token: 'csrf-expiry' });
    assert.equal(url.search, ''); assert.equal(currentUrl.hash, '');
    if (url.pathname === '/api/passkey-recovery-confirm' && method === 'POST')
        return json({ userId: 'target-user', grant: 'short-grant', expiresAt: new Date(Date.now() + 80).toISOString() });
    assert.fail(`Unexpected expiry-test request: ${method} ${url}`);
});
await publicModule.init(activeRoot);
await until(() => activeRoot.elements.get('recovery-register').disabled === false, 'short-lived grant was not enabled');
await until(() => activeRoot.elements.get('recovery-register').disabled === true && activeRoot.elements.get('recovery-status').textContent.includes('期限が切れ'), 'expiry timer did not clear grant');
assert.equal(activeRoot.elements.get('recovery-grant-panel').hidden, true);
await publicModule.cleanup(activeRoot);

console.log('Passkey recovery UI synthetic transport checks passed.');
