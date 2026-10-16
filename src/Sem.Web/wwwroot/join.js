(() => {
    'use strict';

    const API = '/api/';
    const JSON_LIMIT = 1_048_576;
    const CSRF_LIMIT = 65_536;
    const JOIN_REQUEST_LIMIT = 4_096;
    const PASSKEY_REQUEST_LIMIT = 65_536;
    const REQUEST_TIMEOUT_MS = 25_000;
    const NATIVE_TIMEOUT_MS = 300_000;
    const ids = {
        root: 'join-page',
        initError: 'join-module-error',
        status: 'join-status',
        error: 'join-error',
        registerForm: 'join-register-form',
        registerFields: 'join-register-fields',
        invite: 'join-invite',
        name: 'join-name',
        email: 'join-email',
        register: 'join-register-submit',
        confirmForm: 'join-confirm-form',
        confirmFields: 'join-confirm-fields',
        confirm: 'join-confirm-submit',
        passkeyForm: 'join-passkey-form',
        passkeyFields: 'join-passkey-fields',
        consent: 'join-enrollment-consent',
        passkey: 'join-passkey-register',
        reconcile: 'join-reconcile',
        workspaceNote: 'join-workspace-note',
        workspace: 'join-workspace-link'
    };
    const safeFragmentValue = value => typeof value === 'string' && value.length > 0 &&
        value.length <= 128 && !/[\u0000-\u001f\u007f-\u009f]/u.test(value);

    let fragmentData = consumeFragment();
    const invalidFragmentToken = fragmentData.invalidToken;
    let confirmationToken = fragmentData.token;
    let inviteFromFragment = fragmentData.invite;
    fragmentData.token = null;
    fragmentData.invite = null;
    fragmentData = null;
    const root = document.getElementById(ids.root);
    if (!root) return;

    let disposed = false;
    let generation = 0;
    let active = null;
    let ready = false;
    let busy = false;
    let locked = false;
    let unknownKind = null;
    let signedIn = false;
    let enrollmentAvailable = false;
    let queued = false;
    let completed = false;
    let nativeControllers = new Set();

    class HttpFailure extends Error {
        constructor(status, retryAfter) {
            super('HTTP response');
            this.status = status;
            this.retryAfter = retryAfter;
        }
    }

    class RequestFailure extends Error {
        constructor(sent, cause) {
            super('Request failed');
            this.sent = sent;
            this.cause = cause;
        }
    }

    class SessionStateChangedFailure extends Error {
        constructor(status) {
            super('Session state changed');
            this.status = status;
        }
    }

    const byId = name => root.querySelector(`#${ids[name]}`);
    const isObject = value => value !== null && typeof value === 'object' && !Array.isArray(value);
    const validText = (value, max) => typeof value === 'string' && value.length > 0 &&
        value.length <= max && !/[\u0000-\u001f\u007f-\u009f]/u.test(value);

    function consumeFragment() {
        let raw = window.location.hash || '';
        let token = null;
        let invite = null;
        let invalidToken = false;
        try {
            // Remove query and fragment before the first network request. Only fragment
            // values are considered; neither is copied into a URL or persisted.
            window.history.replaceState(window.history.state, '', window.location.pathname);
        } catch {
            raw = '';
            return { token: null, invite: null, invalidToken: true };
        }
        let params = new URLSearchParams(raw.startsWith('#') ? raw.slice(1) : raw);
        raw = '';
        const tokens = params.getAll('token');
        const invites = params.getAll('invite');
        if (tokens.length > 0) {
            if (tokens.length === 1 && safeFragmentValue(tokens[0])) token = tokens[0];
            else invalidToken = true;
        }
        if (invites.length === 1 && safeFragmentValue(invites[0])) invite = invites[0];
        params = null;
        return { token, invite, invalidToken };
    }

    function current(operation = active) {
        return !disposed && !!operation && operation === active && operation.id === generation &&
            root.isConnected !== false && document.getElementById(ids.root) === root;
    }

    function ensureCurrent(operation) {
        if (!current(operation)) throw new DOMException('Stale join operation', 'AbortError');
    }

    function setText(name, text) {
        const target = byId(name);
        if (target) target.textContent = String(text || '');
    }

    function setHidden(name, hidden) {
        const target = byId(name);
        if (target) target.hidden = hidden;
    }

    function setWorkspaceVisible(visible) {
        setHidden('workspaceNote', !visible);
        setHidden('workspace', !visible);
    }

    function render() {
        if (disposed || root.isConnected === false) return;
        const globalLock = !ready || busy || locked || signedIn || queued || completed;
        const registerAllowed = !globalLock && !enrollmentAvailable && !confirmationToken;
        const confirmAllowed = !globalLock && !!confirmationToken;
        const passkeyAllowed = !globalLock && enrollmentAvailable && !confirmationToken;

        setHidden('registerForm', !registerAllowed);
        byId('registerFields').disabled = !registerAllowed;
        byId('register').disabled = !registerAllowed;

        setHidden('confirmForm', !confirmAllowed);
        byId('confirmFields').disabled = !confirmAllowed;
        byId('confirm').disabled = !confirmAllowed;

        setHidden('passkeyForm', !passkeyAllowed);
        byId('passkeyFields').disabled = !passkeyAllowed;
        byId('consent').disabled = !passkeyAllowed;
        byId('passkey').disabled = !passkeyAllowed || byId('consent').checked !== true;

        setHidden('reconcile', !locked);
        byId('reconcile').disabled = busy || disposed;
    }

    function begin(kind) {
        if (active?.controller) active.controller.abort();
        const operation = { id: ++generation, kind, controller: new AbortController(), sent: false, timedOut: false };
        active = operation;
        busy = true;
        render();
        return operation;
    }

    function finish(operation) {
        if (active === operation) {
            active = null;
            busy = false;
            render();
        }
    }

    function retryAfterSeconds(header) {
        if (!header) return null;
        const seconds = Number(header);
        if (Number.isFinite(seconds) && seconds >= 0) return Math.min(Math.ceil(seconds), 86_400);
        const date = Date.parse(header);
        if (!Number.isFinite(date)) return null;
        return Math.min(Math.max(0, Math.ceil((date - Date.now()) / 1000)), 86_400);
    }

    async function readBoundedJson(response, limit, operation) {
        const type = response.headers.get('content-type') || '';
        if (!/^application\/(?:[a-z0-9.+-]*\+)?json(?:\s*;|\s*$)/iu.test(type))
            throw new Error('応答形式を確認できません。');
        const length = response.headers.get('content-length');
        if (length !== null && (!/^\d+$/u.test(length) || Number(length) > limit))
            throw new Error('応答サイズが上限を超えています。');
        if (!response.body) throw new Error('応答本文を確認できません。');
        const reader = response.body.getReader();
        const chunks = [];
        let size = 0;
        try {
            while (true) {
                ensureCurrent(operation);
                const { done, value } = await reader.read();
                ensureCurrent(operation);
                if (done) break;
                size += value.byteLength;
                if (size > limit) {
                    try { await reader.cancel(); } catch { /* stream is discarded */ }
                    throw new Error('応答サイズが上限を超えています。');
                }
                chunks.push(value);
            }
        } finally {
            try { reader.releaseLock(); } catch { /* already closed */ }
        }
        const bytes = new Uint8Array(size);
        let offset = 0;
        for (const chunk of chunks) {
            bytes.set(chunk, offset);
            offset += chunk.byteLength;
        }
        const value = JSON.parse(new TextDecoder('utf-8', { fatal: true }).decode(bytes));
        if (!isObject(value)) throw new Error('応答形式を確認できません。');
        return value;
    }

    async function request(path, method, body, limit, operation, { csrf = false, beforeSend = null } = {}) {
        const target = new URL(path, window.location.origin);
        if (target.origin !== window.location.origin || target.pathname !== path ||
            target.search !== '' || target.hash !== '' || !path.startsWith(API))
            throw new Error('接続先を確認できません。');

        let csrfValue = null;
        let bodyText = null;
        let sent = false;
        try {
            const headers = new Headers({ Accept: 'application/json' });
            if (method === 'POST') {
                if (csrf) {
                    const csrfValueResponse = await request('/api/csrf', 'GET', null, CSRF_LIMIT, operation);
                    ensureCurrent(operation);
                    if (!validText(csrfValueResponse.token, 8192))
                        throw new Error('送信準備を確認できません。');
                    csrfValue = csrfValueResponse.token;
                }
                bodyText = JSON.stringify(body);
                if (new TextEncoder().encode(bodyText).byteLength > limit)
                    throw new Error('送信データが上限を超えています。');
                headers.set('Content-Type', 'application/json');
                if (csrfValue) headers.set('X-CSRF-TOKEN', csrfValue);
                if (beforeSend) {
                    await beforeSend(operation);
                    ensureCurrent(operation);
                }
            } else if (method !== 'GET') {
                throw new Error('未対応の操作です。');
            }

            const controller = operation.controller;
            const timer = window.setTimeout(() => {
                operation.timedOut = true;
                controller.abort();
            }, REQUEST_TIMEOUT_MS);
            try {
                if (method === 'POST') {
                    sent = true;
                    operation.sent = true;
                }
                const response = await window.fetch(target.pathname, {
                    method,
                    mode: 'same-origin',
                    credentials: 'same-origin',
                    cache: 'no-store',
                    redirect: 'error',
                    headers,
                    ...(method === 'POST' ? { body: bodyText } : {}),
                    signal: controller.signal
                });
                ensureCurrent(operation);
                if (response.redirected) {
                    try { await response.body?.cancel(); } catch { /* discarded */ }
                    throw new Error('予期しないリダイレクトです。');
                }
                if (!response.ok) {
                    const failure = new HttpFailure(response.status, retryAfterSeconds(response.headers.get('Retry-After')));
                    try { await response.body?.cancel(); } catch { /* discarded */ }
                    throw failure;
                }
                const value = await readBoundedJson(response, limit, operation);
                ensureCurrent(operation);
                return value;
            } finally {
                window.clearTimeout(timer);
            }
        } catch (error) {
            if (error instanceof SessionStateChangedFailure) throw error;
            if (error instanceof HttpFailure) {
                error.requestSent = sent;
                throw error;
            }
            throw new RequestFailure(sent, error);
        } finally {
            csrfValue = null;
            bodyText = null;
        }
    }

    async function readStatus(operation) {
        const value = await request('/api/status', 'GET', null, JSON_LIMIT, operation);
        ensureCurrent(operation);
        if (typeof value.enrolling !== 'boolean' || !(value.user === null ||
            (isObject(value.user) && validText(value.user.id, 128))))
            throw new Error('ログイン状態の応答を確認できません。');
        return { signedIn: isObject(value.user), enrolling: value.enrolling };
    }

    async function verifySession(operation, expected) {
        const status = await readStatus(operation);
        ensureCurrent(operation);
        if (status.signedIn !== expected.signedIn || status.enrolling !== expected.enrolling)
            throw new SessionStateChangedFailure(status);
        return status;
    }

    function clearFields() {
        byId('invite').value = '';
        byId('name').value = '';
        byId('email').value = '';
    }

    function clearSecrets() {
        confirmationToken = null;
        inviteFromFragment = null;
        clearFields();
    }

    function showSignedIn() {
        signedIn = true;
        enrollmentAvailable = false;
        queued = false;
        clearSecrets();
        setWorkspaceVisible(true);
        setText('status', 'このブラウザーはログイン済みです。登録・確認操作は行いません。');
        render();
    }

    function setUnknown(kind) {
        locked = true;
        unknownKind = kind;
        enrollmentAvailable = false;
        clearSecrets();
        const messages = {
            register: '登録依頼の結果を確認できません。自動再送はしません。読み取り専用で状態を確認してください。',
            confirm: 'メール確認の結果を確認できません。確認リンクを自動再送・再利用しません。読み取り専用で状態を確認してください。',
            options: 'パスキー登録準備の結果を確認できません。自動再送・自動登録はしません。読み取り専用で状態を確認してください。',
            complete: 'パスキー登録の結果を確認できません。自動再送はしません。読み取り専用でログイン状態を確認してください。'
        };
        setText('error', '');
        setText('status', messages[kind] || '処理結果を確認できません。自動再送はしません。');
        setWorkspaceVisible(false);
        render();
    }

    function setSessionChanged(message) {
        locked = true;
        enrollmentAvailable = false;
        clearSecrets();
        setText('error', '');
        setText('status', message || 'ログイン状態が変化した可能性があります。登録操作を停止しました。');
        setWorkspaceVisible(false);
        render();
    }

    function setKnownError(kind, error) {
        const waitMessage = error?.retryAfter === null || error?.retryAfter === undefined
            ? '' : ` 少なくとも${error.retryAfter}秒待ってから操作してください。`;
        if (kind === 'register') {
            setText('error', `招待コード・表示名・メールアドレスを確認してください。${waitMessage}`);
            setText('status', '登録依頼は受け付けられませんでした。入力を確認してから、必要な場合のみ再度操作してください。');
            return;
        }
        if (kind === 'confirm') {
            clearSecrets();
            setText('error', '確認リンクを処理できません。メールのリンクを開き直してください。');
            setText('status', '確認リンクは再送していません。');
            ready = false;
            return;
        }
        enrollmentAvailable = false;
        setText('error', '登録確認セッションを確認できません。リンクを開き直してください。');
        setText('status', `パスキー操作を完了できませんでした。${waitMessage}`);
        locked = true;
        unknownKind = kind;
        setWorkspaceVisible(false);
    }

    function showStatusReadback(status, forUnknown) {
        if (status.signedIn) {
            setWorkspaceVisible(true);
            setText('status', forUnknown
                ? 'このブラウザーはログイン済みです。直前の登録依頼や確認操作の成立を示すものではなく、再送もしていません。'
                : 'このブラウザーはログイン済みです。登録操作は行いません。');
            return;
        }
        setWorkspaceVisible(false);
        if (status.enrolling) {
            setText('status', '登録確認セッションは存在しますが、この状態だけでは直前の依頼・確認の成立や登録対象を特定できません。自動続行・再送はしていません。');
        } else {
            setText('status', 'ログイン・登録確認セッションは確認できません。直前の依頼・確認の成立は判定できず、再送もしていません。');
        }
    }

    async function readOnlyReconcile() {
        if (!locked || busy || disposed) return;
        const operation = begin('reconcile');
        setText('error', '');
        setText('status', '現在のログイン・登録確認状態を読み取り専用で確認しています。');
        try {
            const status = await readStatus(operation);
            ensureCurrent(operation);
            showStatusReadback(status, true);
        } catch {
            if (current(operation)) setText('status', '状態を確認できませんでした。元のPOSTは再送していません。ページを再読み込みしても自動再送は行いません。');
        } finally {
            finish(operation);
        }
    }

    function handlePostFailure(operation, kind, error) {
        if (!current(operation)) return;
        if (error instanceof SessionStateChangedFailure) {
            if (error.status?.signedIn) showSignedIn();
            else setSessionChanged('ログインまたは登録確認セッションが変化しました。送信を中止し、状態を読み取り専用で確認してください。');
            return;
        }
        if (error instanceof HttpFailure && (error.status === 401 || error.status === 403)) {
            setSessionChanged('ログイン状態または確認セッションを確認できません。登録操作を停止しました。必要なら読み取り専用で状態を確認してください。');
            locked = true;
            render();
            return;
        }
        if (error instanceof HttpFailure && error.status >= 400 && error.status < 500) {
            setKnownError(kind, error);
            render();
            return;
        }
        if ((error instanceof RequestFailure && error.sent) || (error instanceof HttpFailure && error.requestSent)) {
            setUnknown(kind);
            return;
        }
        setText('error', kind === 'register'
            ? '登録画面を準備できませんでした。入力内容は送信されていません。'
            : '処理を準備できませんでした。操作を確認してから再度お試しください。');
        setText('status', 'サーバーへの変更要求は送信されていません。');
    }

    function validRegisterValues() {
        const invite = byId('invite').value.trim();
        const name = byId('name').value.trim();
        const email = byId('email').value.trim();
        if (!validText(invite, 128) || !validText(name, 60) || !validText(email, 254) ||
            !/^[^\s@]+@[^\s@]+$/u.test(email))
            throw new Error('招待コード・表示名・メールアドレスを確認してください。');
        return { invite, name, email };
    }

    async function submitRegistration(event) {
        event.preventDefault();
        if (!ready || busy || locked || signedIn || queued || enrollmentAvailable || confirmationToken) return;
        let payload;
        try { payload = validRegisterValues(); }
        catch (error) { setText('error', error.message); return; }

        const operation = begin('register');
        setText('error', '');
        setText('status', '登録依頼を準備しています。');
        try {
            const status = await readStatus(operation);
            ensureCurrent(operation);
            if (status.signedIn) { showSignedIn(); return; }
            if (status.enrolling) {
                setSessionChanged('別の登録確認セッションを検出しました。対象を特定できないため、登録依頼は送信していません。状態を読み取り専用で確認してください。');
                return;
            }
            const result = await request('/api/join/register', 'POST', payload, JOIN_REQUEST_LIMIT, operation, {
                csrf: true,
                beforeSend: currentOperation => verifySession(currentOperation, status)
            });
            ensureCurrent(operation);
            if (!validText(result.message, 500)) throw new RequestFailure(true, new Error('invalid response'));
            queued = true;
            clearFields();
            setText('error', '');
            setText('status', '確認メールの送信依頼を受け付けました。メールが届いたら、同じブラウザーで確認リンクを開いてください。');
            setWorkspaceVisible(false);
        } catch (error) {
            if (!current(operation)) return;
            handlePostFailure(operation, 'register', error);
        } finally {
            payload = null;
            finish(operation);
        }
    }

    async function submitConfirmation(event) {
        event.preventDefault();
        if (!ready || busy || locked || signedIn || !confirmationToken) return;
        let token = confirmationToken;
        confirmationToken = null;
        const body = { token };
        let stage = 'preflight';
        const operation = begin('confirm');
        setText('error', '');
        setText('status', 'メール確認を準備しています。');
        try {
            const status = await readStatus(operation);
            ensureCurrent(operation);
            if (status.signedIn) { token = null; body.token = ''; showSignedIn(); return; }
            stage = 'confirm';
            token = null;
            const result = await request('/api/join/confirm', 'POST', body, JOIN_REQUEST_LIMIT, operation, {
                csrf: true,
                beforeSend: currentOperation => verifySession(currentOperation, status)
            });
            body.token = '';
            token = null;
            ensureCurrent(operation);
            if (!validText(result.message, 500)) throw new RequestFailure(true, new Error('invalid response'));
            stage = 'post-confirm-status';
            const after = await readStatus(operation);
            ensureCurrent(operation);
            if (after.signedIn) { showSignedIn(); return; }
            if (!after.enrolling) {
                setSessionChanged('メール確認の応答後に登録確認セッションを確認できませんでした。パスキー登録は開始していません。');
                return;
            }
            enrollmentAvailable = true;
            setText('error', '');
            setText('status', 'メール確認が完了しました。登録先はサーバーの確認セッションにより決まります。内容を確認し、明示的にパスキー登録を開始してください。');
            setWorkspaceVisible(false);
        } catch (error) {
            body.token = '';
            if (stage === 'preflight' && !(error instanceof HttpFailure && (error.status === 401 || error.status === 403)) &&
                !(error instanceof RequestFailure && error.sent)) {
                confirmationToken = token;
            }
            token = null;
            if (!current(operation)) return;
            if (stage === 'post-confirm-status') {
                setUnknown('confirm');
                return;
            }
            handlePostFailure(operation, 'confirm', error);
        } finally {
            body.token = '';
            token = null;
            finish(operation);
        }
    }

    function decodeBase64Url(value) {
        if (!validText(value, 8192) || !/^[A-Za-z0-9_-]+$/u.test(value))
            throw new Error('パスキー設定を確認できません。');
        const base64 = value.replace(/-/gu, '+').replace(/_/gu, '/') + '='.repeat((4 - value.length % 4) % 4);
        let binary;
        try { binary = window.atob(base64); }
        catch { throw new Error('パスキー設定を確認できません。'); }
        const bytes = new Uint8Array(binary.length);
        for (let index = 0; index < binary.length; index++) bytes[index] = binary.charCodeAt(index);
        return bytes.buffer;
    }

    function creationOptions(value) {
        if (!isObject(value)) throw new Error('パスキー設定を確認できません。');
        const parser = window.PublicKeyCredential?.parseCreationOptionsFromJSON;
        if (typeof parser === 'function') {
            let parsed;
            try { parsed = parser.call(window.PublicKeyCredential, value); }
            catch { throw new Error('パスキー設定を確認できません。'); }
            if (!isObject(parsed) || !(parsed.challenge instanceof ArrayBuffer || ArrayBuffer.isView(parsed.challenge)))
                throw new Error('パスキー設定を確認できません。');
            parsed.timeout = Number.isFinite(parsed.timeout) && parsed.timeout > 0
                ? Math.min(parsed.timeout, NATIVE_TIMEOUT_MS) : NATIVE_TIMEOUT_MS;
            return parsed;
        }
        if (typeof value.challenge !== 'string' || !isObject(value.user) ||
            typeof value.user.id !== 'string' || !isObject(value.rp) ||
            typeof value.rp.name !== 'string' || typeof value.user.name !== 'string' ||
            typeof value.user.displayName !== 'string')
            throw new Error('パスキー設定を確認できません。');
        const parsed = { ...value, challenge: decodeBase64Url(value.challenge), user: { ...value.user, id: decodeBase64Url(value.user.id) } };
        if (value.excludeCredentials !== undefined) {
            if (!Array.isArray(value.excludeCredentials) || value.excludeCredentials.length > 100)
                throw new Error('パスキー設定を確認できません。');
            parsed.excludeCredentials = value.excludeCredentials.map(item => {
                if (!isObject(item) || typeof item.id !== 'string') throw new Error('パスキー設定を確認できません。');
                return { ...item, id: decodeBase64Url(item.id) };
            });
        }
        parsed.timeout = Number.isFinite(parsed.timeout) && parsed.timeout > 0
            ? Math.min(parsed.timeout, NATIVE_TIMEOUT_MS) : NATIVE_TIMEOUT_MS;
        return parsed;
    }

    function encodeBytes(value) {
        if (value == null) return null;
        const bytes = new Uint8Array(value);
        let binary = '';
        for (let offset = 0; offset < bytes.length; offset += 0x8000)
            binary += String.fromCharCode(...bytes.subarray(offset, offset + 0x8000));
        return window.btoa(binary).replace(/\+/gu, '-').replace(/\//gu, '_').replace(/=+$/gu, '');
    }

    function serializeRegistration(credential) {
        if (typeof credential?.toJSON === 'function') {
            const result = credential.toJSON();
            if (isObject(result)) return result;
            throw new Error('パスキー応答を確認できません。');
        }
        const response = credential?.response;
        if (!response || !credential.rawId || !response.clientDataJSON || !response.attestationObject)
            throw new Error('パスキー応答を確認できません。');
        return {
            id: credential.id,
            rawId: encodeBytes(credential.rawId),
            type: credential.type,
            authenticatorAttachment: credential.authenticatorAttachment,
            response: {
                clientDataJSON: encodeBytes(response.clientDataJSON),
                attestationObject: encodeBytes(response.attestationObject),
                transports: response.getTransports?.() || []
            },
            clientExtensionResults: credential.getClientExtensionResults?.() || {}
        };
    }

    async function createNativeCredential(publicKey, operation) {
        if (!('PublicKeyCredential' in window) || !window.navigator?.credentials?.create)
            throw new Error('このブラウザーではパスキー登録を利用できません。');
        const options = creationOptions(publicKey);
        const controller = new AbortController();
        const nativeRecord = { controller, timer: null };
        nativeControllers.add(nativeRecord);
        let credential = null;
        let timedOut = false;
        try {
            const nativePromise = Promise.resolve(window.navigator.credentials.create({
                publicKey: options,
                signal: controller.signal
            }));
            const timeoutPromise = new Promise((_, reject) => {
                nativeRecord.timer = window.setTimeout(() => {
                    timedOut = true;
                    controller.abort();
                    reject(new Error('パスキー操作の時間が上限を超えました。'));
                }, Math.min(options.timeout, NATIVE_TIMEOUT_MS));
            });
            credential = await Promise.race([nativePromise, timeoutPromise]);
            ensureCurrent(operation);
            if (timedOut || controller.signal.aborted || !credential)
                throw new Error('パスキー操作を完了できませんでした。');
            const serialized = JSON.stringify(serializeRegistration(credential));
            if (serialized.length > 50_000) throw new Error('パスキー応答が上限を超えています。');
            return serialized;
        } finally {
            if (nativeRecord.timer !== null) window.clearTimeout(nativeRecord.timer);
            controller.abort();
            nativeControllers.delete(nativeRecord);
            credential = null;
        }
    }

    async function submitPasskey(event) {
        event.preventDefault();
        if (!ready || busy || locked || signedIn || !enrollmentAvailable ||
            byId('consent').checked !== true || confirmationToken) return;
        const operation = begin('passkey');
        let credentialJson = null;
        let body = null;
        let stage = 'preflight';
        setText('error', '');
        setText('status', '登録確認セッションを読み取っています。');
        try {
            let status = await readStatus(operation);
            ensureCurrent(operation);
            if (status.signedIn) { showSignedIn(); return; }
            if (!status.enrolling) {
                enrollmentAvailable = false;
                setText('error', '登録確認セッションが見つかりません。確認リンクを同じブラウザーで開き直してください。');
                setText('status', 'パスキー操作は開始していません。');
                return;
            }
            stage = 'options';
            const options = await request('/api/passkey-options', 'POST', {}, JOIN_REQUEST_LIMIT, operation, {
                csrf: true,
                beforeSend: currentOperation => verifySession(currentOperation, status)
            });
            ensureCurrent(operation);
            stage = 'after-options';
            status = await readStatus(operation);
            ensureCurrent(operation);
            if (status.signedIn) { showSignedIn(); return; }
            if (!status.enrolling) {
                enrollmentAvailable = false;
                setText('error', '登録確認セッションが変わりました。パスキー操作は開始していません。');
                setText('status', '登録確認セッションを開き直してください。');
                return;
            }
            setText('status', 'ブラウザーのパスキー登録を明示的に開始してください。');
            stage = 'native';
            credentialJson = await createNativeCredential(options, operation);
            ensureCurrent(operation);
            stage = 'after-native';
            status = await readStatus(operation);
            ensureCurrent(operation);
            if (status.signedIn) { showSignedIn(); return; }
            if (!status.enrolling) {
                credentialJson = null;
                enrollmentAvailable = false;
                setText('error', '登録確認セッションが変わりました。パスキー応答は送信していません。');
                setText('status', '登録確認セッションを開き直してください。');
                return;
            }
            body = { credential: credentialJson };
            credentialJson = null;
            stage = 'complete';
            const result = await request('/api/passkey-complete', 'POST', body, PASSKEY_REQUEST_LIMIT, operation, {
                csrf: true,
                beforeSend: currentOperation => verifySession(currentOperation, status)
            });
            body.credential = '';
            ensureCurrent(operation);
            if (!validText(result.message, 500)) throw new RequestFailure(true, new Error('invalid response'));
            completed = true;
            enrollmentAvailable = false;
            body = null;
            stage = 'after-complete';
            const finalStatus = await readStatus(operation);
            ensureCurrent(operation);
            if (finalStatus.signedIn) {
                setText('status', 'パスキー登録が完了し、ログイン状態を確認しました。ワークスペースへ移動します。');
                window.location.replace('/workspace');
                return;
            }
            locked = true;
            unknownKind = 'complete';
            setText('status', 'パスキー登録の応答は成功しましたが、ログイン状態を確認できません。自動再送はせず、読み取り専用で状態を確認してください。');
        } catch (error) {
            if (!current(operation)) return;
            if (stage === 'preflight' || stage === 'after-options' || stage === 'after-native') {
                credentialJson = null;
                setSessionChanged(stage === 'after-native'
                    ? '登録確認セッションを再確認できません。パスキー応答は送信していません。'
                    : '登録確認セッションを再確認できません。パスキー操作は開始していません。');
            } else if (stage === 'after-complete') {
                setUnknown('complete');
            } else if (stage === 'options' || stage === 'complete') {
                handlePostFailure(operation, stage, error);
            } else {
                setText('error', 'パスキー登録を完了できませんでした。必要なら、登録確認セッションを読み取り専用で確認してください。');
                setText('status', 'パスキー登録は完了していません。必要な場合は、明示的にもう一度操作してください。');
            }
        } finally {
            if (body) body.credential = '';
            body = null;
            credentialJson = null;
            finish(operation);
        }
    }

    function onConsentChanged() { render(); }

    function cleanup() {
        if (disposed) return;
        disposed = true;
        generation++;
        if (active?.controller) active.controller.abort();
        active = null;
        for (const record of nativeControllers) {
            try { record.controller.abort(); } catch { /* native dialog may already be closed */ }
            if (record.timer !== null) window.clearTimeout(record.timer);
        }
        nativeControllers.clear();
        clearSecrets();
        ready = false;
        busy = false;
        locked = true;
    }

    async function initialize() {
        if (invalidFragmentToken) {
            setText('error', '確認リンクを安全に処理できません。リンクを開き直してください。');
            setText('status', 'リンクの確認を中止しました。');
            setHidden('initError', false);
            render();
            clearSecrets();
            return;
        }
        const operation = begin('initialize');
        try {
            const csrf = await request('/api/csrf', 'GET', null, CSRF_LIMIT, operation);
            ensureCurrent(operation);
            if (!validText(csrf.token, 8192)) throw new Error('CSRF応答を確認できません。');
            const status = await readStatus(operation);
            ensureCurrent(operation);
            ready = true;
            if (status.signedIn) {
                showSignedIn();
            } else if (confirmationToken) {
                enrollmentAvailable = false;
                setText('status', '確認リンクを読み込みました。内容を確認してボタンを押してください。');
                inviteFromFragment = null;
            } else if (status.enrolling) {
                enrollmentAvailable = true;
                inviteFromFragment = null;
                clearFields();
                setText('status', '登録確認セッションがあります。ここでは登録対象を特定できません。ご自身が確認を始めたセッションであることを確認してから続行してください。');
            } else {
                if (inviteFromFragment) byId('invite').value = inviteFromFragment;
                inviteFromFragment = null;
                setText('status', '招待コードと登録情報を入力して確認メールを依頼してください。');
            }
            setHidden('initError', true);
            setText('error', '');
        } catch {
            if (current(operation)) {
                ready = false;
                locked = true;
                clearSecrets();
                setHidden('initError', false);
                setText('initError', '登録画面を初期化できませんでした。安全のため操作を無効にしています。ページを再読み込みしてください。');
                setText('status', '登録準備に失敗しました。');
                render();
            }
        } finally {
            finish(operation);
        }
    }

    if (invalidFragmentToken) {
        setHidden('initError', false);
        setText('initError', '確認リンクを安全に処理できません。リンクを開き直してください。');
    }
    byId('registerForm').addEventListener('submit', submitRegistration);
    byId('confirmForm').addEventListener('submit', submitConfirmation);
    byId('passkeyForm').addEventListener('submit', submitPasskey);
    byId('consent').addEventListener('change', onConsentChanged);
    byId('reconcile').addEventListener('click', readOnlyReconcile);
    window.addEventListener('pagehide', cleanup, { once: true });
    window.addEventListener('pageshow', event => {
        if (event.persisted) window.location.reload();
    });

    render();
    void initialize();
})();