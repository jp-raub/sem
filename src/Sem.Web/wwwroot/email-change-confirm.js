const JSON_LIMIT = 64 * 1024;
const CSRF_LIMIT = 64 * 1024;
const REQUEST_TIMEOUT_MS = 25_000;
const OPERATION_ID = /^[A-Za-z0-9_-]{1,128}$/;

const statusNode = document.getElementById('email-change-confirm-status');
const errorNode = document.getElementById('email-change-confirm-error');

// The confirmation secret is only accepted from the fragment and the whole
// fragment is erased from the visible URL before any network request begins.
let fragment = location.hash.startsWith('#') ? location.hash.slice(1) : '';
history.replaceState(null, '', location.pathname);
const fragmentParams = new URLSearchParams(fragment);
const operationIds = fragmentParams.getAll('op');
const tokens = fragmentParams.getAll('token');
const fragmentKeys = [...fragmentParams.keys()];
const operationId = operationIds.length === 1 ? operationIds[0] : null;
let token = tokens.length === 1 ? tokens[0] : null;
fragmentParams.delete('op');
fragmentParams.delete('token');
fragment = '';
const fragmentIsValid = fragmentKeys.length === 2 &&
    fragmentKeys.every(key => key === 'op' || key === 'token') &&
    operationIds.length === 1 && tokens.length === 1;
tokens.fill(''); tokens.length = 0;

function setStatus(value) {
    if (statusNode) statusNode.textContent = value;
}

function setError(value) {
    if (errorNode) errorNode.textContent = value;
}

function isObject(value) {
    return value !== null && typeof value === 'object' && !Array.isArray(value);
}

async function boundedJson(response, limit) {
    if (!(response.headers.get('Content-Type') || '').toLowerCase().startsWith('application/json'))
        throw new Error('JSON応答を確認できません。');
    const length = response.headers.get('Content-Length');
    if (length !== null && (!/^\d+$/.test(length) || Number(length) > limit))
        throw new Error('応答サイズが大きすぎます。');
    if (!response.body) throw new Error('応答本文を確認できません。');

    const reader = response.body.getReader();
    const chunks = [];
    let size = 0;
    try {
        for (;;) {
            const { done, value } = await reader.read();
            if (done) break;
            size += value.byteLength;
            if (size > limit) throw new Error('応答サイズが大きすぎます。');
            chunks.push(value);
        }
    } catch (error) {
        await reader.cancel().catch(() => {});
        throw error;
    }
    const bytes = new Uint8Array(size);
    let offset = 0;
    for (const chunk of chunks) {
        bytes.set(chunk, offset);
        offset += chunk.byteLength;
    }
    return JSON.parse(new TextDecoder('utf-8', { fatal: true }).decode(bytes));
}

async function csrf(signal) {
    const response = await fetch('/api/csrf', {
        method: 'GET',
        credentials: 'same-origin',
        mode: 'same-origin',
        cache: 'no-store',
        redirect: 'error',
        headers: { Accept: 'application/json' },
        signal
    });
    if (!response.ok || response.redirected) {
        await response.body?.cancel();
        throw new Error('CSRF確認に失敗しました。元のメールからリンクを開き直してください。');
    }
    const value = await boundedJson(response, CSRF_LIMIT);
    if (!isObject(value) || typeof value.token !== 'string' || !value.token || value.token.length > 8192)
        throw new Error('CSRFトークンを確認できません。');
    return value.token;
}

async function confirm(operationIdValue, tokenValue) {
    const controller = new AbortController();
    const timer = setTimeout(() => controller.abort(), REQUEST_TIMEOUT_MS);
    try {
        const csrfToken = await csrf(controller.signal);
        const body = JSON.stringify({ operationId: operationIdValue, token: tokenValue });
        if (new TextEncoder().encode(body).byteLength > JSON_LIMIT)
            throw new Error('送信内容が大きすぎます。');
        const response = await fetch('/api/email-change-confirm', {
            method: 'POST',
            credentials: 'same-origin',
            mode: 'same-origin',
            cache: 'no-store',
            redirect: 'error',
            headers: {
                Accept: 'application/json',
                'Content-Type': 'application/json',
                'X-CSRF-TOKEN': csrfToken
            },
            body,
            signal: controller.signal
        });
        if (!response.ok || response.redirected) {
            await response.body?.cancel();
            const error = new Error(`確認に失敗しました（${response.status}）。`);
            error.status = response.status;
            throw error;
        }
        const value = await boundedJson(response, JSON_LIMIT);
        if (!isObject(value) || typeof value.message !== 'string' ||
            value.message.length === 0 || value.message.length > 512)
            throw new Error('確認結果の応答が不正です。');
    } finally {
        clearTimeout(timer);
    }
}

async function run() {
    const validToken = typeof token === 'string' &&
        token.length >= 1 && token.length <= 4096 && !/[\u0000-\u0020\u007f]/.test(token);
    if (!fragmentIsValid || !OPERATION_ID.test(operationId || '') || !validToken) {
        token = null;
        setStatus('');
        setError('確認リンクが不正または不完全です。メールからリンクを開き直してください。');
        return;
    }
    setStatus('メールアドレスの変更を確認しています。');
    try {
        await confirm(operationId, token);
        token = null;
        setStatus('メールアドレスを確認しました。ログイン状態は発行されていません。');
    } catch {
        token = null;
        setStatus('');
        setError('確認結果を確定できませんでした。ログイン後に状況を確認するか、再認証して確認メールを再送してください。');
    }
}

void run();