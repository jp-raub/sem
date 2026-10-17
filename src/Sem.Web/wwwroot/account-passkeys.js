import { createResultPresenter } from './operation-result-presenter.js';
const API = '/api/account-passkeys/';
const JSON_LIMIT = 64 * 1024;
const CSRF_LIMIT = 16 * 1024;
const TIMEOUT_MS = 25_000;

let resultPresenter;
let actorId = null;
let stopped = false;
let busy = false;
let generation = 0;
const controllers = new Set();
const $ = id => document.getElementById(id);

function message(id, text = '') {
    const node = $(id);
    if (node) node.textContent = text;
    if (id === 'account-passkeys-status' && ['パスキー名を変更しました。', '新しいパスキーを追加しました。', 'パスキーを削除しました。'].includes(text)) resultPresenter?.show('success', text);
}

function boundedJson(response, limit) {
    return readBoundedJson(response, limit);
}

async function readBoundedJson(response, limit) {
    if (!(response.headers.get('Content-Type') || '').toLowerCase().startsWith('application/json'))
        throw new Error('JSON応答を確認できません。');
    const length = response.headers.get('Content-Length');
    if (length !== null && (!/^\d+$/.test(length) || Number(length) > limit))
        throw new Error('応答サイズが上限を超えています。');
    if (!response.body) throw new Error('応答本文を確認できません。');
    const reader = response.body.getReader();
    const parts = [];
    let size = 0;
    try {
        for (;;) {
            const { done, value } = await reader.read();
            if (done) break;
            size += value.byteLength;
            if (size > limit) throw new Error('応答サイズが上限を超えています。');
            parts.push(value);
        }
    } catch (error) {
        await reader.cancel().catch(() => {});
        throw error;
    }
    const bytes = new Uint8Array(size);
    let offset = 0;
    for (const part of parts) { bytes.set(part, offset); offset += part.byteLength; }
    return JSON.parse(new TextDecoder('utf-8', { fatal: true }).decode(bytes));
}

function newRequest() {
    const controller = new AbortController();
    controllers.add(controller);
    const timer = setTimeout(() => controller.abort(), TIMEOUT_MS);
    return {
        controller,
        finish() { clearTimeout(timer); controllers.delete(controller); }
    };
}

async function readCsrf(signal) {
    const response = await fetch('/api/csrf', {
        method: 'GET', credentials: 'same-origin', mode: 'same-origin',
        cache: 'no-store', redirect: 'error', headers: { Accept: 'application/json' }, signal
    });
    if (!response.ok || response.redirected) {
        await response.body?.cancel();
        throw new Error('CSRF確認に失敗しました。ページを再読み込みしてください。');
    }
    const value = await boundedJson(response, CSRF_LIMIT);
    if (!value || typeof value.token !== 'string' || !value.token || value.token.length > 8192)
        throw new Error('CSRFトークンを確認できません。');
    return value.token;
}

async function request(action, body = null, expected = actorId) {
    const mine = generation;
    const call = newRequest();
    let postStarted = false;
    try {
        const headers = { Accept: 'application/json' };
        const init = {
            method: body === null ? 'GET' : 'POST',
            credentials: 'same-origin', mode: 'same-origin',
            cache: 'no-store', redirect: 'error', headers, signal: call.controller.signal
        };
        let path = API + action;
        if (body === null) {
            if (expected) headers['X-Expected-Actor'] = expected;
        } else {
            if (!expected || typeof expected !== 'string' || expected.length > 256)
                throw new Error('アカウントを確認できません。ページを再取得してください。');
            const csrf = await readCsrf(call.controller.signal);
            if (stopped || mine !== generation || actorId !== expected)
                throw new Error('ログイン状態が変わりました。最新状態を再取得してください。');
            const payload = JSON.stringify({ expectedActor: expected, ...body });
            if (new TextEncoder().encode(payload).byteLength > JSON_LIMIT)
                throw new Error('送信サイズが上限を超えています。');
            init.headers = {
                Accept: 'application/json',
                'Content-Type': 'application/json; charset=utf-8',
                'X-CSRF-TOKEN': csrf
            };
            init.body = payload;
            postStarted = true;
        }
        const response = await fetch(path, init);
        if (response.redirected) {
            await response.body?.cancel();
            throw new Error('安全のためリダイレクト応答を拒否しました。');
        }
        const value = await boundedJson(response, JSON_LIMIT);
        if (!response.ok) {
            const error = new Error(typeof value.error === 'string' && value.error.length <= 400
                ? value.error : '処理を完了できませんでした。');
            error.status = response.status;
            throw error;
        }
        if (stopped || mine !== generation) throw new Error('画面が更新されました。最新状態を取得してください。');
        return value;
    } catch (error) {
        if (postStarted && !(Number.isInteger(error?.status) && error.status < 500))
            error.writeOutcomeUnknown = true;
        throw error;
    } finally {
        call.finish();
    }
}

