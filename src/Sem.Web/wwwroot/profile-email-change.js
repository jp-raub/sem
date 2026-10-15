const API = '/api/';
const JSON_LIMIT = 256 * 1024;
const CSRF_LIMIT = 64 * 1024;
const REQUEST_TIMEOUT_MS = 25_000;
const CREDENTIAL_TIMEOUT_MS = 4 * 60_000;
const OPERATION_ID = /^[A-Za-z0-9_-]{1,128}$/;

let pending = null;
let actorId = null;
let writeOutcomeUnknown = false;
let busy = false;
let ownerGeneration = 0;
let authGeneration = 0;
let statusGeneration = 0;
let ownerRoot = null;
const activeRequests = new Set();
const activeCredentials = new Set();

const $ = id => document.getElementById(id);
const isObject = value => value !== null && typeof value === 'object' && !Array.isArray(value);
const currentOwner = mine => mine === ownerGeneration && ownerRoot?.isConnected &&
    ownerRoot === $('profile-email-change');

export function cleanup() {
    ownerGeneration++;
    authGeneration++;
    statusGeneration++;
    for (const controller of activeRequests) controller.abort();
    for (const controller of activeCredentials) controller.abort();
    activeRequests.clear();
    activeCredentials.clear();
    pending = null;
    actorId = null;
    writeOutcomeUnknown = false;
    busy = false;
    ownerRoot = null;
}

function setError(message = '') {
    const node = $('profile-email-change-error');
    if (node) node.textContent = message;
}

function setStatus(message) {
    const node = $('profile-email-change-status');
    if (node) node.textContent = message;
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
        const error = new Error('CSRF確認に失敗しました。ページを再読み込みしてください。');
        error.status = response.status;
        throw error;
    }
    const value = await boundedJson(response, CSRF_LIMIT);
    if (!isObject(value) || typeof value.token !== 'string' || !value.token || value.token.length > 8192)
        throw new Error('CSRFトークンを確認できません。');
    return value.token;
}

function validActorId(value) {
    return typeof value === 'string' && value.length > 0 && value.length <= 256 &&
        !/[\u0000-\u001f\u007f]/.test(value);
}

function abortCredentials() {
    for (const controller of activeCredentials) controller.abort();
}

function markWriteOutcomeUnknown() {
    writeOutcomeUnknown = true;
    render();
}

function invalidateActor() {
    ownerGeneration++;
    authGeneration++;
    statusGeneration++;
    abortCredentials();
    actorId = null;
    pending = null;
    writeOutcomeUnknown = false;
    busy = false;
    const input = $('profile-email-change-address');
    if (input) input.value = '';
    setError('ログイン状態またはアカウントが変わりました。状況を再取得してください。');
    setStatus('アカウントを再確認してください。');
    render();
}

function retryAfterMs(header) {
    if (!header) return null;
    const seconds = Number(header);
    if (Number.isFinite(seconds) && seconds >= 0)
        return Math.min(seconds * 1000, 24 * 60 * 60 * 1000);
    const date = Date.parse(header);
    if (!Number.isFinite(date)) return null;
    return Math.max(0, Math.min(date - Date.now(), 24 * 60 * 60 * 1000));
}

function requestFailureMessage(error, fallback) {
    if (error?.status === 429 || Number.isFinite(error?.retryAfterMs)) {
        const wait = Number.isFinite(error.retryAfterMs)
            ? `少なくとも${Math.ceil(error.retryAfterMs / 1000)}秒待ってください。`
            : 'しばらく待ってください。';
        return `${wait}その後に状況を再取得してください。保留中なら再認証し、なければ変更申請を最初からやり直してください。`;
    }
    return error?.message || fallback;
}

