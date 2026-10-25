const API = '/api/';
const JSON_LIMIT = 1024 * 1024;
const CSRF_LIMIT = 64 * 1024;
const REQUEST_TIMEOUT_MS = 25_000;
const CREDENTIAL_TIMEOUT_MS = 5 * 60_000;
const MASK_MARKER = /[*•…⋯·×]/u;
const ids = {
    root: 'recovery-admin-root', status: 'recovery-admin-status', error: 'recovery-admin-error',
    refresh: 'recovery-admin-refresh', panel: 'recovery-admin-panel', lookupForm: 'recovery-admin-lookup-form',
    targetId: 'recovery-admin-target-id', loadTarget: 'recovery-admin-load-target',
    targetSummary: 'recovery-admin-target-summary', targetName: 'recovery-admin-target-name',
    recipient: 'recovery-admin-target-recipient', enabled: 'recovery-admin-target-enabled',
    recovery: 'recovery-admin-target-recovery', expiry: 'recovery-admin-target-expiry',
    actionsForm: 'recovery-admin-actions-form', reason: 'recovery-admin-reason',
    confirm: 'recovery-admin-confirm', start: 'recovery-admin-start', resend: 'recovery-admin-resend',
    readonly: 'recovery-admin-readonly'
};

let ownerRoot = null;
let ownerGeneration = 0;
let actorGeneration = 0;
let selectionGeneration = 0;
let actorId = null;
let target = null;
let busy = false;
let ready = false;
let writeOutcomeUnknown = false;
let activeRequests = new Set();
let activeCredentials = new Set();
let listeners = null;
let targetExpiryTimer = null;

const node = name => ownerRoot?.querySelector(`#${ids[name]}`) ?? null;
const isObject = value => value !== null && typeof value === 'object' && !Array.isArray(value);
const hasControls = value => /[\u0000-\u001f\u007f]/u.test(value);
const validText = (value, max) => typeof value === 'string' && value.length > 0 && value.length <= max && !hasControls(value);
const validActor = value => validText(value, 128);
const validOperation = value => validText(value, 128);
const validTargetId = value => validText(value, 128);

function expiredRecoveryRecord(value) {
    return value?.enabled === false && value.recovering === true && validOperation(value.operationId) &&
        typeof value.expiresAt === 'string' && Number.isFinite(Date.parse(value.expiresAt)) &&
        Date.parse(value.expiresAt) <= Date.now();
}

function canStartRecovery(value) {
    return Boolean(value && ((value.enabled === true && value.recovering === false) || expiredRecoveryRecord(value)));
}

function canResendRecovery(value) {
    return Boolean(value && value.enabled === false && value.recovering === true &&
        validOperation(value.operationId) && typeof value.expiresAt === 'string' &&
        Number.isFinite(Date.parse(value.expiresAt)) && Date.parse(value.expiresAt) > Date.now());
}

function clearTargetExpiryTimer() {
    if (targetExpiryTimer !== null) clearTimeout(targetExpiryTimer);
    targetExpiryTimer = null;
}

