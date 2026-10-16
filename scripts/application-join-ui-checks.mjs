import assert from 'node:assert/strict';
import { readFile } from 'node:fs/promises';
import { runInNewContext } from 'node:vm';
import { resolve } from 'node:path';

const base = resolve('src/Sem.Web');
const razor = await readFile(resolve(base, 'Components/Pages/Join.razor'), 'utf8');
const auth = await readFile(resolve(base, 'Components/Pages/Auth.razor'), 'utf8');
const source = await readFile(resolve(base, 'wwwroot/join.js'), 'utf8');

assert.match(razor, /@page "\/join"/);
assert.match(razor, /@attribute \[AllowAnonymous\]/);
assert.doesNotMatch(razor, /@rendermode/i);
assert.match(razor, /<script src="\/join\.js" defer><\/script>/);
assert.match(razor, /id="join-module-error"[^>]*role="alert"/);
assert.match(razor, /id="join-register-fields" disabled/);
assert.match(razor, /id="join-register-submit" type="submit" disabled/);
assert.match(razor, /id="join-confirm-fields" disabled/);
assert.match(razor, /id="join-confirm-submit" type="submit" disabled/);
assert.match(razor, /id="join-passkey-fields" disabled/);
assert.match(razor, /id="join-passkey-register" type="submit" disabled/);
const inputTags = razor.match(/<input\b[^>]*>/gu) || [];
assert.ok(inputTags.length >= 4);
assert.ok(inputTags.every(tag => !/\sname\s*=/u.test(tag)), 'SSR inputs must have no name attributes');
assert.match(auth, /href="\/join" data-enhance-nav="false"/);
assert.doesNotMatch(source, /\.innerHTML\b|localStorage|sessionStorage|console\./u);
assert.match(source, /history\.replaceState\(window\.history\.state, '', window\.location\.pathname\)/);
assert.match(source, /request\('\/api\/join\/register', 'POST', payload, JOIN_REQUEST_LIMIT/);
assert.match(source, /request\('\/api\/join\/confirm', 'POST', body, JOIN_REQUEST_LIMIT/);
assert.match(source, /request\('\/api\/passkey-options', 'POST', \{\}/);
assert.match(source, /request\('\/api\/passkey-complete', 'POST', body, PASSKEY_REQUEST_LIMIT/);
assert.match(source, /beforeSend:\s*currentOperation => verifySession/u);

const ids = [
    'join-page', 'join-title', 'join-module-error', 'join-status', 'join-error',
    'join-register-form', 'join-register-fields', 'join-invite', 'join-name',
    'join-email', 'join-register-submit', 'join-confirm-form', 'join-confirm-fields',
    'join-confirm-submit', 'join-passkey-form', 'join-passkey-fields',
    'join-enrollment-consent', 'join-passkey-register', 'join-reconcile',
    'join-workspace-note', 'join-workspace-link'
];

class FakeElement {
    constructor(id) {
        this.id = id;
        this.textContent = '';
        this.value = '';
        this.checked = false;
        this.disabled = true;
        this.hidden = ['join-confirm-form', 'join-passkey-form', 'join-reconcile',
            'join-workspace-note', 'join-workspace-link'].includes(id);
        this.isConnected = true;
        this.listeners = new Map();
    }
    addEventListener(type, listener) { this.listeners.set(type, listener); }
    removeEventListener(type, listener) {
        if (this.listeners.get(type) === listener) this.listeners.delete(type);
    }
    dispatch(type) {
        let prevented = false;
        this.listeners.get(type)?.({ target: this, preventDefault: () => { prevented = true; } });
        return prevented;
    }
}

class FakeRoot {
    constructor() {
        this.isConnected = true;
        this.nodes = new Map(ids.map(id => [id, new FakeElement(id)]));
        this.listeners = new Map();
    }
    querySelector(selector) { return this.nodes.get(selector.slice(1)) ?? null; }
    addEventListener(type, listener) { this.listeners.set(type, listener); }
}

const wait = ms => new Promise(resolveWait => setTimeout(resolveWait, ms));
async function until(predicate, description) {
    for (let count = 0; count < 300; count++) {
        if (predicate()) return;
        await wait(5);
    }
    assert.fail(`timed out waiting for ${description}`);
}

function json(value, status = 200) {
    return new Response(JSON.stringify(value), {
        status,
        headers: { 'Content-Type': 'application/json; charset=utf-8' }
    });
}

function launch({ hash = '', search = '', rejectRegister = false, initialStatus = { user: null, enrolling: false } } = {}) {
    const root = new FakeRoot();
    const location = { origin: 'https://sem.example', pathname: '/join', search, hash, hostname: 'sem.example' };
    const calls = [];
    const registerBodies = [];
    const confirmBodies = [];
    const passkeyOptionBodies = [];
    const passkeyCompleteBodies = [];
    const state = { user: initialStatus.user, enrolling: initialStatus.enrolling };
    const native = { created: 0, parsed: [] };
    const publicKeyOptions = {
        challenge: 'AQ',
        rp: { name: 'SEM', id: 'sem.example' },
        user: { id: 'AQ', name: 'pending@example.test', displayName: 'Pending user' },
        pubKeyCredParams: [{ type: 'public-key', alg: -7 }],
        timeout: 30_000,
        attestation: 'none'
    };
    class FakePublicKeyCredential {}
    FakePublicKeyCredential.parseCreationOptionsFromJSON = value => {
        native.parsed.push(value);
        return {
            challenge: new ArrayBuffer(1),
            rp: value.rp,
            user: { id: new ArrayBuffer(1), name: value.user.name, displayName: value.user.displayName },
            timeout: value.timeout
        };
    };

    const window = {
        location,
        history: {
            state: null,
            replaceState(_state, _title, path) {
                location.pathname = path;
                location.search = '';
                location.hash = '';
            }
        },
        fetch: async (path, options) => {
            assert.equal(location.hash, '', 'the fragment must be removed before any fetch');
            assert.equal(location.search, '', 'query values must be removed before any fetch');
            assert.equal(typeof path, 'string');
            assert.equal(new URL(path, location.origin).search, '', 'requests must not carry query strings');
            assert.equal(new URL(path, location.origin).hash, '', 'requests must not carry fragments');
            assert.equal(options.mode, 'same-origin');
            assert.equal(options.credentials, 'same-origin');
            assert.equal(options.cache, 'no-store');
            assert.equal(options.redirect, 'error');
            calls.push({ path, options });

            if (path === '/api/csrf' && options.method === 'GET') return json({ token: 'csrf-secret' });
            if (path === '/api/status' && options.method === 'GET') return json({ ...state });
            if (options.method !== 'POST') assert.fail(`unexpected request method for ${path}`);
            assert.equal(options.headers.get('X-CSRF-TOKEN'), 'csrf-secret');
            assert.equal(options.headers.get('Content-Type'), 'application/json');
            const body = JSON.parse(options.body);
            assert.ok(new TextEncoder().encode(options.body).byteLength <=
                (path.startsWith('/api/join/') || path === '/api/passkey-options' ? 4096 : 65_536));
            if (path === '/api/join/register') {
                registerBodies.push(body);
                if (rejectRegister) throw new TypeError('synthetic connection interruption');
                return json({ message: 'queued' });
            }
            if (path === '/api/join/confirm') {
                confirmBodies.push(body);
                state.enrolling = true;
                return json({ message: 'confirmed' });
            }
            if (path === '/api/passkey-options') {
                passkeyOptionBodies.push(body);
                return json(publicKeyOptions);
            }
            if (path === '/api/passkey-complete') {
                passkeyCompleteBodies.push(body);
                state.user = { id: 'new-user-id' };
                state.enrolling = false;
                return json({ message: 'complete' });
            }
            assert.fail(`unexpected POST endpoint: ${path}`);
        },
        setTimeout,
        clearTimeout,
        addEventListener() {},
        atob,
        btoa,
        navigator: {
            credentials: {
                create: async ({ publicKey }) => {
                    native.created++;
                    assert.equal(publicKey.challenge instanceof ArrayBuffer, true);
                    return {
                        toJSON: () => ({
                            id: 'credential-id',
                            rawId: 'AQ',
                            type: 'public-key',
                            response: { clientDataJSON: 'AQ', attestationObject: 'AQ', transports: [] }
                        })
                    };
                }
            }
        },
        PublicKeyCredential: FakePublicKeyCredential,
        locationReplace: null
    };
    window.location.replace = value => { window.locationReplace = value; };
    const document = {
        getElementById: id => id === 'join-page' ? root : root.nodes.get(id) ?? null
    };
    const context = {
        window, document, URL, URLSearchParams, Headers, TextEncoder, TextDecoder,
        AbortController, DOMException, ArrayBuffer, Uint8Array, Response,
        setTimeout, clearTimeout
    };
    runInNewContext(source, context);
    return {
        root, location, calls, registerBodies, confirmBodies, passkeyOptionBodies,
        passkeyCompleteBodies, state, native, window, publicKeyOptions
    };
}

// Invite fragment is removed before any request; the explicitly submitted body has only the agreed fields.
{
    const app = launch({ hash: '#invite=invite-secret', search: '?returnUrl=%2Fworkspace' });
    await until(() => app.root.nodes.get('join-status').textContent.includes('招待コードと登録情報'), 'registration initialization');
    assert.equal(app.root.nodes.get('join-invite').value, 'invite-secret');
    assert.equal(app.root.nodes.get('join-register-fields').disabled, false);
    assert.equal(app.registerBodies.length, 0, 'page initialization must not submit registration');
    app.root.nodes.get('join-name').value = 'Sample User';
    app.root.nodes.get('join-email').value = 'sample@example.test';
    assert.equal(app.root.nodes.get('join-register-form').dispatch('submit'), true);
    await until(() => app.root.nodes.get('join-status').textContent.includes('確認メールの送信依頼を受け付けました'), 'queued registration result');
    assert.deepEqual(Object.keys(app.registerBodies[0]).sort(), ['email', 'invite', 'name']);
    assert.deepEqual(app.registerBodies[0], {
        invite: 'invite-secret',
        name: 'Sample User',
        email: 'sample@example.test'
    });
    assert.equal(app.calls.find(call => call.path === '/api/join/register').options.headers.get('X-CSRF-TOKEN'), 'csrf-secret');
}

// A registration POST with an unknown transport outcome is never replayed by read-only reconciliation.
{
    const app = launch({ rejectRegister: true });
    await until(() => app.root.nodes.get('join-register-fields').disabled === false, 'anonymous registration readiness');
    app.root.nodes.get('join-invite').value = 'invite-code';
    app.root.nodes.get('join-name').value = 'Sample User';
    app.root.nodes.get('join-email').value = 'sample@example.test';
    app.root.nodes.get('join-register-form').dispatch('submit');
    await until(() => app.root.nodes.get('join-status').textContent.includes('登録依頼の結果を確認できません'), 'unknown registration lock');
    assert.equal(app.root.nodes.get('join-reconcile').hidden, false);
    assert.equal(app.registerBodies.length, 1);
    app.root.nodes.get('join-reconcile').dispatch('click');
    await until(() => app.root.nodes.get('join-status').textContent.includes('成立は判定できず'), 'read-only reconciliation');
    assert.equal(app.registerBodies.length, 1, 'manual status reads must not replay POST');
    assert.equal(app.root.nodes.get('join-register-fields').disabled, true);
    assert.equal(app.root.nodes.get('join-register-form').hidden, true);
}

// Confirmation is an explicit click; only then is the native passkey creation action available.
{
    const app = launch({ hash: '#token=confirmation-secret' });
    await until(() => app.root.nodes.get('join-confirm-fields').disabled === false, 'confirmation readiness');
    assert.equal(app.confirmBodies.length, 0, 'loading a token must not auto-confirm it');
    assert.equal(app.native.created, 0, 'loading the page must never open WebAuthn');
    assert.equal(app.root.nodes.get('join-confirm-submit').disabled, false);
    app.root.nodes.get('join-confirm-form').dispatch('submit');
    await until(() => app.root.nodes.get('join-passkey-form').hidden === false, 'enrollment after explicit confirmation');
    assert.deepEqual(app.confirmBodies, [{ token: 'confirmation-secret' }]);
    assert.equal(app.passkeyOptionBodies.length, 0, 'confirmation alone must not start WebAuthn');
    assert.equal(app.native.created, 0);
    assert.equal(app.root.nodes.get('join-passkey-register').disabled, true);

    app.root.nodes.get('join-enrollment-consent').checked = true;
    app.root.nodes.get('join-enrollment-consent').dispatch('change');
    assert.equal(app.root.nodes.get('join-passkey-register').disabled, false);
    app.root.nodes.get('join-passkey-form').dispatch('submit');
    await until(() => app.window.locationReplace === '/workspace', 'normal login and workspace navigation');
    assert.equal(app.native.created, 1);
    assert.deepEqual(app.passkeyOptionBodies, [{}]);
    assert.equal(app.native.parsed.length, 1);
    assert.deepEqual(Object.keys(app.native.parsed[0]).sort(), Object.keys(app.publicKeyOptions).sort(),
        'the passkey-options response must be passed directly, not wrapped as a sign-in response');
    assert.deepEqual(Object.keys(app.passkeyCompleteBodies[0]), ['credential']);
    assert.equal(typeof app.passkeyCompleteBodies[0].credential, 'string');
    assert.equal(app.location.hash, '');
    assert.equal(app.location.search, '');
}

console.log('Application join UI checks passed.');