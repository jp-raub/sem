// Synthetic VM check of the retained external-test compatibility input.
// No browser, WebAuthn, host, mail provider, or live API is used.
import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import { fileURLToPath } from 'node:url';
import vm from 'node:vm';

const source = readFileSync(new URL('../src/Sem.Web/wwwroot/sem.js', import.meta.url), 'utf8');
async function run(signedIn) {
    const code = 'A'.repeat(64);
    const nodes = new Map();
    const element = id => {
        if (!nodes.has(id)) nodes.set(id, { textContent: '', innerHTML: '', onclick: null });
        return nodes.get(id);
    };
    const calls = [];
    const context = {
        URLSearchParams, URL,
        location: new URL('https://local.invalid/?keep=1&passkeyAddToken=' + code),
        document: { getElementById: element, title: '' },
        history: {
            replaceState(_state, _title, path) {
                context.location = new URL(path, context.location.origin);
            },
        },
        async fetch(url, options = {}) {
            assert.equal(context.location.search, '?keep=1', 'legacy query is removed before the first API request');
            assert.ok(!String(url).includes(code), 'adapter API URL does not contain the token');
            calls.push({ url, options });
            if (url === '/api/csrf') return { ok: true, json: async () => ({ token: 'synthetic-antiforgery' }) };
            if (url === '/api/status') return {
                ok: true,
                json: async () => ({
                    siteName: 'Synthetic',
                    initialized: true,
                    user: signedIn ? { id: 'synthetic-owner' } : null,
                    emailConfigured: false,
                    pushConfigured: false,
                }),
            };
            assert.equal(url, '/api/passkey-add-confirm');
            // Intentionally fail the response: verify no automatic replay/reload follows.
            return { ok: false, json: async () => ({ error: 'synthetic rejection' }) };
        },
    };
    await new vm.Script(source, { filename: fileURLToPath(new URL('../src/Sem.Web/wwwroot/sem.js', import.meta.url)) })
        .runInNewContext(context, { timeout: 5000 });
    const posts = calls.filter(call => call.options.method === 'POST');
    if (!signedIn) {
        assert.equal(posts.length, 0, 'anonymous legacy link never confirms a code');
        assert.match(element('sem-error').textContent, /同じブラウザ/);
    } else {
        assert.equal(posts.length, 1, 'a legacy link has one guarded POST attempt and no replay on rejection');
        assert.equal(posts[0].options.headers['X-CSRF-TOKEN'], 'synthetic-antiforgery');
        assert.equal(posts[0].options.headers['Content-Type'], 'application/json');
        assert.deepEqual(JSON.parse(posts[0].options.body), { token: code });
        assert.equal(element('sem-error').textContent, 'synthetic rejection');
    }
    assert.equal(calls[0].url, '/api/csrf');
    assert.equal(calls[1].url, '/api/status');
}
await run(false);
await run(true);
console.log('Legacy passkey URL VM checks passed: early query removal, live-state gate, POST-only adapter input, CSRF, and no replay.');