function scheduleTargetExpiry() {
    clearTargetExpiryTimer();
    if (!target?.recovering || !target.expiresAt) return;
    const deadline = Date.parse(target.expiresAt);
    if (!Number.isFinite(deadline)) return;
    const expiringTarget = target;
    const delay = Math.max(0, deadline - Date.now() + 1);
    targetExpiryTimer = setTimeout(() => {
        targetExpiryTimer = null;
        if (!currentOwner() || target !== expiringTarget) return;
        if (Date.parse(target.expiresAt ?? '') > Date.now()) {
            scheduleTargetExpiry();
            return;
        }
        if (node('confirm')) node('confirm').checked = false;
        render();
        setText('status', '復旧期限を過ぎました。再送は停止し、復旧開始はサーバーで再確認します。');
    }, Math.min(delay, 2_147_000_000));
}

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
    setHidden('panel', !ready);
    setHidden('refresh', !ready);
    setHidden('targetSummary', !target);
    setHidden('readonly', !writeOutcomeUnknown);
    const targetIdInput = node('targetId');
    const reasonInput = node('reason');
    const confirmation = node('confirm');
    const hasTarget = Boolean(target && targetIdInput?.value === target.id);
    const canWrite = ready && validActor(actorId) && hasTarget && !busy && !writeOutcomeUnknown;
    const reasonOk = typeof reasonInput?.value === 'string' && reasonInput.value.trim().length >= 3 &&
        reasonInput.value.length <= 500 && !hasControls(reasonInput.value);
    const startEligible = canStartRecovery(target);
    if (reasonInput) reasonInput.disabled = !ready || !hasTarget || !startEligible || busy || writeOutcomeUnknown;
    const startAllowed = canWrite && startEligible && reasonOk && confirmation?.checked === true;
    const resendAllowed = canWrite && canResendRecovery(target);
    if (node('start')) node('start').disabled = !startAllowed;
    if (node('resend')) node('resend').disabled = !resendAllowed;
    if (node('loadTarget')) node('loadTarget').disabled = !ready || busy || !validTargetId(targetIdInput?.value);
    if (node('refresh')) node('refresh').disabled = busy;
    if (target) {
        setText('targetName', target.displayName);
        setText('recipient', target.maskedRecipient);
        setText('enabled', target.enabled ? '有効' : '無効');
        setText('recovery', expiredRecoveryRecord(target) ? '復旧期限切れ（新規開始可能）' :
            target.recovering ? '復旧処理中' : '復旧処理なし');
        const expiry = node('expiry');
        if (target.expiresAt && expiry) {
            expiry.textContent = `復旧期限：${new Date(target.expiresAt).toLocaleString('ja-JP', { timeZone: 'Asia/Tokyo' })}`;
            expiry.hidden = false;
        } else if (expiry) {
            expiry.textContent = '';
            expiry.hidden = true;
        }
    }
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

async function getCsrf(record) {
    const response = await fetch(`${API}csrf`, {
        method: 'GET', credentials: 'same-origin', mode: 'same-origin', cache: 'no-store',
        redirect: 'error', headers: { Accept: 'application/json' }, signal: record.controller.signal
    });
    if (!response.ok || response.redirected) {
        await response.body?.cancel();
        const error = new Error(response.status === 401 || response.status === 403
            ? 'セッションまたは権限を確認できません。状態を再取得してください。'
            : 'CSRF確認に失敗しました。状況を再取得してください。');
        error.status = response.status;
        error.retryAfterMs = retryAfterMs(response.headers.get('Retry-After'));
        throw error;
    }
    const value = await boundedJson(response, CSRF_LIMIT);
    if (!isObject(value) || !validText(value.token, 8192)) throw new Error('CSRFトークンを確認できません。');
    return value.token;
}

function snapshotCurrent(snapshot) {
    if (!snapshot) return currentOwner();
    return currentOwner(snapshot.ownerGeneration) && snapshot.actorGeneration === actorGeneration &&
        snapshot.selectionGeneration === selectionGeneration && snapshot.actorId === actorId &&
        target === snapshot.targetSnapshot &&
        node('targetId')?.value === snapshot.targetId &&
        (!snapshot.requireReason || node('reason')?.value.trim() === snapshot.reason) &&
        (!snapshot.requireConfirmation || node('confirm')?.checked === true);
}

function snapshotActionEligible(snapshot) {
    return !snapshot?.kind || (snapshot.kind === 'start'
        ? canStartRecovery(target)
        : snapshot.kind === 'resend' && canResendRecovery(target));
}

