const API = '/api/';
const JSON_LIMIT = 1024 * 1024;
const CSRF_LIMIT = 64 * 1024;
const REQUEST_TIMEOUT_MS = 25_000;
const RECOVERY_WINDOW_MS = 5 * 60_000;
const ids = {
    root: 'recovery-root', status: 'recovery-status', error: 'recovery-error',
    panel: 'recovery-grant-panel', expiry: 'recovery-expiry', register: 'recovery-register',
    complete: 'recovery-complete', login: 'recovery-login-link'
};

let ownerRoot = null;
let ownerGeneration = 0;
let flowGeneration = 0;
let userId = null;
let grant = null;
let grantExpiresAt = 0;
let busy = false;
let complete = false;
let outcomeUnknown = false;
let expiryTimer = null;
let requests = new Set();
let credentials = new Set();
let listeners = null;
let navigationHandler = null;

const node = name => ownerRoot?.querySelector(`#${ids[name]}`) ?? null;
const isObject = value => value !== null && typeof value === 'object' && !Array.isArray(value);
const hasControls = value => typeof value !== 'string' || /[\u0000-\u001f\u007f]/u.test(value);
const validText = (value, max) => typeof value === 'string' && value.length > 0 && value.length <= max && !hasControls(value);

function currentOwner(mine = ownerGeneration) {
    return mine === ownerGeneration && ownerRoot?.isConnected === true &&
        document.getElementById(ids.root) === ownerRoot;
}