async function jsonRequest(path, body = null) {
    const mine = ownerGeneration;
    const authMine = authGeneration;
    const isPost = body !== null;
    const expectedActor = isPost ? actorId : null;
    if (isPost && !validActorId(expectedActor))
        throw new Error('アカウントを確認できません。状況を再取得してください。');
    const controller = new AbortController();
    const timer = setTimeout(() => controller.abort(), REQUEST_TIMEOUT_MS);
    activeRequests.add(controller);
    let postStarted = false;
    try {
        const headers = { Accept: 'application/json' };
        const init = {
            method: body === null ? 'GET' : 'POST',
            credentials: 'same-origin',
            mode: 'same-origin',
            cache: 'no-store',
            redirect: 'error',
            headers,
            signal: controller.signal
        };
        if (body !== null) {
            const token = await csrf(controller.signal);
            if (!currentOwner(mine) || authGeneration !== authMine || actorId !== expectedActor)
                throw new Error('アカウントが変わりました。状況を再取得してください。');
            const serializedBody = JSON.stringify(body);
            if (new TextEncoder().encode(serializedBody).byteLength > JSON_LIMIT)
                throw new Error('送信内容が大きすぎます。');
            headers['Content-Type'] = 'application/json';
            headers['X-CSRF-TOKEN'] = token;
            init.body = serializedBody;
        }
        const url = new URL(API + path, location.origin);
        if (isPost) {
            url.searchParams.delete('expectedActor');
            url.searchParams.append('expectedActor', expectedActor);
        }
        if (!currentOwner(mine) ||
            (isPost && (authGeneration !== authMine || actorId !== expectedActor)))
            throw new Error('アカウントまたは画面が変わりました。状況を再取得してください。');
        postStarted = isPost;
        const response = await fetch(url, init);
        if (!response.ok || response.redirected) {
            if ((response.status === 401 || response.status === 403) &&
                currentOwner(mine) && authGeneration === authMine)
                invalidateActor();
            await response.body?.cancel();
            const error = new Error(`処理に失敗しました（${response.status}）。`);
            error.status = response.status;
            error.retryAfterMs = retryAfterMs(response.headers.get('Retry-After'));
            throw error;
        }
        if (!currentOwner(mine) ||
            (isPost && (authGeneration !== authMine || actorId !== expectedActor))) {
            await response.body?.cancel();
            throw new Error('アカウントまたは画面が変わりました。状況を再取得してください。');
        }
        const value = await boundedJson(response, JSON_LIMIT);
        if (!currentOwner(mine) ||
            (isPost && (authGeneration !== authMine || actorId !== expectedActor))) {
            if (isPost && currentOwner(mine) && authGeneration === authMine)
                markWriteOutcomeUnknown();
            throw new Error('アカウントまたは画面が変わりました。状況を再取得してください。');
        }
        return value;
    } catch (error) {
        if ((error?.status === 401 || error?.status === 403) &&
            currentOwner(mine) && authGeneration === authMine)
            invalidateActor();
        else if (isPost && postStarted && currentOwner(mine) && authGeneration === authMine) {
            markWriteOutcomeUnknown();
        }
        throw error;
    } finally {
        clearTimeout(timer);
        activeRequests.delete(controller);
    }
}

function validOperationId(value) {
    return typeof value === 'string' && value.length <= 128 && OPERATION_ID.test(value);
}

function validOptionalDate(value, allowNull) {
    if (allowNull && value === null) return true;
    return typeof value === 'string' && value.length <= 64 && Number.isFinite(Date.parse(value));
}

function parseStatus(value) {
    if (!isObject(value) || !validActorId(value.actorId) ||
        !(value.pending === null || isObject(value.pending)))
        throw new Error('メール変更状況の応答が不正です。');
    if (value.pending === null) return { actorId: value.actorId, pending: null };
    const item = value.pending;
    if (!validOperationId(item.operationId) ||
        !validOptionalDate(item.expiresAt, false) ||
        !validOptionalDate(item.lastSentAt, true) ||
        typeof item.confirmationSent !== 'boolean')
        throw new Error('保留中のメール変更情報を確認できません。');
    return {
        actorId: value.actorId,
        pending: {
            operationId: item.operationId,
            expiresAt: item.expiresAt,
            lastSentAt: item.lastSentAt,
            confirmationSent: item.confirmationSent
        }
    };
}