async function request(path, { body = null, targetId = null, snapshot = null } = {}) {
    const mine = ownerGeneration;
    const actorMine = actorGeneration;
    const expectedActor = body === null ? null : actorId;
    if (body !== null && !validActor(expectedActor)) throw new Error('管理者セッションを確認してください。');
    const record = { controller: new AbortController(), postStarted: false };
    activeRequests.add(record);
    const timer = setTimeout(() => record.controller.abort(), REQUEST_TIMEOUT_MS);
    try {
        const method = body === null ? 'GET' : 'POST';
        const url = new URL(API + path, location.origin);
        if (body === null && targetId !== null) url.searchParams.append('id', targetId);
        if (body !== null) {
            url.searchParams.append('expectedActor', expectedActor);
            if (url.searchParams.getAll('expectedActor').length !== 1)
                throw new Error('管理者確認パラメーターが不正です。');
        }
        const headers = { Accept: 'application/json' };
        const init = {
            method, credentials: 'same-origin', mode: 'same-origin', cache: 'no-store',
            redirect: 'error', headers, signal: record.controller.signal
        };
        if (body !== null) {
            const csrfToken = await getCsrf(record);
            if (!currentOwner(mine) || !snapshotCurrent(snapshot) || actorGeneration !== actorMine || actorId !== expectedActor)
                throw new Error('入力または管理者セッションが変わりました。操作をやり直してください。');
            if (!snapshotActionEligible(snapshot))
                throw new Error('復旧対象の状態または期限が変わりました。状態を再取得してください。');
            const text = JSON.stringify(body);
            if (new TextEncoder().encode(text).byteLength > JSON_LIMIT) throw new Error('送信内容が上限を超えています。');
            headers['Content-Type'] = 'application/json';
            headers['X-CSRF-TOKEN'] = csrfToken;
            init.body = text;
            record.postStarted = true;
        }
        if (!currentOwner(mine) || !snapshotCurrent(snapshot)) throw new Error('画面状態が変わりました。');
        const response = await fetch(url, init);
        if (!response.ok || response.redirected) {
            const error = new Error(response.status === 401 || response.status === 403
                ? 'セッションまたは管理者権限を確認してください。'
                : `処理に失敗しました（${response.status}）。`);
            error.status = response.status;
            error.retryAfterMs = retryAfterMs(response.headers.get('Retry-After'));
            await response.body?.cancel();
            if ((response.status === 401 || response.status === 403) && currentOwner(mine)) clearActor();
            throw error;
        }
        if (!currentOwner(mine) || !snapshotCurrent(snapshot)) {
            await response.body?.cancel();
            throw new Error('画面状態が変わりました。状況を再取得してください。');
        }
        const value = await boundedJson(response, JSON_LIMIT);
        if (!currentOwner(mine) || !snapshotCurrent(snapshot))
            throw new Error('画面状態が変わりました。状況を再取得してください。');
        return value;
    } catch (error) {
        if (record.postStarted) error.requestSent = true;
        throw error;
    } finally {
        clearTimeout(timer);
        activeRequests.delete(record);
    }
}

function parseTarget(value, requestedId) {
    if (!isObject(value) || value.id !== requestedId || !validTargetId(value.id) ||
        !validText(value.displayName, 200) || !validText(value.maskedRecipient, 254) ||
        typeof value.enabled !== 'boolean' || typeof value.recovering !== 'boolean')
        throw new Error('対象ユーザーの状態を確認できません。');
    if (value.maskedRecipient.includes('@') && !MASK_MARKER.test(value.maskedRecipient))
        throw new Error('保存済みメールの伏字表示を確認できません。');
    const parsed = {
        id: value.id, displayName: value.displayName, maskedRecipient: value.maskedRecipient,
        enabled: value.enabled, recovering: value.recovering, operationId: null, expiresAt: null
    };
    if (value.operationId !== undefined && value.operationId !== null) {
        if (!validOperation(value.operationId)) throw new Error('復旧処理の識別情報を確認できません。');
        parsed.operationId = value.operationId;
    }
    if (value.expiresAt !== undefined && value.expiresAt !== null) {
        if (typeof value.expiresAt !== 'string' || value.expiresAt.length > 64 || !Number.isFinite(Date.parse(value.expiresAt)))
            throw new Error('復旧期限を確認できません。');
        parsed.expiresAt = value.expiresAt;
    }
    return parsed;
}

function abortActive() {
    for (const record of activeRequests) {
        if (record.postStarted) writeOutcomeUnknown = true;
        record.controller.abort();
    }
    for (const controller of activeCredentials) controller.abort();
}

function invalidateProof({ clearTarget = false } = {}) {
    selectionGeneration++;
    abortActive();
    busy = false;
    if (clearTarget) target = null;
    setText('error', '');
    if (writeOutcomeUnknown) setText('status', '送信結果が不明です。書き込みを停止しました。状況を再取得してください。');
    render();
}

function clearActor() {
    actorGeneration++;
    selectionGeneration++;
    abortActive();
    actorId = null;
    target = null;
    clearTargetExpiryTimer();
    if (node('confirm')) node('confirm').checked = false;
    if (node('reason')) node('reason').value = '';
    ready = false;
    busy = false;
    writeOutcomeUnknown = false;
    setText('status', 'セッションまたは管理者権限を確認できません。再ログイン後に状態を再取得してください。');
    setText('error', '');
    render();
}

function parseContext(value, requestedId) {
    if (!isObject(value) || !validActor(value.actorId)) throw new Error('管理者の状態を確認できません。');
    if (requestedId === null) return { actorId: value.actorId, target: null };
    return { actorId: value.actorId, target: parseTarget(value.target, requestedId) };
}