function setText(name, value) {
    const element = node(name);
    if (element) element.textContent = value;
}
function setHidden(name, hidden) {
    const element = node(name);
    if (element) element.hidden = hidden;
}
function render() {
    if (!currentOwner()) return;
    setHidden('panel', !grant || complete || outcomeUnknown);
    setHidden('complete', !complete);
    setHidden('login', !complete);
    const button = node('register');
    if (button) button.disabled = !grant || Date.now() >= grantExpiresAt || busy || complete || outcomeUnknown;
}
function clearTimer() {
    if (expiryTimer !== null) clearTimeout(expiryTimer);
    expiryTimer = null;
}
function abortPending() {
    for (const record of requests) record.controller.abort();
    for (const controller of credentials) controller.abort();
}
function clearGrant() {
    clearTimer();
    grant = null;
    userId = null;
    grantExpiresAt = 0;
    render();
}
function scheduleExpiry(mine, flowMine) {
    clearTimer();
    const delay = Math.max(0, grantExpiresAt - Date.now());
    expiryTimer = setTimeout(() => {
        expiryTimer = null;
        if (!currentOwner(mine) || flowGeneration !== flowMine || !grant) return;
        const completingWrite = [...requests].some(record => record.path === 'passkey-recovery-complete' && record.sent);
        flowGeneration++;
        abortPending();
        busy = false;
        outcomeUnknown = completingWrite;
        clearGrant();
        setText('status', completingWrite
            ? '登録結果が不明です。登録を自動再送しません。通常のログインを試すか、運用窓口へ連絡してください。'
            : '復旧許可の期限が切れました。復旧リンクを再度開いてください。');
        render();
    }, Math.min(delay, 2_147_000_000));
}
function retryAfterMs(header) {
    if (!header) return null;
    const seconds = Number(header);
    if (Number.isFinite(seconds) && seconds >= 0) return Math.min(seconds * 1000, 24 * 60 * 60 * 1000);
    const date = Date.parse(header);
    return Number.isFinite(date) ? Math.max(0, Math.min(date - Date.now(), 24 * 60 * 60 * 1000)) : null;
}
async function boundedJson(response, limit) {
    if (!(response.headers.get('Content-Type') || '').toLowerCase().startsWith('application/json'))
        throw new Error('JSON応答を確認できません。');
    const length = response.headers.get('Content-Length');
    if (length !== null && (!/^\d+$/.test(length) || Number(length) > limit))
        throw new Error('応答サイズが上限を超えています。');
    if (!response.body) throw new Error('応答本文を確認できません。');
    const reader = response.body.getReader();
    const chunks = [];
    let size = 0;
    try {
        for (;;) {
            const { done, value } = await reader.read();
            if (done) break;
            size += value.byteLength;
            if (size > limit) throw new Error('応答サイズが上限を超えています。');
            chunks.push(value);
        }
        const bytes = new Uint8Array(size);
        let offset = 0;
        for (const chunk of chunks) { bytes.set(chunk, offset); offset += chunk.byteLength; }
        return JSON.parse(new TextDecoder('utf-8', { fatal: true }).decode(bytes));
    } catch (error) {
        try { await reader.cancel(); } catch { /* already closed */ }
        throw error;
    }
}
async function csrf(record) {
    const response = await fetch(`${API}csrf`, {
        method: 'GET', credentials: 'same-origin', mode: 'same-origin', cache: 'no-store',
        redirect: 'error', headers: { Accept: 'application/json' }, signal: record.controller.signal
    });
    if (!response.ok || response.redirected) {
        await response.body?.cancel();
        const error = new Error(response.status === 401 || response.status === 403
            ? '復旧セッションを確認できません。リンクを再度開いてください。'
            : 'CSRF確認に失敗しました。');
        error.status = response.status;
        error.retryAfterMs = retryAfterMs(response.headers.get('Retry-After'));
        throw error;
    }
    const value = await boundedJson(response, CSRF_LIMIT);
    if (!isObject(value) || !validText(value.token, 8192)) throw new Error('CSRFトークンを確認できません。');
    return value.token;
}
function isCurrent(mine, flowMine, expectedGrant = undefined) {
    return currentOwner(mine) && flowGeneration === flowMine &&
        (expectedGrant === undefined || grant === expectedGrant);
}
async function post(path, body, { mine, flowMine, expectedGrant = undefined } = {}) {
    const record = { controller: new AbortController(), sent: false, path };
    requests.add(record);
    const timer = setTimeout(() => record.controller.abort(), REQUEST_TIMEOUT_MS);
    try {
        const token = await csrf(record);
        if (!isCurrent(mine, flowMine, expectedGrant)) throw new Error('画面状態が変わりました。操作を中止しました。');
        const text = JSON.stringify(body);
        if (new TextEncoder().encode(text).byteLength > JSON_LIMIT) throw new Error('送信内容が上限を超えています。');
        const headers = { Accept: 'application/json', 'Content-Type': 'application/json', 'X-CSRF-TOKEN': token };
        record.sent = true;
        const response = await fetch(`${API}${path}`, {
            method: 'POST', credentials: 'same-origin', mode: 'same-origin', cache: 'no-store',
            redirect: 'error', headers, body: text, signal: record.controller.signal
        });
        if (!response.ok || response.redirected) {
            const error = new Error(response.status === 401 || response.status === 403
                ? '復旧リンクまたは権限を確認できません。'
                : `処理に失敗しました（${response.status}）。`);
            error.status = response.status;
            error.retryAfterMs = retryAfterMs(response.headers.get('Retry-After'));
            await response.body?.cancel();
            throw error;
        }
        if (!isCurrent(mine, flowMine, expectedGrant)) {
            await response.body?.cancel();
            throw new Error('画面状態が変わりました。結果を確認できません。');
        }
        return await boundedJson(response, JSON_LIMIT);
    } catch (error) {
        if (record.sent) error.requestSent = true;
        throw error;
    } finally {
        clearTimeout(timer);
        requests.delete(record);
    }
}
function parseNativeOptions(value, creating) {
    if (!isObject(value)) throw new Error('パスキーの認証オプションを確認できません。');
    const parser = creating ? window.PublicKeyCredential?.parseCreationOptionsFromJSON
        : window.PublicKeyCredential?.parseRequestOptionsFromJSON;
    try {
        if (typeof parser === 'function') {
            const parsed = parser.call(window.PublicKeyCredential, value);
            if (!isObject(parsed) || !(parsed.challenge instanceof ArrayBuffer || ArrayBuffer.isView(parsed.challenge)))
                throw new Error('invalid');
            return {
                ...parsed,
                timeout: Number.isFinite(parsed.timeout) && parsed.timeout > 0
                    ? Math.min(parsed.timeout, RECOVERY_WINDOW_MS) : RECOVERY_WINDOW_MS
            };
        }
        const decode = text => {
            if (!validText(text, 8192) || !/^[A-Za-z0-9_-]+$/.test(text)) throw new Error('invalid');
            const base64 = text.replace(/-/g, '+').replace(/_/g, '/') + '='.repeat((4 - text.length % 4) % 4);
            const binary = atob(base64);
            return Uint8Array.from(binary, char => char.charCodeAt(0));
        };
        if (typeof value.challenge !== 'string') throw new Error('invalid');
        const parsed = { ...value, challenge: decode(value.challenge) };
        if (creating) {
            if (!isObject(value.user) || typeof value.user.id !== 'string') throw new Error('invalid');
            parsed.user = { ...value.user, id: decode(value.user.id) };
            if (value.excludeCredentials !== undefined) {
                if (!Array.isArray(value.excludeCredentials) || value.excludeCredentials.length > 100) throw new Error('invalid');
                parsed.excludeCredentials = value.excludeCredentials.map(item => {
                    if (!isObject(item) || typeof item.id !== 'string') throw new Error('invalid');
                    return { ...item, id: decode(item.id) };
                });
            }
        } else if (value.allowCredentials !== undefined) {
            if (!Array.isArray(value.allowCredentials) || value.allowCredentials.length > 100) throw new Error('invalid');
            parsed.allowCredentials = value.allowCredentials.map(item => {
                if (!isObject(item) || typeof item.id !== 'string') throw new Error('invalid');
                return { ...item, id: decode(item.id) };
            });
        }
        parsed.timeout = Number.isFinite(parsed.timeout) && parsed.timeout > 0
            ? Math.min(parsed.timeout, RECOVERY_WINDOW_MS) : RECOVERY_WINDOW_MS;
        return parsed;
    } catch {
        throw new Error('パスキーの認証オプションを確認できません。');
    }
}
function encodeBytes(value) {
    if (value == null) return null;
    const bytes = new Uint8Array(value);
    let binary = '';
    for (let offset = 0; offset < bytes.length; offset += 0x8000)
        binary += String.fromCharCode(...bytes.subarray(offset, offset + 0x8000));
    return btoa(binary).replace(/\+/g, '-').replace(/\//g, '_').replace(/=+$/g, '');
}
function serializeRegistration(credential) {
    if (typeof credential?.toJSON === 'function') {
        const value = credential.toJSON();
        if (isObject(value)) return value;
    }
    const response = credential?.response;
    if (!response || !credential.rawId || !response.clientDataJSON || !response.attestationObject)
        throw new Error('パスキーの登録結果を確認できません。');
    return {
        id: credential.id, rawId: encodeBytes(credential.rawId), type: credential.type,
        authenticatorAttachment: credential.authenticatorAttachment,
        response: {
            clientDataJSON: encodeBytes(response.clientDataJSON),
            attestationObject: encodeBytes(response.attestationObject),
            transports: response.getTransports?.() || []
        },
        clientExtensionResults: credential.getClientExtensionResults?.() || {}
    };
}
async function createNativeCredential(publicKey, mine, flowMine, grantSnapshot) {
    if (!('PublicKeyCredential' in window) || !navigator.credentials?.create)
        throw new Error('このブラウザーではパスキー登録を利用できません。');
    const options = parseNativeOptions(publicKey, true);
    const controller = new AbortController();
    const remaining = Math.max(1, grantExpiresAt - Date.now());
    const timer = setTimeout(() => controller.abort(), Math.min(options.timeout, RECOVERY_WINDOW_MS, remaining));
    credentials.add(controller);
    try {
        const result = await navigator.credentials.create({ publicKey: options, signal: controller.signal });
        if (!isCurrent(mine, flowMine, grantSnapshot)) throw new Error('復旧許可の期限または画面状態が変わりました。');
        if (!result) throw new Error('パスキー登録が完了しませんでした。');
        return JSON.stringify(serializeRegistration(result));
    } catch (error) {
        if (!isCurrent(mine, flowMine, grantSnapshot)) throw new Error('復旧許可の期限または画面状態が変わりました。');
        if (error?.name === 'NotAllowedError' || error?.name === 'AbortError')
            throw new Error('パスキー登録が完了しませんでした。必要なら新しい復旧リンクを開いてください。');
        throw error instanceof Error ? error : new Error('パスキー登録を完了できませんでした。');
    } finally {
        clearTimeout(timer);
        credentials.delete(controller);
    }
}
function stripFragment() {
    const cleanUrl = `${location.pathname}${location.search}`;
    history.replaceState(history.state, '', cleanUrl);
}
async function consumeFragment(mine = ownerGeneration) {
    if (!currentOwner(mine)) return;
    const flowMine = ++flowGeneration;
    abortPending();
    clearGrant();
    busy = true;
    complete = false;
    outcomeUnknown = false;
    setText('error', '');
    setText('status', '復旧リンクを確認しています。');
    render();

    let params = new URLSearchParams((location.hash || '').replace(/^#/, ''));
    const operations = params.getAll('op');
    const tokens = params.getAll('token');
    let operationId = operations.length === 1 ? operations[0] : null;
    let token = tokens.length === 1 ? tokens[0] : null;
    try { stripFragment(); }
    catch {
        params = null;
        operationId = null;
        token = null;
        busy = false;
        setText('status', '復旧リンクを安全に処理できません。リンクを開き直してください。');
        render();
        return;
    }
    params = null;
    if (!validText(operationId, 128) || !validText(token, 8192)) {
        operationId = null;
        token = null;
        busy = false;
        setText('status', '有効な復旧リンクがありません。新しい案内を依頼してください。');
        render();
        return;
    }
    const confirmationBody = { operationId, token };
    operationId = null;
    token = null;
    try {
        const value = await post('passkey-recovery-confirm', confirmationBody, { mine, flowMine });
        confirmationBody.operationId = '';
        confirmationBody.token = '';
        if (!isCurrent(mine, flowMine)) return;
        if (!isObject(value) || !validText(value.userId, 128) || !validText(value.grant, 8192) ||
            typeof value.expiresAt !== 'string' || value.expiresAt.length > 64 || !Number.isFinite(Date.parse(value.expiresAt)))
            throw new Error('復旧確認の応答を確認できません。');
        const serverDeadline = Date.parse(value.expiresAt);
        const localDeadline = Date.now() + RECOVERY_WINDOW_MS;
        const deadline = Math.min(serverDeadline, localDeadline);
        if (deadline <= Date.now()) throw new Error('復旧許可の期限が切れています。新しい復旧リンクを依頼してください。');
        userId = value.userId;
        grant = value.grant;
        grantExpiresAt = deadline;
        busy = false;
        setText('expiry', `登録期限：${new Date(deadline).toLocaleString('ja-JP', { timeZone: 'Asia/Tokyo' })}`);
        setText('status', '復旧リンクを確認しました。期限内に新しいパスキーを登録してください。');
        scheduleExpiry(mine, flowMine);
        render();
    } catch (error) {
        confirmationBody.operationId = '';
        confirmationBody.token = '';
        if (!currentOwner(mine) || flowGeneration !== flowMine) return;
        if (error?.requestSent) outcomeUnknown = true;
        busy = false;
        setText('status', outcomeUnknown
            ? '確認結果が不明です。リンクを自動再送しません。新しい案内が必要な場合は運用窓口へ連絡してください。'
            : '復旧リンクを確認できませんでした。新しい案内を依頼してください。');
        setText('error', error?.message || '復旧リンクを確認できませんでした。');
        if (error?.retryAfterMs !== null && error?.retryAfterMs !== undefined)
            setText('error', `${error.message} Retry-Afterを確認し、少なくとも${Math.ceil(error.retryAfterMs / 1000)}秒待ってください。`);
        render();
    }
}
async function registerNewKey(event) {
    event?.preventDefault();
    if (!currentOwner() || busy || complete || outcomeUnknown || !grant || Date.now() >= grantExpiresAt) return;
    const mine = ownerGeneration;
    const flowMine = flowGeneration;
    const userSnapshot = userId;
    const grantSnapshot = grant;
    busy = true;
    setText('error', '');
    setText('status', 'パスキー登録の準備をしています。');
    render();
    try {
        const optionsResponse = await post('passkey-recovery-options', { userId: userSnapshot, grant: grantSnapshot },
            { mine, flowMine, expectedGrant: grantSnapshot });
        if (!isCurrent(mine, flowMine, grantSnapshot) || userId !== userSnapshot) return;
        if (!isObject(optionsResponse) || !isObject(optionsResponse.publicKey))
            throw new Error('パスキー登録オプションを確認できません。');
        if (Date.now() >= grantExpiresAt) throw new Error('復旧許可の期限が切れました。');
        setText('status', 'ブラウザーのパスキー登録を完了してください。');
        const credential = await createNativeCredential(optionsResponse.publicKey, mine, flowMine, grantSnapshot);
        if (!isCurrent(mine, flowMine, grantSnapshot) || userId !== userSnapshot) return;
        if (Date.now() >= grantExpiresAt) throw new Error('復旧許可の期限が切れました。');
        const result = await post('passkey-recovery-complete', { userId: userSnapshot, grant: grantSnapshot, credential },
            { mine, flowMine, expectedGrant: grantSnapshot });
        if (!isCurrent(mine, flowMine, grantSnapshot) || userId !== userSnapshot) {
            outcomeUnknown = true;
            throw new Error('画面状態が変わり、登録結果を確認できません。');
        }
        if (!isObject(result) || !validText(result.message, 500)) {
            outcomeUnknown = true;
            throw new Error('登録結果の形式を確認できません。');
        }
        complete = true;
        busy = false;
        clearGrant();
        setText('status', result.message);
        setText('error', '');
        render();
    } catch (error) {
        if (!currentOwner(mine) || flowGeneration !== flowMine) return;
        if (error?.status === 401 || error?.status === 403) clearGrant();
        if (error?.requestSent) outcomeUnknown = true;
        busy = false;
        setText('status', outcomeUnknown
            ? '登録結果が不明です。登録を自動再送しません。通常のログインを試すか、運用窓口へ連絡してください。'
            : '新しいパスキーを登録できませんでした。');
        let message = error?.message || '新しいパスキーを登録できませんでした。';
        if (error?.retryAfterMs !== null && error?.retryAfterMs !== undefined)
            message += ` Retry-Afterを確認し、少なくとも${Math.ceil(error.retryAfterMs / 1000)}秒待ってください。`;
        setText('error', message);
        render();
    }
}
function installListeners(root) {
    const button = root.querySelector(`#${ids.register}`);
    listeners = { register: event => registerNewKey(event) };
    button?.addEventListener('click', listeners.register);
    navigationHandler = () => {
        if (!currentOwner()) return;
        flowGeneration++;
        abortPending();
        clearGrant();
        complete = false;
        outcomeUnknown = false;
        setText('error', '');
        consumeFragment(ownerGeneration);
    };
    window.addEventListener('hashchange', navigationHandler);
    window.addEventListener('popstate', navigationHandler);
}
function removeListeners(root) {
    if (root && listeners) root.querySelector(`#${ids.register}`)?.removeEventListener('click', listeners.register);
    if (navigationHandler) {
        window.removeEventListener('hashchange', navigationHandler);
        window.removeEventListener('popstate', navigationHandler);
    }
    listeners = null;
    navigationHandler = null;
}
export async function init(root) {
    if (!root || root.id !== ids.root) return;
    if (ownerRoot === root && currentOwner()) return;
    cleanup(ownerRoot);
    ownerRoot = root;
    const mine = ++ownerGeneration;
    flowGeneration++;
    userId = null;
    grant = null;
    grantExpiresAt = 0;
    busy = true;
    complete = false;
    outcomeUnknown = false;
    installListeners(root);
    await consumeFragment(mine);
}
export function cleanup(root = ownerRoot) {
    if (root && ownerRoot && root !== ownerRoot) return;
    ownerGeneration++;
    flowGeneration++;
    removeListeners(ownerRoot);
    abortPending();
    clearTimer();
    requests = new Set();
    credentials = new Set();
    ownerRoot = null;
    userId = null;
    grant = null;
    grantExpiresAt = 0;
    busy = false;
    complete = false;
    outcomeUnknown = false;
}
