import assert from 'node:assert/strict';
import { readFile } from 'node:fs/promises';
import vm from 'node:vm';
import { TextDecoder, TextEncoder } from 'node:util';

const root = new URL('../', import.meta.url);
const file = async path => readFile(new URL(path, root), 'utf8');
const razor = await file('src/Sem.Web/Components/Pages/Auth.razor');
const script = await file('src/Sem.Web/wwwroot/auth.js');
const redirect = await file('src/Sem.Web/Components/Account/Shared/RedirectToLogin.razor');
const workspace = await file('src/Sem.Web/Components/Pages/Workspace.razor');
const home = await file('src/Sem.Web/Components/Pages/Home.razor');
const endpoints = await file('src/Sem.Web/Runtime/SemEndpoints.cs');
const loginEndpoints = await file('src/Sem.Web/Runtime/ApplicationLoginEndpoints.cs');
const authStyles = await file('src/Sem.Web/Components/Pages/Auth.razor.css');

assert.match(razor, /@page "\/auth"/);
assert.match(razor, /PASSKEY SIGN IN/);
assert.match(razor, /id="auth-heading">また、一緒に音楽を。/);
assert.match(authStyles, /max-width: 440px/);
assert.match(authStyles, /var\(--appearance-surface/);
assert.match(authStyles, /forced-colors: active/);
assert.match(razor, /@attribute \[AllowAnonymous\]/);
assert.match(razor, /<form id="auth-form" method="post" action="\/auth"/);
assert.match(razor, /<fieldset id="auth-fields" disabled>/);
assert.match(razor, /<input id="auth-email"[^>]*type="email"[^>]*required/);
assert.doesNotMatch(razor, /<input[^>]+\bname=/i, 'SSR form must not name/serialize the email');
assert.match(razor, /id="auth-submit" type="submit" disabled/);
assert.match(razor, /id="auth-status" role="status"/);
assert.match(razor, /id="auth-error" role="alert"/);
assert.match(razor, /画面スクリプトを読み込めない場合/);
assert.match(razor, /<script src="\/auth\.js" defer><\/script>/);
assert.match(redirect, /GetLeftPart\(UriPartial\.Path\)/, 'redirect must drop query and fragment');
assert.match(redirect, /\/auth\?returnUrl=/);
assert.match(redirect, /forceLoad: true/, 'redirect must perform a full document navigation');
assert.match(redirect, /"api"/i);
assert.match(redirect, /TryDecodeCanonicalPath/);
assert.match(redirect, /for \(var i = 0; i < 5; i\+\+\)/);
assert.match(workspace, /id="workspace-auth-link" data-enhance-nav="false" href="\/auth\?returnUrl=%2Fworkspace"/, 'auth link must bypass enhanced navigation');
assert.doesNotMatch(workspace, /メール変更・認証復旧は後続実装/);
assert.match(home, /sem\.js/, 'external diagnostics must remain in Home');
assert.match(endpoints, /X-CSRF-TOKEN|ValidateRequestAsync/);
assert.match(endpoints, /action == "csrf"/);
assert.match(loginEndpoints, /login-options/);
assert.match(loginEndpoints, /isLogin/);
assert.match(loginEndpoints, /X-CSRF-TOKEN/);
assert.match(script, /cache: 'no-store'/);
assert.match(script, /redirect: 'error'/);
assert.match(script, /credentials: 'same-origin'/);
assert.match(script, /REQUEST_TIMEOUT_MS = 25_000/);
assert.match(script, /fatal: true/);
assert.match(script, /lockUnknownOutcome/);
assert.match(script, /api\('status', 'GET'/);
assert.doesNotMatch(script, /localStorage|sessionStorage|console\.(?:log|info|warn|error)/);

const webauthnOptions = {
    challenge: encodeUrl(new Uint8Array([1, 2, 3])),
    timeout: 600_000,
    rpId: 'sem.example.test',
    allowCredentials: [{ type: 'public-key', id: encodeUrl(new Uint8Array([4, 5])) }]
};

function encodeUrl(bytes) {
    return Buffer.from(bytes).toString('base64url');
}

function makeResponse(value, status = 200, headers = {}) {
    const body = value instanceof Uint8Array ? value : new TextEncoder().encode(JSON.stringify(value));
    return new Response(body, {
        status,
        headers: { 'content-type': 'application/json; charset=utf-8', ...headers }
    });
}

async function createHarness({
    search = '?returnUrl=%2Fworkspace',
    loginResponse = () => makeResponse({ message: 'ok' }),
    statusValue = { user: null },
    credentialResult,
    optionsResponse = () => makeResponse(webauthnOptions),
    csrfResponse = () => makeResponse({ token: 'csrf-test-token' })
} = {}) {
    class Element {
        constructor(id) {
            this.id = id;
            this.handlers = new Map();
            this.disabled = false;
            this.hidden = false;
            this.textContent = '';
            this.value = '';
            this.attributes = {};
        }
        addEventListener(type, handler) {
            const list = this.handlers.get(type) || [];
            list.push(handler);
            this.handlers.set(type, list);
        }
        setAttribute(name, value) { this.attributes[name] = value; }
        focus() { this.focused = true; }
        async emit(type, event = {}) {
            let result;
            for (const handler of this.handlers.get(type) || []) result = handler(event);
            return await result;
        }
    }
    const ids = ['auth-form', 'auth-fields', 'auth-email', 'auth-submit', 'auth-status', 'auth-error', 'auth-reconcile'];
    const elements = new Map(ids.map(id => [id, new Element(id)]));
    elements.get('auth-fields').disabled = true;
    elements.get('auth-submit').disabled = true;
    elements.get('auth-reconcile').hidden = true;
    elements.get('auth-reconcile').disabled = true;
    elements.get('auth-email').value = 'person@example.test';
    const calls = [];
    const redirects = [];
    const windowEvents = new Map();
    let credentialCalls = 0;
    const nativeCredential = credentialResult === undefined ? {
        id: 'credential-id', rawId: Uint8Array.from([7, 8]).buffer, type: 'public-key',
        response: {
            clientDataJSON: Uint8Array.from([9, 10]).buffer,
            authenticatorData: Uint8Array.from([11, 12]).buffer,
            signature: Uint8Array.from([13, 14]).buffer,
            userHandle: null
        },
        getClientExtensionResults: () => ({})
    } : credentialResult;
    const window = {
        location: {
            origin: 'https://sem.example.test', hostname: 'sem.example.test', search,
            replace: path => redirects.push(path), reload: () => { window.reloaded = true; }
        },
        navigator: {
            credentials: {
                get: async options => {
                    credentialCalls++;
                    window.lastPublicKeyOptions = options.publicKey;
                    window.lastSignal = options.signal;
                    return typeof nativeCredential === 'function' ? await nativeCredential(options) : nativeCredential;
                }
            }
        },
        PublicKeyCredential: {},
        atob,
        btoa,
        setTimeout,
        clearTimeout,
        addEventListener: (type, handler) => {
            const list = windowEvents.get(type) || [];
            list.push(handler);
            windowEvents.set(type, list);
        },
        fetch: async (url, init) => {
            calls.push({ url, init });
            if (url === '/api/csrf') return csrfResponse();
            if (url === '/api/login-options') return optionsResponse();
            if (url === '/api/login') return loginResponse(calls.at(-1));
            if (url === '/api/status') return makeResponse(statusValue);
            throw new Error(`Unexpected fetch URL: ${url}`);
        }
    };
    const document = { getElementById: id => elements.get(id) || null };
    const context = {
        window, document, URL, URLSearchParams, Headers, Response, AbortController, DOMException,
        TextEncoder, TextDecoder, Uint8Array, ArrayBuffer, Buffer, JSON, Math, Number, String,
        RegExp, Error, TypeError, Promise, Object, Array, Set, Map, console
    };
    vm.runInNewContext(script, context, { filename: 'auth.js' });
    for (let i = 0; i < 30 && elements.get('auth-fields').disabled; i++) await new Promise(resolve => setTimeout(resolve, 0));
    return {
        elements, calls, redirects, window, windowEvents,
        get credentialCalls() { return credentialCalls; }
    };
}

async function finishLogin(search = '?returnUrl=%2Fworkspace') {
    const harness = await createHarness({ search });
    assert.equal(harness.credentialCalls, 0, 'WebAuthn must not start before explicit submit');
    assert.equal(harness.elements.get('auth-fields').disabled, false, 'controls enable only after CSRF init');
    let prevented = false;
    await harness.elements.get('auth-form').emit('submit', { preventDefault() { prevented = true; } });
    assert.equal(prevented, true);
    assert.equal(harness.credentialCalls, 1, `native call skipped; calls=${harness.calls.map(call => call.url).join(',')} error=${harness.elements.get('auth-error').textContent}`);
    assert.deepEqual(harness.redirects, [expectedReturn(search)]);
    return harness;
}

function expectedReturn(search) {
    const raw = new URLSearchParams(search).getAll('returnUrl');
    if (raw.length !== 1) return '/workspace';
    const value = raw[0];
    if (!value.startsWith('/') || value.startsWith('//') || /[\\\u0000-\u001f\u007f-\u009f#]/u.test(value) ||
        /%(?![0-9a-f]{2})|%(?:2f|5c|0[0-9a-f]|1[0-9a-f]|7f)/iu.test(value)) return '/workspace';
    try {
        const target = new URL(value, 'https://sem.example.test');
        if (target.origin !== 'https://sem.example.test' || target.username || target.password || target.hash) return '/workspace';
        let canonical = target.pathname;
        for (let i = 0; i < 5; i++) {
            if (!canonical.startsWith('/') || canonical.startsWith('//') ||
                /[\\\u0000-\u001f\u007f-\u009f]/u.test(canonical) ||
                /%(?![0-9a-f]{2})|%(?:2f|5c|0[0-9a-f]|1[0-9a-f]|7f)/iu.test(canonical)) return '/workspace';
            const next = decodeURIComponent(canonical);
            if (next === canonical) break;
            canonical = next;
        }
        if (/%[0-9a-f]{2}/iu.test(canonical) || !canonical.startsWith('/') || canonical.startsWith('//') ||
            /[\\\u0000-\u001f\u007f-\u009f]/u.test(canonical)) return '/workspace';
        const first = canonical.split('/').filter(Boolean)[0]?.toLowerCase();
        if (['auth', 'api', 'account'].includes(first)) return '/workspace';
        return target.pathname;
    } catch { return '/workspace'; }
}

const happy = await finishLogin();
assert.deepEqual(happy.calls.map(call => call.url), ['/api/csrf', '/api/login-options', '/api/login']);
assert.deepEqual(happy.calls.map(call => call.init.method), ['GET', 'POST', 'POST']);
for (const call of happy.calls) {
    assert.equal(call.init.mode, 'same-origin');
    assert.equal(call.init.credentials, 'same-origin');
    assert.equal(call.init.cache, 'no-store');
    assert.equal(call.init.redirect, 'error');
}
const optionsCall = happy.calls.find(call => call.url === '/api/login-options');
assert.equal(optionsCall.init.headers.get('X-CSRF-TOKEN'), 'csrf-test-token');
assert.deepEqual(JSON.parse(optionsCall.init.body), { email: 'person@example.test' });
const loginCall = happy.calls.find(call => call.url === '/api/login');
const credentialBody = JSON.parse(loginCall.init.body);
const serialized = JSON.parse(credentialBody.credential);
assert.equal(serialized.id, 'credential-id');
assert.equal(serialized.type, 'public-key');
assert.equal(serialized.response.clientDataJSON, encodeUrl(new Uint8Array([9, 10])));
assert.equal(serialized.response.authenticatorData, encodeUrl(new Uint8Array([11, 12])));
assert.equal(serialized.response.signature, encodeUrl(new Uint8Array([13, 14])));
assert.deepEqual([...new Uint8Array(happy.window.lastPublicKeyOptions.challenge)], [1, 2, 3]);
assert.deepEqual([...new Uint8Array(happy.window.lastPublicKeyOptions.allowCredentials[0].id)], [4, 5]);
assert.equal(happy.window.lastPublicKeyOptions.timeout, 300_000);
assert.ok(happy.calls.every(call => !call.url.includes('person@example.test')));

const withQuery = await finishLogin('?returnUrl=%2Fevents%2Fabc%3Ftab%3Doverview');
assert.deepEqual(withQuery.redirects, ['/events/abc'], 'query must not be copied to the post-login destination');

for (const unsafe of [
    '//evil.example.test', 'https://evil.example.test/path', '/%2f%2fevil.example.test',
    '/%252f%252fevil.example.test', '/foo%5cbar', '/foo%0a', '/foo%85', '/foo\\bar',
    '/workspace#secret', '/auth', '/auth/again', '/api/status', '/Account/Login',
    '/%61pi/status', '/%2561pi/status', '/%252561pi/status', '/%25252525252561pi/status',
    'https://user:secret@evil.example.test/path'
]) {
    const harness = await finishLogin(`?${new URLSearchParams({ returnUrl: unsafe }).toString()}`);
    assert.deepEqual(harness.redirects, ['/workspace'], `unsafe return path should default: ${unsafe}`);
}

const cancelled = await createHarness({ credentialResult: null });
await cancelled.elements.get('auth-form').emit('submit', { preventDefault() {} });
assert.equal(cancelled.calls.filter(call => call.url === '/api/login').length, 0, 'canceled native prompt must not POST login');
assert.equal(cancelled.elements.get('auth-fields').disabled, false, 'cancelled prompt may be explicitly retried');

const noJsonCredential = {
    id: 'legacy-credential', rawId: Uint8Array.from([3, 4]).buffer, type: 'public-key',
    response: { clientDataJSON: Uint8Array.from([1]).buffer, authenticatorData: Uint8Array.from([2]).buffer, signature: Uint8Array.from([5]).buffer, userHandle: null },
    getClientExtensionResults: () => ({ appid: false })
};
const legacy = await createHarness({ credentialResult: noJsonCredential });
await legacy.elements.get('auth-form').emit('submit', { preventDefault() {} });
const legacyPayload = JSON.parse(JSON.parse(legacy.calls.find(call => call.url === '/api/login').init.body).credential);
assert.equal(legacyPayload.response.authenticatorData, encodeUrl(new Uint8Array([2])));
assert.equal(legacyPayload.clientExtensionResults.appid, false);

const unknown = await createHarness({ loginResponse: async () => { throw new TypeError('network failure'); } });
await unknown.elements.get('auth-form').emit('submit', { preventDefault() {} });
assert.equal(unknown.calls.filter(call => call.url === '/api/login').length, 1);
assert.equal(unknown.elements.get('auth-fields').disabled, true);
assert.equal(unknown.elements.get('auth-reconcile').hidden, false);
assert.match(unknown.elements.get('auth-error').textContent, /再送信は行いません/);
await unknown.elements.get('auth-reconcile').emit('click');
assert.equal(unknown.calls.filter(call => call.url === '/api/status').length, 1);
assert.equal(unknown.calls.filter(call => call.url === '/api/login').length, 1, 'status reconciliation must never replay login');
assert.equal(unknown.elements.get('auth-fields').disabled, true);
assert.match(unknown.elements.get('auth-status').textContent, /再送信していません/);

const reconciled = await createHarness({
    loginResponse: async () => { throw new TypeError('network failure'); },
    statusValue: { user: { id: 'user-123', displayName: 'Test' } }
});
await reconciled.elements.get('auth-form').emit('submit', { preventDefault() {} });
await reconciled.elements.get('auth-reconcile').emit('click');
assert.deepEqual(reconciled.redirects, ['/workspace']);
assert.equal(reconciled.calls.filter(call => call.url === '/api/login').length, 1);

const knownFailure = await createHarness({ loginResponse: () => makeResponse({ error: 'ログインできません。' }, 400) });
await knownFailure.elements.get('auth-form').emit('submit', { preventDefault() {} });
assert.equal(knownFailure.elements.get('auth-fields').disabled, false, 'definitive validation failure can be retried explicitly');
assert.equal(knownFailure.elements.get('auth-reconcile').hidden, true);


let resolvePendingCredential;
let pendingSignal;
const stale = await createHarness({ credentialResult: options => {
    pendingSignal = options.signal;
    return new Promise(resolve => { resolvePendingCredential = resolve; });
} });
const pendingSubmit = stale.elements.get('auth-form').handlers.get('submit')[0]({ preventDefault() {} });
for (let i = 0; i < 30 && !resolvePendingCredential; i++) await new Promise(resolve => setTimeout(resolve, 0));
assert.ok(resolvePendingCredential, 'native prompt should begin only after the explicit submit');
for (const handler of stale.windowEvents.get('pagehide') || []) handler();
assert.equal(pendingSignal.aborted, true, 'page disposal must abort the native credential request');
resolvePendingCredential({ id: 'late', toJSON: () => ({ id: 'late' }) });
await pendingSubmit;
assert.equal(stale.calls.filter(call => call.url === '/api/login').length, 0, 'stale credentials must not be posted');

const malformedUtf8 = await createHarness({ csrfResponse: () => makeResponse(Uint8Array.from([0xc3, 0x28])) });
assert.equal(malformedUtf8.elements.get('auth-fields').disabled, true, 'invalid UTF-8 must leave the SSR form disabled');
assert.match(malformedUtf8.elements.get('auth-error').textContent, /初期化できません/);

const tooLarge = await createHarness({ csrfResponse: () => makeResponse({ token: 'unused' }, 200, { 'content-length': '1048577' }) });
assert.equal(tooLarge.elements.get('auth-fields').disabled, true, 'oversized JSON must fail closed');

console.log('Application auth UI synthetic checks passed.');