function render() {
    const form = $('profile-email-change-form');
    const input = $('profile-email-change-address');
    const begin = $('profile-email-change-begin');
    const pendingRoot = $('profile-email-change-pending');
    const pendingStatus = $('profile-email-change-pending-status');
    const resend = $('profile-email-change-resend');
    const cancel = $('profile-email-change-cancel');
    const refresh = $('profile-email-change-refresh');
    if (!form || !input || !begin || !pendingRoot || !pendingStatus || !resend || !cancel || !refresh) return;

    const hasActor = validActorId(actorId);
    const hasPending = pending !== null;
    const writesLocked = writeOutcomeUnknown;
    form.hidden = hasPending;
    pendingRoot.hidden = !hasPending;
    if (hasPending) {
        pendingStatus.textContent = pending.confirmationSent
            ? '確認メールを送信済みです。受信したメールのリンクから変更を確定してください。'
            : 'メール変更が保留中です。確認メールを送るにはパスキーで再認証してください。';
        resend.textContent = pending.confirmationSent
            ? '再認証して確認メールを再送'
            : '再認証して確認メールを送信';
    }
    input.disabled = busy || hasPending || !hasActor || writesLocked;
    begin.disabled = busy || hasPending || !hasActor || writesLocked;
    resend.disabled = busy || !hasPending || !hasActor || writesLocked;
    cancel.disabled = busy || !hasPending || !hasActor || writesLocked;
    refresh.disabled = busy;
}

async function refreshStatus({ quiet = false } = {}) {
    if (busy) return;
    const mine = ownerGeneration;
    const requestGeneration = ++statusGeneration;
    busy = true;
    render();
    if (!quiet) {
        setError('');
        setStatus('変更状況を確認しています。');
    }
    try {
        const value = parseStatus(await jsonRequest('email-change-status'));
        if (!currentOwner(mine) || requestGeneration !== statusGeneration) return;
        applyStatus(value);
    } catch (error) {
        if (currentOwner(mine) && requestGeneration === statusGeneration)
            lockUnknownActor(error, quiet);
    } finally {
        if (ownerRoot?.isConnected) {
            busy = false;
            render();
        }
    }
}

function lockUnknownActor(error, quiet = false) {
    ownerGeneration++;
    authGeneration++;
    statusGeneration++;
    abortCredentials();
    actorId = null;
    pending = null;
    busy = false;
    const input = $('profile-email-change-address');
    if (input) input.value = '';
    if (!quiet) {
        setError(requestFailureMessage(error, 'メール変更状況を取得できません。'));
        setStatus('状況を確認できません。待機後に再取得してください。');
    }
    render();
}

function applyStatus(value) {
    if (actorId !== null && actorId !== value.actorId) {
        ownerGeneration++;
        authGeneration++;
        statusGeneration++;
        abortCredentials();
        busy = false;
        pending = null;
        const input = $('profile-email-change-address');
        if (input) input.value = '';
    }
    actorId = value.actorId;
    pending = value.pending;
    writeOutcomeUnknown = false;
    if (pending) $('profile-email-change-address').value = '';
    setError('');
    setStatus(pending
        ? (pending.confirmationSent
            ? '確認メールを送信済みです。'
            : 'メール変更が保留中です。')
        : '保留中のメール変更はありません。');
    render();
}
function decodeBase64Url(value) {
    if (typeof value !== 'string' || !/^[A-Za-z0-9_-]+$/.test(value) || value.length > 16_384)
        throw new Error('パスキーの認証オプションを確認できません。');
    const base64 = value.replace(/-/g, '+').replace(/_/g, '/');
    const padded = base64 + '='.repeat((4 - base64.length % 4) % 4);
    try {
        const binary = atob(padded);
        if (!binary.length) throw new Error();
        return Uint8Array.from(binary, character => character.charCodeAt(0));
    } catch {
        throw new Error('パスキーの認証オプションを確認できません。');
    }
}

function requestOptions(json) {
    if (!isObject(json) || typeof json.challenge !== 'string')
        throw new Error('パスキーの認証オプションを確認できません。');
    if (json.allowCredentials !== undefined &&
        (!Array.isArray(json.allowCredentials) || json.allowCredentials.length > 100))
        throw new Error('パスキーの認証オプションを確認できません。');
    try {
        if (typeof window.PublicKeyCredential.parseRequestOptionsFromJSON === 'function')
            return window.PublicKeyCredential.parseRequestOptionsFromJSON(json);
    } catch {
        throw new Error('パスキーの認証オプションを確認できません。');
    }
    const parsed = { ...json, challenge: decodeBase64Url(json.challenge) };
    if (json.allowCredentials) {
        parsed.allowCredentials = json.allowCredentials.map(item => {
            if (!isObject(item) || typeof item.id !== 'string')
                throw new Error('パスキーの認証オプションを確認できません。');
            return { ...item, id: decodeBase64Url(item.id) };
        });
    }
    return parsed;
}