function base64UrlToBytes(value) {
    if (typeof value !== 'string' || !/^[A-Za-z0-9_-]+$/.test(value))
        throw new Error('パスキー設定を読み取れませんでした。');
    const base64 = value.replace(/-/g, '+').replace(/_/g, '/') +
        '='.repeat((4 - value.length % 4) % 4);
    const binary = atob(base64);
    return Uint8Array.from(binary, c => c.charCodeAt(0)).buffer;
}

function bytesToBase64Url(value) {
    if (value == null) return null;
    const bytes = new Uint8Array(value);
    let binary = '';
    for (let i = 0; i < bytes.length; i += 0x8000)
        binary += String.fromCharCode(...bytes.subarray(i, i + 0x8000));
    return btoa(binary).replace(/\+/g, '-').replace(/\//g, '_').replace(/=+$/g, '');
}

function requestOptions(value) {
    if (typeof PublicKeyCredential?.parseRequestOptionsFromJSON === 'function') {
        try { return PublicKeyCredential.parseRequestOptionsFromJSON(value); }
        catch { throw new Error('パスキー設定を読み取れませんでした。'); }
    }
    const options = { ...value, challenge: base64UrlToBytes(value.challenge) };
    if (Array.isArray(value.allowCredentials))
        options.allowCredentials = value.allowCredentials.map(item => ({ ...item, id: base64UrlToBytes(item.id) }));
    options.timeout = Number.isFinite(options.timeout) ? Math.min(options.timeout, 300_000) : 300_000;
    return options;
}

function creationOptions(value) {
    if (typeof PublicKeyCredential?.parseCreationOptionsFromJSON === 'function') {
        try { return PublicKeyCredential.parseCreationOptionsFromJSON(value); }
        catch { throw new Error('パスキー設定を読み取れませんでした。'); }
    }
    const options = { ...value, challenge: base64UrlToBytes(value.challenge), user: { ...value.user, id: base64UrlToBytes(value.user.id) } };
    for (const field of ['excludeCredentials'])
        if (Array.isArray(value[field])) options[field] = value[field].map(item => ({ ...item, id: base64UrlToBytes(item.id) }));
    options.timeout = Number.isFinite(options.timeout) ? Math.min(options.timeout, 300_000) : 300_000;
    return options;
}

function serializeCredential(credential) {
    if (typeof credential?.toJSON === 'function') {
        const value = credential.toJSON();
        if (value && typeof value === 'object' && !Array.isArray(value)) return value;
    }
    const response = credential?.response;
    if (!response) throw new Error('パスキー応答を読み取れませんでした。');
    const serialized = { clientDataJSON: bytesToBase64Url(response.clientDataJSON) };
    if (response.attestationObject) {
        serialized.attestationObject = bytesToBase64Url(response.attestationObject);
        serialized.transports = response.getTransports?.() || [];
    } else if (response.authenticatorData && response.signature) {
        serialized.authenticatorData = bytesToBase64Url(response.authenticatorData);
        serialized.signature = bytesToBase64Url(response.signature);
        serialized.userHandle = bytesToBase64Url(response.userHandle);
    } else throw new Error('パスキー応答を読み取れませんでした。');
    return {
        id: credential.id, rawId: bytesToBase64Url(credential.rawId), type: credential.type,
        authenticatorAttachment: credential.authenticatorAttachment,
        response: serialized, clientExtensionResults: credential.getClientExtensionResults?.() || {}
    };
}

function dateLabel(value) {
    const milliseconds = typeof value === 'number' && Number.isFinite(value)
        ? value * 1000 : (typeof value === 'string' ? Date.parse(value) : NaN);
    if (!Number.isFinite(milliseconds)) return '登録日不明';
    return new Intl.DateTimeFormat('ja-JP', {
        dateStyle: 'medium', timeStyle: 'short', timeZone: 'Asia/Tokyo'
    }).format(new Date(milliseconds));
}

function appendText(parent, tag, text, className = '') {
    const node = document.createElement(tag);
    node.textContent = text;
    if (className) node.className = className;
    parent.append(node);
    return node;
}

function renderKeys(keys) {
    const root = $('account-passkeys-list');
    root.replaceChildren();
    if (!Array.isArray(keys) || keys.length === 0) {
        appendText(root, 'p', '登録済みのパスキーはありません。');
        return;
    }
    const list = document.createElement('ul');
    list.className = 'account-passkey-list';
    for (const key of keys) {
        if (!key || typeof key.id !== 'string' || key.id.length > 2048) continue;
        const item = document.createElement('li');
        item.className = 'account-passkey-item';
        const info = document.createElement('div');
        appendText(info, 'strong', typeof key.name === 'string' && key.name ? key.name : '名前未設定');
        appendText(info, 'small', dateLabel(key.createdAt));
        const actions = document.createElement('div');
        actions.className = 'account-passkey-actions';
        const rename = document.createElement('form');
        rename.className = 'account-passkey-rename';
        const label = document.createElement('label');
        label.textContent = '表示名';
        const input = document.createElement('input');
        input.type = 'text';
        input.maxLength = 200;
        input.required = true;
        input.autocomplete = 'off';
        input.value = typeof key.name === 'string' ? key.name : '';
        label.append(input);
        const renameButton = document.createElement('button');
        renameButton.type = 'submit';
        renameButton.textContent = '名前を変更';
        rename.append(label, renameButton);
        rename.addEventListener('submit', async event => {
            event.preventDefault();
            await run(async () => {
                await request('rename', { credentialId: key.id, name: input.value });
                message('account-passkeys-status', 'パスキー名を変更しました。');
                await refresh();
            });
        });
        const deleteButton = document.createElement('button');
        deleteButton.type = 'button';
        deleteButton.textContent = '削除';
        deleteButton.disabled = keys.length <= 1;
        if (deleteButton.disabled) deleteButton.title = '最後のパスキーは削除できません。';
        deleteButton.addEventListener('click', () => run(() => deleteFlow(key)));
        actions.append(rename, deleteButton);
        item.append(info, actions);
        list.append(item);
    }
    root.append(list);
}

function renderAddition(addition) {
    const panel = $('account-passkeys-add-flow');
    const addButton = $('account-passkeys-add');
    const resend = $('account-passkeys-add-resend');
    const create = $('account-passkeys-add-create');
    const confirm = $('account-passkeys-add-confirm');
    const codePanel = $('account-passkeys-email-code-panel');
    const code = $('account-passkeys-email-code');
    const cancel = $('account-passkeys-add-cancel');
    if (!addition) {
        panel.hidden = true;
        addButton.hidden = false;
        addButton.disabled = false;
        resend.hidden = create.hidden = confirm.hidden = cancel.hidden = true;
        codePanel.hidden = true;
        code.disabled = true;
        code.value = '';
        message('account-passkeys-add-status', '');
        return;
    }
    addButton.hidden = true;
    panel.hidden = false;
    const expires = dateLabel(addition.expires);
    const registration = addition.registrationUntil ? dateLabel(addition.registrationUntil) : '';
    message('account-passkeys-add-status', addition.emailVerified
        ? `メールを再確認しました。追加全体の期限は ${expires}、登録期限は ${registration} です。`
        : `既存パスキーで再認証済みです。保存済みメールへの確認を待っています。追加全体の期限は ${expires} です。`);
    resend.hidden = !!addition.emailVerified;
    create.hidden = !addition.emailVerified;
    confirm.hidden = !!addition.emailVerified;
    codePanel.hidden = !!addition.emailVerified;
    code.disabled = !!addition.emailVerified;
    confirm.disabled = !!addition.emailVerified;
    cancel.hidden = false;
    confirm.onclick = () => run(async () => {
        const token = code.value.trim();
        code.value = '';
        if (!/^[A-Fa-f0-9]{64}$/.test(token))
            throw new Error('保存済みメールに届いた64桁の確認コードを入力してください。');
        await request('add-confirm', { token });
        message('account-passkeys-status', 'メールを再確認しました。5分以内に新しいパスキーを登録してください。');
        await refresh();
    });
    resend.onclick = () => run(async () => {
        await request('add-resend', { id: addition.id });
        message('account-passkeys-status', '確認コードを再送しました。期限は延長されません。');
        await refresh();
    });
    cancel.onclick = () => run(async () => {
        await request('add-cancel', { id: addition.id });
        message('account-passkeys-status', 'パスキー追加を取り消しました。');
        await refresh();
    });
    create.onclick = () => run(() => completeAddition(addition));
}

async function refresh() {
    const current = actorId;
    const data = await request('list', null, current);
    if (current && data.actorId !== current) {
        generation++;
        actorId = null;
        throw new Error('ログインアカウントが変わりました。一覧をもう一度取得してください。');
    }
    actorId = data.actorId;
    if (typeof data.email !== 'string' || !data.emailConfirmed)
        throw new Error('認証用メールアドレスを確認できません。');
    $('account-passkeys-email').textContent = data.email;
    renderKeys(data.passkeys);
    renderAddition(data.addition);
    $('account-passkeys-add').disabled = !!data.addition;
    message('account-passkeys-status', `${data.passkeys.length} 件のパスキーを読み込みました。`);
}

async function run(operation) {
    if (busy || stopped) return;
    const completionPresenter = resultPresenter;
    busy = true;
    $('account-passkeys-add').disabled = true;
    message('account-passkeys-error', '');
    try {
        await operation();
    } catch (error) {
        message('account-passkeys-error', error?.writeOutcomeUnknown
            ? '通信結果が不明です。操作を再送せず、一覧を再取得して状態を確認してください。'
            : (error instanceof Error ? error.message.slice(0, 400) : '操作できませんでした。'));
        if (error?.writeOutcomeUnknown) message('account-passkeys-status', '完了状態を照合してください。');
        completionPresenter?.show(error?.writeOutcomeUnknown ? 'unknown' : 'error', $('account-passkeys-error')?.textContent || '操作結果を確認してください。');
    } finally {
        busy = false;
        if (!stopped) $('account-passkeys-add').disabled = false;
    }
}

async function beginAddition() {
    if (!window.PublicKeyCredential || !navigator.credentials?.get)
        throw new Error('このブラウザーではパスキーを利用できません。');
    const options = await request('add-begin', {});
    const id = options.operationId;
    delete options.operationId;
    let credential = null;
    let serialized = null;
    try {
        credential = await navigator.credentials.get({ publicKey: requestOptions(options) });
        if (!credential) throw new Error('再認証を完了できませんでした。');
        serialized = JSON.stringify(serializeCredential(credential));
        if (new TextEncoder().encode(serialized).byteLength > JSON_LIMIT - 1024)
            throw new Error('パスキー応答が上限を超えています。');
        await request('add-reauth', { id, credential: serialized });
    } finally {
        credential = null;
        serialized = null;
    }
    message('account-passkeys-status', '既存パスキーで再認証しました。保存済みメールを確認してください。');
    await refresh();
}

async function completeAddition(addition) {
    if (!window.PublicKeyCredential || !navigator.credentials?.create)
        throw new Error('このブラウザーではパスキーを登録できません。');
    const options = await request('add-create-options', {});
    let credential = null;
    let serialized = null;
    try {
        credential = await navigator.credentials.create({ publicKey: creationOptions(options) });
        if (!credential) throw new Error('パスキー登録を完了できませんでした。');
        serialized = JSON.stringify(serializeCredential(credential));
        if (new TextEncoder().encode(serialized).byteLength > JSON_LIMIT - 1024)
            throw new Error('パスキー応答が上限を超えています。');
        await request('add-complete', { credential: serialized });
    } finally {
        credential = null;
        serialized = null;
    }
    message('account-passkeys-status', '新しいパスキーを追加しました。');
    await refresh();
}

async function deleteFlow(key) {
    if (!window.PublicKeyCredential || !navigator.credentials?.get)
        throw new Error('このブラウザーではパスキーを利用できません。');
    const challenge = await request('delete-options', { credentialId: key.id });
    let credential = null;
    let serialized = null;
    let settled = false;
    try {
        credential = await navigator.credentials.get({ publicKey: requestOptions(challenge.publicKey) });
        if (!credential) throw new Error('再認証を完了できませんでした。');
        serialized = JSON.stringify(serializeCredential(credential));
        if (new TextEncoder().encode(serialized).byteLength > JSON_LIMIT - 1024)
            throw new Error('パスキー応答が上限を超えています。');
        await request('delete-verify', { proofId: challenge.proofId, credential: serialized });
        credential = null;
        serialized = null;
        const name = typeof key.name === 'string' && key.name ? key.name : '名前未設定のパスキー';
        const confirmed = window.confirm(`「${name}」を削除しますか？この確認は5分で失効し、最後のパスキーは削除できません。`);
        if (!confirmed) {
            await request('delete-cancel', { proofId: challenge.proofId });
            settled = true;
            message('account-passkeys-status', '削除を取り消しました。');
            return;
        }
        try {
            await request('delete-confirm', { proofId: challenge.proofId, confirmed: true });
            settled = true;
            message('account-passkeys-status', 'パスキーを削除しました。');
            await refresh();
        } catch (error) {
            // A received status is authoritative; unknown transport outcomes are
            // cleaned up below with the same proof-bound cancellation endpoint.
            if (error?.status === 409) settled = true;
            throw error;
        }
    } finally {
        credential = null;
        serialized = null;
        if (!settled && !stopped) {
            try { await request('delete-cancel', { proofId: challenge.proofId }); }
            catch { /* Expired, revoked, or already-consumed proofs cannot be reused. */ }
        }
    }
}

export async function init() {
    stopped = false;
    generation++;
    resultPresenter?.clear();
    const owner = $('account-passkeys'), mine = generation;
    resultPresenter = createResultPresenter(owner, () => !stopped && mine === generation);
    $('account-passkeys-add').onclick = () => run(beginAddition);
    $('account-passkeys-refresh').onclick = () => run(refresh);
    try {
        await refresh();
    } catch (error) {
        message('account-passkeys-error', error instanceof Error ? error.message.slice(0, 400) : 'パスキーを読み込めませんでした。');
        message('account-passkeys-status', 'アカウントを確認できません。ログイン状態を確かめて再取得してください。');
    }
}

export function cleanup() {
    resultPresenter?.clear(); resultPresenter = null;
    stopped = true;
    generation++;
    for (const controller of controllers) controller.abort();
    controllers.clear();
    actorId = null;
    busy = false;
}