async function refreshStatus(requestedId = null) {
    if (!currentOwner() || !ready || busy) return;
    if (requestedId !== null && !validTargetId(requestedId)) {
        setText('error', '対象ユーザーIDを確認してください。');
        return;
    }
    selectionGeneration++;
    abortActive();
    clearTargetExpiryTimer();
    target = null;
    if (node('confirm')) node('confirm').checked = false;
    const mine = ownerGeneration;
    const selectionMine = selectionGeneration;
    busy = true;
    setText('error', '');
    setText('status', '最新の状態を読み込んでいます。書き込みは行いません。');
    render();
    try {
        const value = await request('passkey-recovery-admin-context', { targetId: requestedId });
        if (!currentOwner(mine) || selectionGeneration !== selectionMine) return;
        const parsed = parseContext(value, requestedId);
        if (actorId !== parsed.actorId) {
            actorGeneration++;
            abortActive();
            target = null;
        }
        actorId = parsed.actorId;
        target = parsed.target;
        if (expiredRecoveryRecord(target) && node('confirm')) node('confirm').checked = false;
        scheduleTargetExpiry();
        ready = true;
        writeOutcomeUnknown = false;
        setText('status', requestedId === null
            ? '管理者セッションを確認しました。対象ユーザーIDを入力して確認してください。'
            : '対象の状態を確認しました。');
        setText('error', '');
    } catch (error) {
        if (!currentOwner(mine) || selectionGeneration !== selectionMine) return;
        if (error?.status === 401 || error?.status === 403) clearActor();
        else {
            setText('error', error?.message || '状態を取得できませんでした。');
            setText('status', writeOutcomeUnknown
                ? '送信結果が不明です。書き込みは停止中です。'
                : '状態を確認できません。再試行してください。');
            if (error?.retryAfterMs !== null && error?.retryAfterMs !== undefined)
                setText('error', `${error.message} Retry-Afterを確認し、少なくとも${Math.ceil(error.retryAfterMs / 1000)}秒待ってください。`);
        }
    } finally {
        if (currentOwner(mine) && selectionGeneration === selectionMine) {
            busy = false;
            render();
        }
    }
}

function readSnapshot(requireConfirmation, requireReason) {
    const targetIdValue = node('targetId')?.value ?? '';
    const reasonValue = node('reason')?.value ?? '';
    const reason = reasonValue.trim();
    if (!validTargetId(targetIdValue)) throw new Error('対象ユーザーIDを入力してください。');
    if (requireReason && (reason.length < 3 || reasonValue.length > 500 || hasControls(reasonValue)))
        throw new Error('理由は前後の空白を除いて3〜500文字で入力してください。');
    if (requireConfirmation && node('confirm')?.checked !== true)
        throw new Error('既存パスキーとセッションの無効化を確認してください。');
    if (!target || target.id !== targetIdValue) throw new Error('先に対象ユーザーの状態を確認してください。');
    return {
        ownerGeneration, actorGeneration, selectionGeneration, actorId,
        targetId: targetIdValue, targetSnapshot: target, reason, requireReason, requireConfirmation
    };
}

function parseOptions(json, creating) {
    if (!isObject(json) || typeof json.challenge !== 'string') throw new Error('パスキーの認証オプションを確認できません。');
    const parser = creating ? window.PublicKeyCredential?.parseCreationOptionsFromJSON : window.PublicKeyCredential?.parseRequestOptionsFromJSON;
    let options;
    try {
        if (typeof parser === 'function') options = parser.call(window.PublicKeyCredential, json);
        else {
            const decode = value => {
                if (typeof value !== 'string' || !/^[A-Za-z0-9_-]+$/.test(value)) throw new Error('invalid');
                const base64 = value.replace(/-/g, '+').replace(/_/g, '/') + '='.repeat((4 - value.length % 4) % 4);
                const binary = atob(base64);
                return Uint8Array.from(binary, char => char.charCodeAt(0));
            };
            options = { ...json, challenge: decode(json.challenge) };
            if (creating) {
                if (!isObject(json.user) || typeof json.user.id !== 'string') throw new Error('invalid');
                options.user = { ...json.user, id: decode(json.user.id) };
                if (json.excludeCredentials !== undefined) {
                    if (!Array.isArray(json.excludeCredentials) || json.excludeCredentials.length > 100) throw new Error('invalid');
                    options.excludeCredentials = json.excludeCredentials.map(item => {
                        if (!isObject(item) || typeof item.id !== 'string') throw new Error('invalid');
                        return { ...item, id: decode(item.id) };
                    });
                }
            } else if (json.allowCredentials !== undefined) {
                if (!Array.isArray(json.allowCredentials) || json.allowCredentials.length > 100) throw new Error('invalid');
                options.allowCredentials = json.allowCredentials.map(item => {
                    if (!isObject(item) || typeof item.id !== 'string') throw new Error('invalid');
                    return { ...item, id: decode(item.id) };
                });
            }
        }
    } catch {
        throw new Error('パスキーの認証オプションを確認できません。');
    }
    if (!isObject(options) || !(options.challenge instanceof ArrayBuffer || ArrayBuffer.isView(options.challenge)))
        throw new Error('パスキーの認証オプションを確認できません。');
    const timeout = Number.isFinite(options.timeout) && options.timeout > 0
        ? Math.min(options.timeout, CREDENTIAL_TIMEOUT_MS) : CREDENTIAL_TIMEOUT_MS;
    return { ...options, timeout };
}