function serializeCredential(credential) {
    if (typeof credential?.toJSON === 'function') return credential.toJSON();
    const encode = bytes => bytes
        ? btoa(String.fromCharCode(...new Uint8Array(bytes)))
            .replace(/\+/g, '-').replace(/\//g, '_').replace(/=+$/g, '')
        : null;
    const response = credential.response;
    const serializedResponse = { clientDataJSON: encode(response.clientDataJSON) };
    if (response.attestationObject) {
        serializedResponse.attestationObject = encode(response.attestationObject);
        serializedResponse.transports = response.getTransports?.() || [];
    } else {
        serializedResponse.authenticatorData = encode(response.authenticatorData);
        serializedResponse.signature = encode(response.signature);
        serializedResponse.userHandle = encode(response.userHandle);
    }
    return {
        id: credential.id,
        rawId: encode(credential.rawId),
        type: credential.type,
        authenticatorAttachment: credential.authenticatorAttachment,
        response: serializedResponse,
        clientExtensionResults: credential.getClientExtensionResults()
    };
}

function requireSuccessMessage(value) {
    if (!isObject(value) || typeof value.message !== 'string' ||
        value.message.length === 0 || value.message.length > 512) {
        markWriteOutcomeUnknown();
        throw new Error('処理結果の応答が不正です。状況を再取得してください。');
    }
}

async function authenticateAndSend(operationId, publicKey) {
    const mine = ownerGeneration;
    const authMine = authGeneration;
    const expectedActor = actorId;
    if (!validOperationId(operationId) || !isObject(publicKey))
        throw new Error('パスキーの認証オプションを確認できません。状況を再取得してください。');
    if (!('PublicKeyCredential' in window) || !navigator.credentials?.get)
        throw new Error('このブラウザーではパスキーを利用できません。');
    let credential;
    const controller = new AbortController();
    const options = requestOptions(publicKey);
    const timeout = Number.isFinite(options.timeout) && options.timeout > 0
        ? Math.min(options.timeout, CREDENTIAL_TIMEOUT_MS)
        : CREDENTIAL_TIMEOUT_MS;
    const timer = setTimeout(() => controller.abort(), timeout);
    activeCredentials.add(controller);
    try {
        credential = await navigator.credentials.get({ publicKey: options, signal: controller.signal });
    } catch (error) {
        if (!currentOwner(mine) || authGeneration !== authMine || actorId !== expectedActor) return;
        if (error?.name === 'NotAllowedError' || error?.name === 'AbortError')
            throw new Error('パスキー認証は完了しませんでした。必要なら再認証してください。');
        throw new Error('パスキー認証を完了できませんでした。');
    } finally {
        clearTimeout(timer);
        activeCredentials.delete(controller);
    }
    if (!credential) throw new Error('パスキー認証は完了しませんでした。');
    if (!currentOwner(mine) || authGeneration !== authMine || actorId !== expectedActor) return;
    const result = await jsonRequest('email-change-reauth', {
        operationId,
        credential: JSON.stringify(serializeCredential(credential))
    });
    if (!currentOwner(mine) || authGeneration !== authMine || actorId !== expectedActor) return;
    requireSuccessMessage(result);
    if (pending?.operationId === operationId) pending.confirmationSent = true;
    setStatus('再認証に成功しました。確認メールを送信しました。');
    render();
}

async function reconcileStatus() {
    const mine = ownerGeneration;
    const requestGeneration = ++statusGeneration;
    try {
        const value = parseStatus(await jsonRequest('email-change-status'));
        if (!currentOwner(mine) || requestGeneration !== statusGeneration) return;
        applyStatus(value);
    } catch (error) {
        if (currentOwner(mine) && requestGeneration === statusGeneration)
            lockUnknownActor(error);
    }
}

async function begin(event) {
    event.preventDefault();
    const mine = ownerGeneration;
    const authMine = authGeneration;
    const expectedActor = actorId;
    const form = $('profile-email-change-form');
    const input = $('profile-email-change-address');
    if (!form.reportValidity() || busy || pending || writeOutcomeUnknown || !validActorId(expectedActor)) return;
    const newEmail = input.value.trim();
    if (!newEmail) return;
    busy = true;
    render();
    setError('');
    setStatus('変更を開始しています。パスキーで再認証してください。');
    let operationId = null;
    try {
        const result = await jsonRequest('email-change-begin', { newEmail });
        if (!currentOwner(mine) || authGeneration !== authMine || actorId !== expectedActor) return;
        if (!isObject(result) || !validOperationId(result.operationId)) {
            markWriteOutcomeUnknown();
            await reconcileStatus();
            if (!currentOwner(mine)) return;
            throw new Error('開始結果を確認できません。状況を再取得してください。');
        }
        operationId = result.operationId;
        pending = {
            operationId,
            expiresAt: null,
            lastSentAt: null,
            confirmationSent: false
        };
        input.value = '';
        render();
        await authenticateAndSend(operationId, result.publicKey);
        if (!currentOwner(mine) || authGeneration !== authMine || actorId !== expectedActor) return;
    } catch (error) {
        if (!currentOwner(mine)) return;
        if (error?.status !== 401 && error?.status !== 403) await reconcileStatus();
        if (!currentOwner(mine)) return;
        setError(requestFailureMessage(error, 'メール変更を開始できませんでした。'));
    } finally {
        if (currentOwner(mine)) {
            busy = false;
            render();
        }
    }
}

async function reauthenticate() {
    const mine = ownerGeneration;
    const authMine = authGeneration;
    const expectedActor = actorId;
    if (busy || !pending || writeOutcomeUnknown || !validActorId(expectedActor)) return;
    const operationId = pending.operationId;
    busy = true;
    render();
    setError('');
    setStatus('パスキーの再認証を準備しています。');
    try {
        const result = await jsonRequest('email-change-reauth-options', { operationId });
        if (!currentOwner(mine) || authGeneration !== authMine || actorId !== expectedActor) return;
        if (!isObject(result) || result.operationId !== operationId || !isObject(result.publicKey)) {
            markWriteOutcomeUnknown();
            throw new Error('再認証オプションを確認できません。状況を再取得してください。');
        }
        setStatus('パスキーで再認証してください。');
        await authenticateAndSend(operationId, result.publicKey);
        if (!currentOwner(mine) || authGeneration !== authMine || actorId !== expectedActor) return;
    } catch (error) {
        if (!currentOwner(mine)) return;
        if (error?.status !== 401 && error?.status !== 403) await reconcileStatus();
        if (!currentOwner(mine)) return;
        setError(requestFailureMessage(error, '再認証または確認メール送信に失敗しました。'));
    } finally {
        if (currentOwner(mine)) {
            busy = false;
            render();
        }
    }
}

async function cancel() {
    const mine = ownerGeneration;
    const authMine = authGeneration;
    const expectedActor = actorId;
    if (busy || !pending || writeOutcomeUnknown || !validActorId(expectedActor)) return;
    const operationId = pending.operationId;
    busy = true;
    render();
    setError('');
    setStatus('メール変更を取り消しています。');
    try {
        const result = await jsonRequest('email-change-cancel', { operationId });
        if (!currentOwner(mine) || authGeneration !== authMine || actorId !== expectedActor) return;
        requireSuccessMessage(result);
        pending = null;
        setStatus('メール変更を取り消しました。');
        render();
    } catch (error) {
        if (!currentOwner(mine)) return;
        if (error?.status !== 401 && error?.status !== 403) await reconcileStatus();
        if (!currentOwner(mine)) return;
        setError(requestFailureMessage(error, '取り消し結果を確認できません。'));
    } finally {
        if (currentOwner(mine)) {
            busy = false;
            render();
        }
    }
}

export function init() {
    cleanup();
    ownerRoot = $('profile-email-change');
    if (!ownerRoot) return;
    $('profile-email-change-form')?.addEventListener('submit', begin);
    $('profile-email-change-resend')?.addEventListener('click', reauthenticate);
    $('profile-email-change-cancel')?.addEventListener('click', cancel);
    $('profile-email-change-refresh')?.addEventListener('click', () => refreshStatus());
    render();
    void refreshStatus();
}