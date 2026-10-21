(() => {
    'use strict';

    const DEFAULT_RETURN_PATH = '/workspace';
    const REQUEST_TIMEOUT_MS = 25_000;
    const RESPONSE_LIMIT_BYTES = 1_048_576;
    const REQUEST_LIMIT_BYTES = 65_536;
    const form = document.getElementById('auth-form');
    const fields = document.getElementById('auth-fields');
    const emailInput = document.getElementById('auth-email');
    const submitButton = document.getElementById('auth-submit');
    const statusNode = document.getElementById('auth-status');
    const errorNode = document.getElementById('auth-error');
    const reconcileButton = document.getElementById('auth-reconcile');

    if (!form || !fields || !emailInput || !submitButton || !statusNode || !errorNode || !reconcileButton) return;

    let csrfToken = null;
    let generation = 0;
    let activeOperation = null;
    let phase = 'initializing';
    let unknownOutcome = false;
    let disposed = false;
    let navigating = false;
    let returnPath = DEFAULT_RETURN_PATH;

    class ApiFailure extends Error {
        constructor(message, status) {
            super(message);
            this.name = 'ApiFailure';
            this.status = status;
        }
    }

    function showError(message) {
        errorNode.textContent = String(message || '').slice(0, 400);
    }

    function setStatus(message) {
        statusNode.textContent = String(message || '').slice(0, 400);
    }

    function setControlsDisabled(disabled) {
        fields.disabled = disabled;
        submitButton.disabled = disabled;
        form.setAttribute('aria-busy', disabled ? 'true' : 'false');
    }

    function isUnsafeEncoding(value) {
        let candidate = value;
        for (let i = 0; i < 5; i++) {
            if (/[\\\u0000-\u001f\u007f-\u009f]/u.test(candidate) || candidate.includes('#') ||
                /%(?![0-9a-f]{2})|%(?:2f|5c|0[0-9a-f]|1[0-9a-f]|7f)/iu.test(candidate)) return true;
            let decoded;
            try { decoded = decodeURIComponent(candidate); }
            catch { return true; }
            if (decoded === candidate) return false;
            candidate = decoded;
        }
        return /[%\\\u0000-\u001f\u007f-\u009f#]/u.test(candidate);
    }

    function canonicalPathname(pathname) {
        let candidate = pathname;
        for (let i = 0; i < 5; i++) {
            if (!candidate.startsWith('/') || candidate.startsWith('//') ||
                /[\\\u0000-\u001f\u007f-\u009f]/u.test(candidate) ||
                /%(?![0-9a-f]{2})|%(?:2f|5c|0[0-9a-f]|1[0-9a-f]|7f)/iu.test(candidate)) return null;
            let decoded;
            try { decoded = decodeURIComponent(candidate); }
            catch { return null; }
            if (decoded === candidate) return candidate;
            candidate = decoded;
        }
        // Fail closed when nested escaping exceeds the bounded canonicalization depth.
        if (/%[0-9a-f]{2}/iu.test(candidate) || !candidate.startsWith('/') || candidate.startsWith('//') ||
            /[\\\u0000-\u001f\u007f-\u009f]/u.test(candidate)) return null;
        return candidate;
    }

    function safeReturnPath(raw) {
        if (typeof raw !== 'string' || raw.length === 0 || raw.length > 2048 || raw[0] !== '/' ||
            raw.startsWith('//') || isUnsafeEncoding(raw)) return DEFAULT_RETURN_PATH;
        let target;
        try { target = new URL(raw, window.location.origin); }
        catch { return DEFAULT_RETURN_PATH; }
        if (target.origin !== window.location.origin || target.username || target.password || target.hash ||
            !target.pathname.startsWith('/') || target.pathname.startsWith('//')) return DEFAULT_RETURN_PATH;
        const canonical = canonicalPathname(target.pathname);
        if (canonical === null) return DEFAULT_RETURN_PATH;
        const first = canonical.split('/').filter(Boolean)[0] || '';
        if (['auth', 'api', 'account'].includes(first.toLowerCase())) return DEFAULT_RETURN_PATH;
        // Only the approved local path is carried forward; query and fragment are dropped.
        return target.pathname;
    }

    function getReturnPath() {
        try {
            const params = new URLSearchParams(window.location.search);
            const values = params.getAll('returnUrl');
            return values.length === 1 ? safeReturnPath(values[0]) : DEFAULT_RETURN_PATH;
        } catch {
            return DEFAULT_RETURN_PATH;
        }
    }

    function beginOperation(nextPhase) {
        if (activeOperation) activeOperation.controller.abort();
        const operation = { id: ++generation, controller: new AbortController() };
        activeOperation = operation;
        phase = nextPhase;
        return operation;
    }

    function isCurrent(operation) {
        return !disposed && activeOperation === operation && operation.id === generation;
    }

    function ensureCurrent(operation) {
        if (!isCurrent(operation)) throw new DOMException('Stale operation', 'AbortError');
    }

    async function readBoundedJson(response, operation) {
        if (!response || !response.headers || !response.body) throw new Error('応答を読み取れませんでした。');
        const contentType = response.headers.get('content-type') || '';
        if (!/^application\/(?:[a-z0-9.+-]*\+)?json(?:\s*;|\s*$)/i.test(contentType))
            throw new Error('応答形式を確認できませんでした。');
        const lengthHeader = response.headers.get('content-length');
        if (lengthHeader && /^\d+$/.test(lengthHeader) && Number(lengthHeader) > RESPONSE_LIMIT_BYTES)
            throw new Error('応答サイズが上限を超えています。');

        const reader = response.body.getReader();
        const chunks = [];
        let total = 0;
        try {
            while (true) {
                ensureCurrent(operation);
                const result = await reader.read();
                ensureCurrent(operation);
                if (result.done) break;
                total += result.value.byteLength;
                if (total > RESPONSE_LIMIT_BYTES) {
                    await reader.cancel();
                    throw new Error('応答サイズが上限を超えています。');
                }
                chunks.push(result.value);
            }
        } finally {
            try { reader.releaseLock(); } catch { /* stream may already be closed */ }
        }
        const bytes = new Uint8Array(total);
        let offset = 0;
        for (const chunk of chunks) { bytes.set(chunk, offset); offset += chunk.byteLength; }
        const text = new TextDecoder('utf-8', { fatal: true }).decode(bytes);
        const value = JSON.parse(text);
        if (!value || typeof value !== 'object' || Array.isArray(value)) throw new Error('応答形式を確認できませんでした。');
        return value;
    }

    async function api(action, method, body, operation) {
        if (!['csrf', 'login-options', 'login', 'status'].includes(action)) throw new Error('未対応の操作です。');
        const endpoint = new URL(`/api/${action}`, window.location.origin);
        if (endpoint.origin !== window.location.origin) throw new Error('接続先を確認できませんでした。');
        const headers = new Headers();
        const init = {
            method,
            // Keep the browser-generated Origin with no-referrer, including WebKit.
            // Destination is still an allowlisted same-origin path and redirects are rejected.
            mode: 'cors',
            credentials: 'same-origin',
            cache: 'no-store',
            redirect: 'error',
            headers,
            signal: operation.controller.signal
        };
        if (method === 'POST') {
            if (typeof csrfToken !== 'string' || csrfToken.length === 0 || csrfToken.length > 8192)
                throw new Error('ページを再読み込みして、もう一度お試しください。');
            headers.set('Content-Type', 'application/json');
            headers.set('X-CSRF-TOKEN', csrfToken);
            const encoded = JSON.stringify(body);
            if (new TextEncoder().encode(encoded).byteLength > REQUEST_LIMIT_BYTES)
                throw new Error('送信データが上限を超えています。');
            init.body = encoded;
        }

        const timer = window.setTimeout(() => operation.controller.abort(), REQUEST_TIMEOUT_MS);
        try {
            const response = await window.fetch(endpoint.pathname, init);
            ensureCurrent(operation);
            const value = await readBoundedJson(response, operation);
            if (!response.ok) {
                const message = typeof value.error === 'string' && value.error.length <= 400
                    ? value.error : '処理を完了できませんでした。';
                throw new ApiFailure(message, response.status);
            }
            return value;
        } finally {
            window.clearTimeout(timer);
        }
    }

    function base64UrlToBytes(value) {
        if (typeof value !== 'string' || !/^[A-Za-z0-9_-]+$/.test(value)) throw new Error('パスキー設定を読み取れませんでした。');
        const base64 = value.replace(/-/g, '+').replace(/_/g, '/') + '='.repeat((4 - value.length % 4) % 4);
        const binary = window.atob(base64);
        const result = new Uint8Array(binary.length);
        for (let i = 0; i < binary.length; i++) result[i] = binary.charCodeAt(i);
        return result.buffer;
    }

    function requestOptions(value) {
        if (typeof window.PublicKeyCredential?.parseRequestOptionsFromJSON === 'function') {
            try { return window.PublicKeyCredential.parseRequestOptionsFromJSON(value); }
            catch { throw new Error('パスキー設定を読み取れませんでした。'); }
        }
        const options = { ...value, challenge: base64UrlToBytes(value.challenge) };
        if (Array.isArray(value.allowCredentials)) {
            options.allowCredentials = value.allowCredentials.map(item => ({ ...item, id: base64UrlToBytes(item.id) }));
        }
        if (Number.isFinite(options.timeout)) options.timeout = Math.min(options.timeout, 300_000);
        else options.timeout = 300_000;
        return options;
    }

    function bytesToBase64Url(value) {
        if (value == null) return null;
        const bytes = new Uint8Array(value);
        let binary = '';
        for (let offset = 0; offset < bytes.length; offset += 0x8000)
            binary += String.fromCharCode(...bytes.subarray(offset, offset + 0x8000));
        return window.btoa(binary).replace(/\+/g, '-').replace(/\//g, '_').replace(/=+$/g, '');
    }

    function serializeCredential(credential) {
        if (credential && typeof credential.toJSON === 'function') {
            const value = credential.toJSON();
            if (value && typeof value === 'object' && !Array.isArray(value)) return value;
            throw new Error('パスキー応答を読み取れませんでした。');
        }
        const response = credential?.response;
        if (!response) throw new Error('パスキー応答を読み取れませんでした。');
        const serialized = { clientDataJSON: bytesToBase64Url(response.clientDataJSON) };
        if (response.authenticatorData && response.signature) {
            serialized.authenticatorData = bytesToBase64Url(response.authenticatorData);
            serialized.signature = bytesToBase64Url(response.signature);
            serialized.userHandle = bytesToBase64Url(response.userHandle);
        } else {
            throw new Error('パスキー応答を読み取れませんでした。');
        }
        return {
            id: credential.id,
            rawId: bytesToBase64Url(credential.rawId),
            type: credential.type,
            authenticatorAttachment: credential.authenticatorAttachment,
            response: serialized,
            clientExtensionResults: credential.getClientExtensionResults?.() || {}
        };
    }

    function enableForm() {
        if (disposed || unknownOutcome || navigating) return;
        phase = 'ready';
        setControlsDisabled(false);
        reconcileButton.hidden = true;
        reconcileButton.disabled = true;
    }

    function lockUnknownOutcome() {
        unknownOutcome = true;
        phase = 'unknown';
        setControlsDisabled(true);
        reconcileButton.hidden = false;
        reconcileButton.disabled = false;
        showError('ログイン要求の結果を確認できませんでした。安全のため再送信は行いません。ログイン状態を手動で確認してください。');
        setStatus('通信結果が不明です。ログイン状態を読み取り専用で確認できます。');
    }

    function redirectAfterLogin() {
        navigating = true;
        setControlsDisabled(true);
        setStatus('ログインしました。移動しています。');
        showError('');
        window.location.replace(returnPath);
    }

    function emailValue() {
        const value = emailInput.value.trim();
        if (!value || value.length > 254 || /[\u0000-\u001f\u007f-\u009f]/u.test(value) || !/^[^\s@]+@[^\s@]+$/.test(value))
            throw new Error('登録済みメールアドレスを確認してください。');
        return value;
    }

    function friendlyError(error) {
        if (error?.name === 'NotAllowedError' || error?.name === 'AbortError') return 'パスキー操作を完了できませんでした。必要に応じて、もう一度お試しください。';
        return error instanceof Error ? error.message.slice(0, 400) : 'ログインできませんでした。';
    }

    async function submitLogin(event) {
        event.preventDefault();
        if (phase !== 'ready' || unknownOutcome || disposed || navigating) return;
        let capturedEmail;
        try { capturedEmail = emailValue(); }
        catch (error) { showError(friendlyError(error)); emailInput.focus(); return; }

        const operation = beginOperation('busy');
        operation.loginPostStarted = false;
        setControlsDisabled(true);
        showError('');
        setStatus('ログインを準備しています。パスキーの確認は送信ボタンを押した後に開始されます。');
        let credential = null;
        let serializedCredential = null;
        try {
            const optionsJson = await api('login-options', 'POST', { email: capturedEmail }, operation);
            ensureCurrent(operation);
            if (!('PublicKeyCredential' in window) || !window.navigator?.credentials?.get)
                throw new Error('このブラウザーではパスキーを利用できません。');
            const publicKey = requestOptions(optionsJson);
            if (!publicKey || !(publicKey.challenge instanceof ArrayBuffer) || !publicKey.rpId && !window.location.hostname)
                throw new Error('パスキー設定を読み取れませんでした。');
            publicKey.timeout = Number.isFinite(publicKey.timeout) ? Math.min(publicKey.timeout, 300_000) : 300_000;
            credential = await window.navigator.credentials.get({ publicKey, signal: operation.controller.signal });
            ensureCurrent(operation);
            if (!credential) throw new Error('パスキー操作が取り消されました。');
            serializedCredential = JSON.stringify(serializeCredential(credential));
            if (serializedCredential.length > 50_000) throw new Error('パスキー応答が上限を超えています。');
            operation.loginPostStarted = true;
            const result = await api('login', 'POST', { credential: serializedCredential }, operation);
            ensureCurrent(operation);
            if (typeof result.message !== 'string') throw new Error('ログイン応答を確認できませんでした。');
            redirectAfterLogin();
        } catch (error) {
            if (!isCurrent(operation)) return;
            if (operation.loginPostStarted) {
                if (error instanceof ApiFailure && (error.status === 400 || error.status === 403)) {
                    showError(friendlyError(error));
                    setStatus('ログインできませんでした。登録済みメールアドレスを確認して、必要な場合のみ再度操作してください。');
                } else {
                    lockUnknownOutcome();
                }
            } else {
                showError(friendlyError(error));
                setStatus('ログインできませんでした。入力を確認して、必要な場合のみ再度操作してください。');
            }
        } finally {
            credential = null;
            serializedCredential = null;
            if (activeOperation === operation) {
                activeOperation = null;
                if (!unknownOutcome && !navigating && !disposed) enableForm();
                else if (unknownOutcome) {
                    setControlsDisabled(true);
                    reconcileButton.hidden = false;
                    reconcileButton.disabled = false;
                }
            }
        }
    }

    async function reconcileStatus() {
        if (!unknownOutcome || phase === 'reconciling' || disposed) return;
        const operation = beginOperation('reconciling');
        reconcileButton.disabled = true;
        setStatus('現在のログイン状態を読み取り専用で確認しています。');
        try {
            const result = await api('status', 'GET', null, operation);
            ensureCurrent(operation);
            if (result.user && typeof result.user.id === 'string' && result.user.id.length > 0) {
                redirectAfterLogin();
                return;
            }
            setStatus('ログイン状態は確認できませんでした。ログイン要求は再送信していません。時間をおいてからページを再読み込みしてください。');
        } catch {
            if (isCurrent(operation)) setStatus('状態を確認できませんでした。ログイン要求は再送信していません。時間をおいてからページを再読み込みしてください。');
        } finally {
            if (activeOperation === operation) {
                activeOperation = null;
                phase = 'unknown';
                reconcileButton.hidden = false;
                reconcileButton.disabled = false;
                setControlsDisabled(true);
            }
        }
    }

    function cleanup() {
        if (disposed) return;
        disposed = true;
        generation++;
        if (activeOperation) activeOperation.controller.abort();
        activeOperation = null;
        csrfToken = null;
    }

    async function initialize() {
        returnPath = getReturnPath();
        const operation = beginOperation('initializing');
        try {
            const result = await api('csrf', 'GET', null, operation);
            ensureCurrent(operation);
            if (typeof result.token !== 'string' || result.token.length === 0 || result.token.length > 8192)
                throw new Error('CSRF トークンを取得できませんでした。');
            csrfToken = result.token;
            showError('');
            setStatus('登録済みメールアドレスを入力し、ボタンを押してパスキーでログインしてください。');
            phase = 'ready';
            setControlsDisabled(false);
        } catch {
            if (isCurrent(operation)) {
                phase = 'failed';
                setControlsDisabled(true);
                showError('ログイン画面を初期化できませんでした。安全のため操作を無効にしています。ページを再読み込みしてください。');
                setStatus('ログイン準備に失敗しました。');
            }
        } finally {
            if (activeOperation === operation) activeOperation = null;
        }
    }

    form.addEventListener('submit', submitLogin);
    reconcileButton.addEventListener('click', reconcileStatus);
    window.addEventListener('pagehide', cleanup, { once: true });
    window.addEventListener('pageshow', event => {
        if (event.persisted) window.location.reload();
    });
    void initialize();
})();