function encodeBytes(value) {
    if (value == null) return null;
    const bytes = new Uint8Array(value);
    let binary = '';
    for (let offset = 0; offset < bytes.length; offset += 0x8000)
        binary += String.fromCharCode(...bytes.subarray(offset, offset + 0x8000));
    return btoa(binary).replace(/\+/g, '-').replace(/\//g, '_').replace(/=+$/g, '');
}

function serializeCredential(credential) {
    if (typeof credential?.toJSON === 'function') {
        const value = credential.toJSON();
        if (isObject(value)) return value;
        throw new Error('パスキーの結果を安全に処理できません。');
    }
    const response = credential?.response;
    if (!response || !credential.rawId || !response.clientDataJSON)
        throw new Error('パスキーの確認結果を処理できません。');
    const base = {
        id: credential.id, rawId: encodeBytes(credential.rawId), type: credential.type,
        authenticatorAttachment: credential.authenticatorAttachment,
        clientExtensionResults: credential.getClientExtensionResults?.() || {}
    };
    if (response.authenticatorData && response.signature) {
        return {
            ...base,
            response: {
                clientDataJSON: encodeBytes(response.clientDataJSON),
                authenticatorData: encodeBytes(response.authenticatorData),
                signature: encodeBytes(response.signature),
                userHandle: encodeBytes(response.userHandle),
            }
        };
    }
    if (response.attestationObject) {
        return {
            ...base,
            response: {
                clientDataJSON: encodeBytes(response.clientDataJSON),
                attestationObject: encodeBytes(response.attestationObject),
                transports: response.getTransports?.() || []
            }
        };
    }
    throw new Error('パスキーの確認結果を処理できません。');
}

async function getCredential(publicKey, snapshot) {
    if (!('PublicKeyCredential' in window) || !navigator.credentials?.get)
        throw new Error('このブラウザーではパスキー認証を利用できません。');
    const options = parseOptions(publicKey, false);
    const controller = new AbortController();
    const timer = setTimeout(() => controller.abort(), Math.min(options.timeout, CREDENTIAL_TIMEOUT_MS));
    activeCredentials.add(controller);
    try {
        const credential = await navigator.credentials.get({ publicKey: options, signal: controller.signal });
        if (!snapshotCurrent(snapshot)) throw new Error('入力が変わったため、パスキー操作を無効にしました。');
        if (!credential) throw new Error('パスキー認証が完了しませんでした。');
        return JSON.stringify(serializeCredential(credential));
    } catch (error) {
        if (!snapshotCurrent(snapshot)) throw new Error('入力が変わったため、パスキー操作を無効にしました。');
        if (error?.name === 'NotAllowedError' || error?.name === 'AbortError')
            throw new Error('パスキー認証が完了しませんでした。必要なら状態を再取得してください。');
        throw error instanceof Error ? error : new Error('パスキー認証を完了できませんでした。');
    } finally {
        clearTimeout(timer);
        activeCredentials.delete(controller);
    }
}

async function runAdminFlow(kind, event) {
    event?.preventDefault();
    if (!currentOwner() || busy || writeOutcomeUnknown) return;
    const requireConfirmation = kind === 'start';
    const requireReason = kind === 'start';
    let snapshot;
    try { snapshot = readSnapshot(requireConfirmation, requireReason); snapshot.kind = kind; }
    catch (error) { setText('error', error.message); return; }
    const operationId = target.operationId;
    if (kind === 'start' && !canStartRecovery(target)) {
        setText('error', '開始できる対象ではありません。状態を再取得してください。');
        return;
    }
    if (kind === 'resend' && !canResendRecovery(target)) {
        setText('error', '再送できる復旧処理がありません。状況を再取得してください。');
        return;
    }
    busy = true;
    setText('error', '');
    setText('status', kind === 'start' ? '本人確認の準備をしています。' : '再送の本人確認を準備しています。');
    render();
    try {
        const optionsPath = kind === 'start' ? 'passkey-recovery-admin-options' : 'passkey-recovery-resend-options';
        const optionsBody = kind === 'start'
            ? { targetUserId: snapshot.targetId, reason: snapshot.reason }
            : { operationId };
        const optionsResponse = await request(optionsPath, { body: optionsBody, snapshot });
        if (!snapshotCurrent(snapshot)) return;
        if (!snapshotActionEligible(snapshot)) {
            setText('status', '復旧対象の状態または期限が変わりました。');
            setText('error', '状態を再取得してから操作をやり直してください。');
            return;
        }
        if (!isObject(optionsResponse) || !isObject(optionsResponse.publicKey)) {
            writeOutcomeUnknown = true;
            throw new Error('認証オプションの応答を確認できません。書き込みを停止しました。');
        }
        setText('status', 'ブラウザーのパスキー確認を完了してください。');
        const credential = await getCredential(optionsResponse.publicKey, snapshot);
        if (!snapshotCurrent(snapshot)) return;
        if (!snapshotActionEligible(snapshot)) {
            setText('status', '復旧対象の状態または期限が変わりました。');
            setText('error', '状態を再取得してから操作をやり直してください。');
            return;
        }
        const resultPath = kind === 'start' ? 'passkey-recovery-start' : 'passkey-recovery-resend';
        const resultBody = kind === 'start'
            ? { targetUserId: snapshot.targetId, reason: snapshot.reason, credential, explicitlyConfirmed: true }
            : { operationId, credential };
        const result = await request(resultPath, { body: resultBody, snapshot });
        if (!snapshotCurrent(snapshot)) {
            writeOutcomeUnknown = true;
            throw new Error('入力が変わりました。送信結果を確認できないため、状態を再取得してください。');
        }
        if (!isObject(result) || !validOperation(result.operationId) ||
            typeof result.expiresAt !== 'string' || result.expiresAt.length > 64 || !Number.isFinite(Date.parse(result.expiresAt)) ||
            !validText(result.maskedRecipient, 254) || (result.maskedRecipient.includes('@') && !MASK_MARKER.test(result.maskedRecipient))) {
            writeOutcomeUnknown = true;
            throw new Error('送信結果の形式を確認できません。書き込みを停止しました。');
        }
        if (node('confirm')) node('confirm').checked = false;
        if (node('reason')) node('reason').value = '';
        target = { ...target, enabled: false, recovering: true, operationId: result.operationId, expiresAt: result.expiresAt, maskedRecipient: result.maskedRecipient };
        scheduleTargetExpiry();
        writeOutcomeUnknown = false;
        setText('status', '本人確認に成功し、復旧案内を送信キューに登録しました。');
        setText('error', '');
    } catch (error) {
        if (!currentOwner(snapshot.ownerGeneration)) return;
        if (error?.status === 401 || error?.status === 403) {
            clearActor();
            return;
        }
        if (error?.requestSent) writeOutcomeUnknown = true;
        setText('status', writeOutcomeUnknown
            ? '送信結果が不明です。再送せず、状況を再取得してください。'
            : '処理を完了できませんでした。');
        let message = error?.message || '処理を完了できませんでした。';
        if (error?.retryAfterMs !== null && error?.retryAfterMs !== undefined)
            message += ` Retry-Afterを確認し、少なくとも${Math.ceil(error.retryAfterMs / 1000)}秒待ってから状況を再取得してください。`;
        setText('error', message);
    } finally {
        if (currentOwner(snapshot.ownerGeneration) && selectionGeneration === snapshot.selectionGeneration) {
            busy = false;
            render();
        }
    }
}

function onTargetChanged() {
    if (node('confirm')) node('confirm').checked = false;
    if (node('reason')) node('reason').value = '';
    invalidateProof({ clearTarget: true });
    setText('status', '対象IDが変更されました。対象の状態を再確認してください。');
}
function onReasonChanged() { invalidateProof(); }
function onConfirmationChanged() { invalidateProof(); }

function installListeners(root) {
    const lookup = root.querySelector(`#${ids.lookupForm}`);
    const actions = root.querySelector(`#${ids.actionsForm}`);
    const targetInput = root.querySelector(`#${ids.targetId}`);
    const reasonInput = root.querySelector(`#${ids.reason}`);
    const confirmInput = root.querySelector(`#${ids.confirm}`);
    const refreshButton = root.querySelector(`#${ids.refresh}`);
    const resendButton = root.querySelector(`#${ids.resend}`);
    listeners = {
        lookup: event => { event.preventDefault(); refreshStatus(targetInput.value); },
        actions: event => runAdminFlow('start', event),
        target: onTargetChanged,
        reason: onReasonChanged,
        confirm: onConfirmationChanged,
        refresh: () => refreshStatus(targetInput.value.trim() ? targetInput.value : null),
        resend: event => runAdminFlow('resend', event)
    };
    lookup?.addEventListener('submit', listeners.lookup);
    actions?.addEventListener('submit', listeners.actions);
    targetInput?.addEventListener('input', listeners.target);
    reasonInput?.addEventListener('input', listeners.reason);
    confirmInput?.addEventListener('change', listeners.confirm);
    refreshButton?.addEventListener('click', listeners.refresh);
    resendButton?.addEventListener('click', listeners.resend);
}

function removeListeners(root) {
    if (!root || !listeners) return;
    root.querySelector(`#${ids.lookupForm}`)?.removeEventListener('submit', listeners.lookup);
    root.querySelector(`#${ids.actionsForm}`)?.removeEventListener('submit', listeners.actions);
    root.querySelector(`#${ids.targetId}`)?.removeEventListener('input', listeners.target);
    root.querySelector(`#${ids.reason}`)?.removeEventListener('input', listeners.reason);
    root.querySelector(`#${ids.confirm}`)?.removeEventListener('change', listeners.confirm);
    root.querySelector(`#${ids.refresh}`)?.removeEventListener('click', listeners.refresh);
    root.querySelector(`#${ids.resend}`)?.removeEventListener('click', listeners.resend);
    listeners = null;
}

export async function init(root) {
    if (!root || root.id !== ids.root) return;
    if (ownerRoot === root && currentOwner()) return;
    cleanup(ownerRoot);
    ownerRoot = root;
    const mine = ++ownerGeneration;
    actorGeneration++;
    selectionGeneration++;
    actorId = null;
    target = null;
    busy = true;
    ready = false;
    writeOutcomeUnknown = false;
    installListeners(root);
    setText('status', '管理者セッションを確認しています。');
    render();
    try {
        const value = await request('passkey-recovery-admin-context');
        if (!currentOwner(mine)) return;
        const parsed = parseContext(value, null);
        actorId = parsed.actorId;
        ready = true;
        // 明示的に選ばれた対象は入力候補だけにする。照会・再認証・復旧は自動実行しない。
        const selectedTarget = root.getAttribute('data-selected-target');
        if (validTargetId(selectedTarget)) root.querySelector(`#${ids.targetId}`).value = selectedTarget;
        setText('status', '管理者セッションを確認しました。対象ユーザーIDを入力して確認してください。');
        setText('error', '');
    } catch (error) {
        if (!currentOwner(mine)) return;
        if (error?.status === 401 || error?.status === 403) clearActor();
        else {
            ready = false;
            setText('status', '管理者の状態を確認できません。再ログイン後、画面を再読み込みしてください。');
            setText('error', error?.message || '状態を取得できませんでした。');
        }
    } finally {
        if (currentOwner(mine)) {
            busy = false;
            render();
        }
    }
}

export function cleanup(root = ownerRoot) {
    if (root && ownerRoot && root !== ownerRoot) return;
    ownerGeneration++;
    actorGeneration++;
    selectionGeneration++;
    removeListeners(ownerRoot);
    abortActive();
    clearTargetExpiryTimer();
    activeRequests = new Set();
    activeCredentials = new Set();
    ownerRoot = null;
    actorId = null;
    target = null;
    busy = false;
    ready = false;
    writeOutcomeUnknown = false;
}
