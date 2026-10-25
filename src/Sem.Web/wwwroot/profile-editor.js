import { createResultPresenter } from './operation-result-presenter.js';
const API = '/api/core/';
const JSON_LIMIT = 4 * 1024 * 1024;
const IMAGE_FILE_LIMIT = 10 * 1024 * 1024;
const SAVED_IMAGE_LIMIT = 1024 * 1024;
const PNG_DATA_PREFIX = 'data:image/png;base64,';
const PNG_SIGNATURE = [137, 80, 78, 71, 13, 10, 26, 10];
const ALLOWED_IMAGE_TYPES = new Set(['image/png', 'image/jpeg', 'image/webp']);
let resultPresenter;
let state = null;
let busy = false;
let qrObjectUrl = null;
let shownQrVersion=null;
let imageWriteNeedsRefresh = false;
let profileWriteNeedsRefresh=false, historyBusy=false;
let ownerGeneration=0, ownerRoot=null;
const activeRequests=new Set();
const currentOwner=mine=>mine===ownerGeneration && ownerRoot?.isConnected && ownerRoot===document.getElementById('profile-settings');
export function cleanup(){ resultPresenter?.clear();resultPresenter=null; ownerGeneration++;for(const c of activeRequests)c.abort();activeRequests.clear();state=null;busy=false;imageWriteNeedsRefresh=false;profileWriteNeedsRefresh=false;historyBusy=false;const dialog=ownerRoot?.querySelector?.('#profile-edit-dialog');if(dialog?.open)dialog.close();const qrDialog=ownerRoot?.querySelector?.('#profile-qr-dialog');if(qrDialog?.open)qrDialog.close();if(qrObjectUrl){URL.revokeObjectURL(qrObjectUrl);qrObjectUrl=null;}ownerRoot=null; }

const $ = id => document.getElementById(id);
const elem = (tag, text = null) => {
    const el = document.createElement(tag);
    if (text !== null) el.textContent = text;
    return el;
};
function setError(text = '') { const node=$('profile-settings-error');if(node)node.textContent=text; }
function setStatus(text) { const node=$('profile-settings-status');if(node)node.textContent=text; }
function savedPngDataUrl(value) {
    if (typeof value !== 'string' || !value.startsWith(PNG_DATA_PREFIX) ||
        value.length > PNG_DATA_PREFIX.length + 4 * Math.ceil(SAVED_IMAGE_LIMIT / 3)) return null;
    const encoded = value.slice(PNG_DATA_PREFIX.length);
    if (!encoded || encoded.length % 4 !== 0 ||
        !/^(?:[A-Za-z0-9+/]{4})*(?:[A-Za-z0-9+/]{2}==|[A-Za-z0-9+/]{3}=)?$/.test(encoded)) return null;
    try {
        const binary = atob(encoded);
        if (!binary.length || binary.length > SAVED_IMAGE_LIMIT || btoa(binary) !== encoded ||
            binary.length < PNG_SIGNATURE.length) return null;
        for (let index = 0; index < PNG_SIGNATURE.length; index++)
            if (binary.charCodeAt(index) !== PNG_SIGNATURE[index]) return null;
        return value;
    } catch { return null; }
}
function imageRowVersion() {
    const version = state?.profile?.imageRowVersion;
    if (version === undefined || version === null) return 0;
    if (!Number.isSafeInteger(version) || version < 0) throw new Error('画像の更新番号を確認できません。プロフィールを再取得してください。');
    return version;
}
function renderSavedImage() {
    const image = $('profile-image-preview');
    const fallback = $('profile-image-fallback');
    if (!image || !fallback || !state) return;
    const dataUrl = savedPngDataUrl(state.profile.imageDataUrl);
    image.removeAttribute('src');
    image.hidden = !dataUrl;
    fallback.hidden = Boolean(dataUrl);
    fallback.textContent = state.profile.icon || '🙂';
    if (dataUrl) image.src = dataUrl;
}
function setImageControls() {
    const disabled = !state || busy || imageWriteNeedsRefresh || profileWriteNeedsRefresh;
    for (const id of ['profile-image-file', 'profile-image-upload', 'profile-image-delete'])
        if ($(id)) $(id).disabled = disabled;
    for(const id of ['profile-edit-open','profile-edit-cancel'])if($(id))$(id).disabled=disabled;
    const dialogRefresh=$('profile-dialog-refresh');if(dialogRefresh){dialogRefresh.hidden=!imageWriteNeedsRefresh&&!profileWriteNeedsRefresh;dialogRefresh.disabled=busy;}
    const hasSavedImage = Boolean(state?.profile?.imageDataUrl);
    if ($('profile-image-delete')) $('profile-image-delete').disabled = disabled || !hasSavedImage;
    if ($('profile-save')) $('profile-save').disabled = disabled;
    if ($('profile-social-add')) $('profile-social-add').disabled = disabled;
    if ($('profile-qr-issue')) $('profile-qr-issue').disabled = disabled;
    const questionForm = $('profile-question-add');
    if (questionForm) {
        const submit = questionForm.querySelector('button[type="submit"]') || questionForm.querySelector('button');
        if (submit) submit.disabled = disabled;
    }
    const refresh = $('profile-image-refresh');
    if (refresh) {
        refresh.hidden = !imageWriteNeedsRefresh && !profileWriteNeedsRefresh && Boolean(state);
        refresh.disabled = busy;
    }
}
function clearSelectedImage() {
    const input = $('profile-image-file');
    const detail = $('profile-image-selection');
    if (input) input.value = '';
    if (detail) detail.textContent = '画像は選択されていません。';
}
function endpoint(name, params = {}) {
    const url = new URL(API + name, location.origin);
    for (const [key, value] of Object.entries(params)) url.searchParams.set(key, String(value));
    return url;
}
async function boundedJson(response, limit) {
    if (!(response.headers.get('Content-Type') || '').toLowerCase().startsWith('application/json')) throw new Error('JSON応答を確認できません。');
    const length=response.headers.get('Content-Length');
    if(length!==null && (!/^\d+$/.test(length)||Number(length)>limit)) throw new Error('応答サイズが大きすぎます。');
    if(!response.body) throw new Error('応答本文を確認できません。');
    const reader=response.body.getReader(), chunks=[];let size=0;
    try { for(;;){const {done,value}=await reader.read();if(done)break;size+=value.byteLength;if(size>limit)throw new Error('応答サイズが大きすぎます。');chunks.push(value);} }
    catch(error){await reader.cancel().catch(()=>{});throw error;}
    const data=new Uint8Array(size);let offset=0;for(const chunk of chunks){data.set(chunk,offset);offset+=chunk.byteLength;}
    return JSON.parse(new TextDecoder('utf-8',{fatal:true}).decode(data));
}
function clearIdentity() {
    state=null;
    const dialog=$('profile-edit-dialog');if(dialog?.open)dialog.close();
    for(const id of ['profile-card-name','profile-card-reading','profile-card-call','profile-card-social','profile-card-answers','profile-card-avatar','profile-history'])$(id)?.replaceChildren();
    const qrDialog=$('profile-qr-dialog');if(qrDialog?.open)qrDialog.close();$('profile-qr-large-image')?.replaceChildren();if($('profile-qr-enlarge'))$('profile-qr-enlarge').hidden=true;
    const root=$('profile-settings');if(root)root.hidden=true;
    const image=$('profile-qr-image');if(image)image.replaceChildren();
    const preview=$('profile-image-preview');if(preview){preview.removeAttribute('src');preview.hidden=true;}
    const fallback=$('profile-image-fallback');if(fallback){fallback.textContent='🙂';fallback.hidden=false;}
    clearSelectedImage();
    if(qrObjectUrl){URL.revokeObjectURL(qrObjectUrl);qrObjectUrl=null;}
    const refresh=$('profile-image-refresh');if(refresh)refresh.hidden=false;
    setImageControls();
}
async function csrf(signal) {
    const response=await fetch('/api/csrf',{method:'GET',credentials:'same-origin',mode:'same-origin',cache:'no-store',redirect:'error',headers:{Accept:'application/json'},signal});
    if(!response.ok)throw new Error('CSRF確認に失敗しました。再読込してください。');
    const value=await boundedJson(response,64*1024);if(typeof value.token!=='string'||!value.token||value.token.length>8192)throw new Error('CSRFトークンを確認できません。');return value.token;
}
async function post(url,body,headers={}) {
    let submitted=false, rejected=false;
    const mine=ownerGeneration,controller=new AbortController(),timer=setTimeout(()=>controller.abort(),25000);activeRequests.add(controller);
    try {
        const token=await csrf(controller.signal);
        submitted=true;
        const response=await fetch(url,{method:'POST',credentials:'same-origin',mode:'same-origin',cache:'no-store',redirect:'error',headers:{Accept:'application/json','Content-Type':'application/json','X-CSRF-TOKEN':token,...headers},body:JSON.stringify(body),signal:controller.signal});
        if(!response.ok||response.redirected){rejected=!response.redirected&&response.status>=400&&response.status<500&&response.status!==408;if(currentOwner(mine)&&(response.status===401||response.status===403))clearIdentity();await response.body?.cancel();throw new Error(`処理に失敗しました（${response.status}）。再読込して状態を確認してください。`);}
        const value=await boundedJson(response,JSON_LIMIT);return {json:async()=>value};
    } catch(error) { if(error && typeof error==='object') error.resultKind=submitted&&!rejected?'unknown':'error';throw error;
    } finally {clearTimeout(timer);activeRequests.delete(controller);}
}
async function readJson(url) {
    const mine=ownerGeneration,controller=new AbortController(),timer=setTimeout(()=>controller.abort(),25000);activeRequests.add(controller);
    try {const response=await fetch(url,{method:'GET',credentials:'same-origin',mode:'same-origin',cache:'no-store',redirect:'error',headers:{Accept:'application/json'},signal:controller.signal});
        if(!response.ok||response.redirected){if(currentOwner(mine)&&(response.status===401||response.status===403))clearIdentity();await response.body?.cancel();throw new Error('ログイン状態または閲覧権限を確認してください。');}return await boundedJson(response,JSON_LIMIT);
    } finally {clearTimeout(timer);activeRequests.delete(controller);}
}
function input(labelText, value, maxLength, type = 'text') {
    const label = elem('label', labelText);
    const control = elem('input');
    control.type = type;
    control.maxLength = maxLength;
    control.value = value || '';
    label.append(control);
    return { label, control };
}
function renderSocial(socials=state.profile.social || []) {
    const root = $('profile-social-links');
    root.replaceChildren();
    for (const social of socials) {
        const row = elem('div');
        const service = input('サービス名', social.service, 60);
        const url = input('URL', social.url, 500, 'url');
        const remove = elem('button', '削除');
        remove.type = 'button';
        remove.addEventListener('click', () => { row.remove(); });
        row.append(service.label, url.label, remove);
        row.dataset.social = 'true';
        root.append(row);
    }
}
function renderAnswers() {
    const root = $('profile-answer-fields');
    root.replaceChildren();
    const answers = new Map((state.profile.answers || []).map(x => [x.questionId, x]));
    for (const question of state.questions.filter(x => x.enabled)) {
        const answer = answers.get(question.id);
        const label = elem('label', question.text);
        let control;
        if (question.type === 'YesNo') {
            control = elem('select');
            for (const [value, text] of [['', '未回答'], ['YES', 'はい'], ['NO', 'いいえ']]) {
                const option = elem('option', text); option.value = value; option.selected = (answer?.value || '') === value;
                control.append(option);
            }
        } else {
            control = elem('textarea');
            control.maxLength = 1000;
            control.value = answer?.value || '';
        }
        control.dataset.questionId = question.id;
        label.append(control);
        root.append(label);
    }
}
function questionAction(operation, question, extra = {}) {
    return post(endpoint('profile-questions', { expectedActor: state.actorId }), {
        operation, id: question?.id || null, text: extra.text ?? question?.text ?? null,
        type: question?.type ?? extra.type ?? null, rowVersion: question?.rowVersion ?? 0, order: extra.order ?? null
    });
}
function renderQuestionAdmin() {
    const section = $('profile-question-admin');
    section.hidden = !state.canManageQuestions;
    if (!state.canManageQuestions) return;
    const root = $('profile-question-list');
    root.replaceChildren();
    state.questions.forEach((question, index) => {
        const item = elem('li');
        const label = elem('label', `${question.enabled ? '有効' : '停止中'}・${question.type === 'Text' ? '自由記述' : 'はい／いいえ'}${question.hasAnswers ? '・回答あり' : ''}`);
        const text = elem('input'); text.type = 'text'; text.maxLength = 200; text.value = question.text;
        label.append(text);
        const save = elem('button', '文面を保存'); save.type = 'button';
        save.addEventListener('click', () => mutate(async () => questionAction('Edit', question, { text: text.value })));
        const stop = elem('button', '停止'); stop.type = 'button'; stop.disabled = !question.enabled;
        stop.addEventListener('click', () => mutate(async () => questionAction('Stop', question)));
        const remove = elem('button', '削除'); remove.type = 'button'; remove.disabled = question.hasAnswers;
        remove.title = question.hasAnswers ? '回答がある設問は削除できません。停止して回答を保持してください。' : '';
        remove.addEventListener('click', () => {
            if (confirm('この設問を削除しますか？')) void mutate(async () => questionAction('Delete', question));
        });
        const up = elem('button', '上へ'); up.type = 'button'; up.disabled = index === 0;
        up.addEventListener('click', () => reorder(index, index - 1));
        const down = elem('button', '下へ'); down.type = 'button'; down.disabled = index === state.questions.length - 1;
        down.addEventListener('click', () => reorder(index, index + 1));
        item.append(label, save, stop, remove, up, down);
        root.append(item);
    });
}
async function reorder(from, to) {
    if (busy || to < 0 || to >= state.questions.length) return;
    const order = [...state.questions];
    const [moved] = order.splice(from, 1);
    order.splice(to, 0, moved);
    await mutate(() => questionAction('Reorder', null, {
        order: order.map(q => ({ id: q.id, rowVersion: q.rowVersion }))
    }));
}
function collectProfile() {
    const social = [...$('profile-social-links').querySelectorAll('[data-social]')].map(row => ({
        service: row.querySelector('input').value,
        url: row.querySelectorAll('input')[1].value
    }));
    const answers = [...$('profile-answer-fields').querySelectorAll('[data-question-id]')].map(control => ({
        questionId: control.dataset.questionId,
        value: control.value
    }));
    return {
        displayName: $('profile-display-name').value,
        reading: $('profile-reading').value,
        callName: $('profile-call-name').value,
        icon: $('profile-icon').value,
        social,
        rowVersion: state.profile.rowVersion,
        answers
    };
}
async function loadQrStatus(mine=ownerGeneration) {
    const qr=await readJson(endpoint('profile-qr'));if(!currentOwner(mine)||!state)return;if(qrObjectUrl&&(!qr.active||shownQrVersion!==qr.rowVersion))clearQrView();state.qr=qr;
    const text = state.qr.active
        ? `発行済み・有効期限 ${state.qr.expiresAt}。再発行すると以前のQRは直ちに無効になります。`
        : state.qr.exists
            ? `期限切れ（${state.qr.expiresAt}）。`
            : 'QRはまだ発行されていません。';
    $('profile-qr-status').textContent = text;
    $('profile-qr-issue').textContent = state.qr.exists ? 'QRを再発行' : 'QRを発行';
}
function populateEditor(){
    $('profile-display-name').value = state.profile.displayName;
    $('profile-reading').value = state.profile.reading;
    $('profile-call-name').value = state.profile.callName;
    $('profile-icon').value = state.profile.icon || '';
    renderSavedImage();
    renderSocial(); renderAnswers(); renderQuestionAdmin();
}
async function load(mine=ownerGeneration) {
    setError('');
    const loaded=await readJson(endpoint('profile-editor'));if(!currentOwner(mine))return;state=loaded;
    populateEditor(); renderProfileCard();
    $('profile-settings').hidden = false;
    const sectionId = new Map([
        ['#profile-edit-heading', 'profile-edit-heading'],
        ['#account-passkeys', 'account-passkeys'],
        ['#profile-email-change', 'profile-email-change'],
        ['#profile-qr-heading', 'profile-qr-heading']
    ]).get(globalThis.location?.hash);
    if (sectionId) requestAnimationFrame(() => document.getElementById(sectionId)?.scrollIntoView({block:'start'}));

    imageWriteNeedsRefresh = false;profileWriteNeedsRefresh=false;
    clearSelectedImage();
    setImageControls();
    const refresh = $('profile-image-refresh'); if (refresh) refresh.hidden = true;
    await loadQrStatus(mine);if(!currentOwner(mine))return;
    setStatus('現在のプロフィールを読み込みました。');
    await loadHistory(mine);
}
async function mutate(action, profileWrite=false) {
    if (busy || imageWriteNeedsRefresh || profileWriteNeedsRefresh) return;
    const mine=ownerGeneration;busy = true; setImageControls();setError(''); setStatus('保存しています…');
    let saved=false;
    try { await action();saved=true;if(!currentOwner(mine))return;await load(mine);if(currentOwner(mine)){setStatus('保存しました。');if(!profileWrite)resultPresenter?.show('success','プロフィール設定を保存しました。');return true;} }
    catch (error) { if(currentOwner(mine)){if(profileWrite&&(saved||error.resultKind==='unknown'))profileWriteNeedsRefresh=true;setError(error.message || '保存できませんでした。再読込して状態を確認してください。');setStatus('');resultPresenter?.show(saved?'partial':error.resultKind||'error',saved?'保存処理は完了しましたが、最新表示を取得できませんでした。再保存せずプロフィールを再取得してください。':error.message||'処理を開始できませんでした。');} }
    finally { if(currentOwner(mine)){busy = false;setImageControls();} }
}
async function saveProfile(event) {
    event.preventDefault();
    if (!state || busy || imageWriteNeedsRefresh || profileWriteNeedsRefresh) return;
    if(await mutate(() => post(endpoint('profile-editor', { expectedActor: state.actorId }), collectProfile()),true)){closeEditor(false);resultPresenter?.show('success','プロフィール設定を保存しました。');}
}
async function addQuestion(event) {
    event.preventDefault();
    if (!state || busy || imageWriteNeedsRefresh || profileWriteNeedsRefresh) return;
    const text = $('profile-question-new-text').value;
    const type = $('profile-question-new-type').value;
    await mutate(async () => {
        await questionAction('Add', null, { text, type });
        const node=$('profile-question-new-text');if(node)node.value='';
    });
}
async function boundedSvg(response) {
    const limit=256*1024, length=response.headers.get('Content-Length');
    if(length!==null&&(!/^\d+$/.test(length)||Number(length)>limit))throw new Error('QR画像が大きすぎます。');
    if(!response.body)throw new Error('QR画像本文を確認できません。');
    const reader=response.body.getReader(),chunks=[];let size=0;
    try {for(;;){const {done,value}=await reader.read();if(done)break;size+=value.byteLength;if(size>limit)throw new Error('QR画像が大きすぎます。');chunks.push(value);}}
    catch(error){await reader.cancel().catch(()=>{});throw error;}
    return new Blob(chunks,{type:'image/svg+xml'});
}
async function issueQr() {
    if (!state || busy || imageWriteNeedsRefresh || profileWriteNeedsRefresh) return;
    const reissue = state.qr.exists;
    if (reissue && !confirm('以前のQRを無効にして、新しい24時間QRを発行しますか？')) return;
    const mine=ownerGeneration;let svgController=null,svgTimer=null;busy = true;setImageControls(); setError(''); setStatus('QRを発行しています…');
    try {
        const response = await post(endpoint('profile-qr', { expectedActor: state.actorId }), {
            rowVersion: state.qr.rowVersion, reissue
        });
        const issued = await response.json();if(!currentOwner(mine))return;
        if (typeof issued.token !== 'string' || !/^[0-9a-f]{64}$/i.test(issued.token) ||
            !Number.isSafeInteger(issued.rowVersion) || typeof issued.expiresAt !== 'string')
            throw new Error('QR発行応答を確認できません。');
        const url = endpoint('profile-qr-svg', { expectedActor: state.actorId, version: issued.rowVersion });
        svgController=new AbortController();activeRequests.add(svgController);svgTimer=setTimeout(()=>svgController.abort(),25000);
        const svgResponse = await fetch(url, {
            method: 'GET', credentials: 'same-origin', mode: 'same-origin', cache: 'no-store',
            redirect: 'error', signal:svgController.signal, headers: { Accept: 'image/svg+xml', 'X-Profile-QR-Token': issued.token }
        });
        if (!svgResponse.ok || svgResponse.redirected ||
            !(svgResponse.headers.get('Content-Type') || '').toLowerCase().startsWith('image/svg+xml'))
            throw new Error('QR画像を取得できません。発行状態を再読込してください。');
        const svg = await boundedSvg(svgResponse);if(!currentOwner(mine))return;
        if (qrObjectUrl) URL.revokeObjectURL(qrObjectUrl);
        qrObjectUrl = URL.createObjectURL(svg);shownQrVersion=issued.rowVersion;
        const img = elem('img'); img.src = qrObjectUrl; img.alt = 'プロフィールの24時間QRコード';
        const root = $('profile-qr-image'); root.replaceChildren(img);if($('profile-qr-enlarge'))$('profile-qr-enlarge').hidden=false;
        state.qr = { exists: true, active: true, expiresAt: issued.expiresAt, rowVersion: issued.rowVersion };
        $('profile-qr-status').textContent = `発行済み・有効期限 ${issued.expiresAt}。再発行すると以前のQRは直ちに無効になります。`;
        $('profile-qr-issue').textContent = 'QRを再発行';
        setStatus('QRを発行しました。画像はこの画面のメモリにのみ保持されます。');
    } catch (error) {
        if(!currentOwner(mine))return;
        setError(error.message || 'QRを発行できませんでした。'); setStatus('');
        try { await loadQrStatus(); } catch { /* keep the current state and never retry issuance */ }
    } finally { clearTimeout(svgTimer);activeRequests.delete(svgController);if(currentOwner(mine)){busy = false;setImageControls();} }
}
function selectedImage() {
    const file = $('profile-image-file')?.files?.[0] || null;
    const detail = $('profile-image-selection');
    if (!file) {
        if (detail) detail.textContent = '画像は選択されていません。';
        return null;
    }
    if (!ALLOWED_IMAGE_TYPES.has(file.type)) {
        if (detail) detail.textContent = 'PNG・JPEG・WebP形式の静止画像を選択してください。SVG・GIF・アニメーション画像は使用できません。';
        return null;
    }
    if (!Number.isSafeInteger(file.size) || file.size <= 0 || file.size > IMAGE_FILE_LIMIT) {
        if (detail) detail.textContent = '画像ファイルは10 MiB以下である必要があります。';
        return null;
    }
    if (detail) detail.textContent = `${file.name || '選択した画像'}（${file.size.toLocaleString('ja-JP')} bytes）`;
    return file;
}
async function changeImage(operation) {
    if (!state || busy || imageWriteNeedsRefresh || profileWriteNeedsRefresh) return;
    let file = null;
    if (operation === 'upload') {
        file = selectedImage();
        if (!file) { setError('PNG・JPEG・WebP形式の10 MiB以下の画像を選択してください。'); return; }
    } else if (!state.profile.imageDataUrl) {
        setError('削除するプロフィール画像がありません。'); return;
    } else if (!confirm('保存済みのプロフィール画像を削除しますか？')) return;

    let rowVersion;
    try { rowVersion = imageRowVersion(); }
    catch (error) { setError(error.message); return; }
    const mine=ownerGeneration, actor=state.actorId;
    const draft=$('profile-edit-dialog')?.open?collectProfile():null;
    const controller=new AbortController(),timer=setTimeout(()=>controller.abort(),25000);
    activeRequests.add(controller);
    busy=true;setImageControls();setError('');setStatus(operation==='upload'?'画像をアップロードしています…':'画像を削除しています…');
    try {
        const token=await csrf(controller.signal);
        if (!currentOwner(mine) || !state) return;
        const url=endpoint(operation==='upload'?'profile-image':'profile-image-delete',{
            expectedActor:actor,...(operation==='upload'?{rowVersion}:{})
        });
        const headers={Accept:'application/json','X-CSRF-TOKEN':token};
        let body;
        if (operation==='upload') {
            headers['Content-Type']=file.type;
            body=file;
        } else {
            headers['Content-Type']='application/json';
            body=JSON.stringify({rowVersion});
        }
        const response=await fetch(url,{method:'POST',credentials:'same-origin',mode:'same-origin',cache:'no-store',redirect:'error',headers,body,signal:controller.signal});
        if (!currentOwner(mine)) return;
        if (!response.ok || response.redirected) {
            if (response.status===401 || response.status===403) clearIdentity();
            await response.body?.cancel();
            throw new Error(`画像の更新結果を確認できません（${response.status}）。`);
        }
        const result=await boundedJson(response,JSON_LIMIT);
        if (!Number.isSafeInteger(result?.rowVersion) || result.rowVersion<0 ||
            !Number.isSafeInteger(result?.width) || result.width<0 || result.width>512 ||
            !Number.isSafeInteger(result?.height) || result.height<0 || result.height>512 ||
            (operation==='upload' && (!result.width || !result.height)))
            throw new Error('画像更新の応答が不正です。');
        if (!currentOwner(mine)) return;
        imageWriteNeedsRefresh=false;
        clearSelectedImage();
        setStatus(operation==='upload'
            ? `画像を保存しました（正規化後 ${result.width} × ${result.height}px）。プロフィールを再読み込みしています。`
            : '画像を削除しました。プロフィールを再読み込みしています。');
        await load(mine);
        if(currentOwner(mine)&&draft)restoreDraft(draft);
        if (currentOwner(mine)) setStatus(operation==='upload'
            ? `画像を保存しました（正規化後 ${result.width} × ${result.height}px）。`
            : '画像を削除しました。');
    } catch (error) {
        if (currentOwner(mine)) {
            // A failed/aborted write may have committed. Never replay it automatically.
            imageWriteNeedsRefresh=true;
            clearSelectedImage();
            setImageControls();
            const refresh=$('profile-image-refresh');if(refresh)refresh.hidden=false;
            setError(error.message || '画像の更新結果を確認できません。');
            setStatus('結果が不明です。再アップロードせず、「プロフィールを再取得」を押して状態を確認してください。');
        }
    } finally {
        clearTimeout(timer);activeRequests.delete(controller);
        if(currentOwner(mine)){busy=false;setImageControls();}
    }
}
async function manualRefresh() {
    if (busy) return;
    const mine=ownerGeneration;busy=true;setImageControls();setError('');setStatus('プロフィールを再取得しています…');
    try {
        await load(mine);
        if (currentOwner(mine)) setStatus('プロフィールを再取得しました。画像を必要に応じて確認してください。');
    } catch (error) {
        if (currentOwner(mine)) { setError(error.message || 'プロフィールを再取得できません。');setStatus(''); }
    } finally { if(currentOwner(mine)){busy=false;setImageControls();} }
}
function bind() {
    $('profile-editor-form').addEventListener('submit', saveProfile);
    $('profile-social-add').addEventListener('click', () => {
        if(!state||busy||imageWriteNeedsRefresh||profileWriteNeedsRefresh)return;
        renderSocial([...collectProfile().social,{service:'',url:''}]);
    });
    bindProfileUi();
    $('profile-qr-issue').addEventListener('click', issueQr);
    $('profile-question-add').addEventListener('submit', addQuestion);
    $('profile-image-file').addEventListener('change', () => {
        selectedImage();setError('');setImageControls();
    });
    $('profile-image-upload').addEventListener('click', () => changeImage('upload'));
    $('profile-image-delete').addEventListener('click', () => changeImage('delete'));
    $('profile-image-refresh').addEventListener('click', () => manualRefresh());
}

function clearQrView(){const dialog=$('profile-qr-dialog');if(dialog?.open)dialog.close();$('profile-qr-large-image')?.replaceChildren();$('profile-qr-image')?.replaceChildren();if($('profile-qr-enlarge'))$('profile-qr-enlarge').hidden=true;if(qrObjectUrl)URL.revokeObjectURL(qrObjectUrl);qrObjectUrl=null;shownQrVersion=null;}
function safeLink(value){try{const url=new URL(value);return ['http:','https:'].includes(url.protocol)&&!url.username&&!url.password?url.href:null;}catch{return null;}}
function renderProfileCard(){
    const name=$('profile-card-name');if(!name||!state)return;
    name.textContent=state.profile.displayName;
    $('profile-card-reading').textContent=state.profile.reading||'読み方未設定';
    $('profile-card-call').textContent=state.profile.callName||state.profile.displayName;
    const avatar=$('profile-card-avatar'),data=savedPngDataUrl(state.profile.imageDataUrl);avatar.replaceChildren();
    if(data){const img=elem('img');img.src=data;img.alt='';avatar.append(img);}else avatar.textContent=state.profile.icon||'🙂';
    const social=$('profile-card-social');social.replaceChildren();
    for(const item of state.profile.social||[]){const href=safeLink(item.url);if(!href)continue;const a=elem('a',item.service||'SNS');a.href=href;a.target='_blank';a.rel='noopener noreferrer';social.append(a);}
    if(!social.childNodes.length)social.append(elem('p','SNSは未登録です。編集から追加できます。'));
    const answers=$('profile-card-answers');answers.replaceChildren();
    for(const answer of state.profile.answers||[]){if(!answer.enabled)continue;const row=elem('section');row.className='profile-answer';const value=answer.value?answer.type==='YesNo'?(answer.value==='YES'?'はい':'いいえ'):answer.value:'未回答';const text=elem('p',value);text.style.whiteSpace='pre-wrap';row.append(elem('h4',answer.text),text);answers.append(row);}
    if(!answers.childNodes.length)answers.append(elem('p','表示できる設問はありません。'));
}
function restoreDraft(draft){
    for(const[key,id]of[['displayName','profile-display-name'],['reading','profile-reading'],['callName','profile-call-name'],['icon','profile-icon']])$(id).value=draft[key]||'';
    renderSocial(draft.social);
    for(const field of $('profile-answer-fields').querySelectorAll('[data-question-id]'))field.value=draft.answers.find(a=>a.questionId===field.dataset.questionId)?.value||'';
}
function closeEditor(discard=true){
    if(busy||imageWriteNeedsRefresh||profileWriteNeedsRefresh)return;
    const dialog=$('profile-edit-dialog');if(!dialog?.open)return;
    if(discard){populateEditor();clearSelectedImage();setError('');}
    dialog.close();$('profile-edit-open')?.focus();
}
function bindProfileUi(){
    const dialog=$('profile-edit-dialog');if(!dialog)return;
    $('profile-edit-open').addEventListener('click',()=>{if(!state||busy||imageWriteNeedsRefresh||profileWriteNeedsRefresh||dialog.open)return;dialog.showModal();$('profile-display-name').focus();});
    $('profile-edit-cancel').addEventListener('click',()=>closeEditor());
    $('profile-dialog-refresh').addEventListener('click',()=>manualRefresh());
    dialog.addEventListener('cancel',e=>{e.preventDefault();closeEditor();});
    dialog.addEventListener('keydown',e=>{if(e.key!=='Tab'||!dialog.open)return;const fields=[...dialog.querySelectorAll('button,input,textarea,select,a[href],[tabindex]')].filter(x=>x.tabIndex>=0&&!x.matches(':disabled,[hidden],[inert]')&&x.getClientRects().length&&getComputedStyle(x).visibility!=='hidden');const first=fields[0],last=fields.at(-1),active=document.activeElement;if(!first){e.preventDefault();dialog.focus();}else if(e.shiftKey&&(active===first||!fields.includes(active))){e.preventDefault();last.focus();}else if(!e.shiftKey&&(active===last||!fields.includes(active))){e.preventDefault();first.focus();}});
    $('profile-history-refresh')?.addEventListener('click',()=>loadHistory());
    const qrDialog=$('profile-qr-dialog'),enlarge=$('profile-qr-enlarge'),qrClose=$('profile-qr-close');
    const closeQr=()=>{if(qrDialog?.open)qrDialog.close();$('profile-qr-large-image')?.replaceChildren();enlarge?.focus();};
    enlarge?.addEventListener('click',()=>{if(!qrObjectUrl||!state||qrDialog.open)return;if(!state.qr?.active||!Number.isFinite(Date.parse(state.qr.expiresAt))||Date.parse(state.qr.expiresAt)<=Date.now()){clearQrView();setError('QRの期限が切れています。必要な場合だけ再発行してください。');return;}const img=elem('img');img.src=qrObjectUrl;img.alt='発行済みのプロフィールQRコード';$('profile-qr-large-image').replaceChildren(img);qrDialog.showModal();qrClose.focus();});
    qrClose?.addEventListener('click',closeQr);qrDialog?.addEventListener('cancel',e=>{e.preventDefault();closeQr();});
    qrDialog?.addEventListener('keydown',e=>{if(e.key==='Tab'){e.preventDefault();qrClose.focus();}});
}
async function loadHistory(mine=ownerGeneration){
    const root=$('profile-history'),status=$('profile-history-status');if(!root||!state||historyBusy)return;
    const actor=state.actorId;historyBusy=true;root.replaceChildren();status.textContent='登録履歴を読み込んでいます。';$('profile-history-refresh').disabled=true;
    try{const value=await readJson(endpoint('profile-registration-history',{id:actor}));if(!currentOwner(mine)||!state||state.actorId!==actor)return;
        if(value.userId!==actor||!Array.isArray(value.events)||value.events.length>100)throw Error('登録履歴を確認できません。');
        for(const item of value.events){const article=elem('article');article.className='profile-event';const heading=elem('h3'),link=elem('a',item.title);link.href='/events/'+encodeURIComponent(item.eventId);heading.append(link);article.append(heading,elem('p',item.scheduledDate));if(item.spectator)article.append(elem('p','観覧のみ'));for(const part of item.parts||[]){const row=elem('div');row.className='profile-song';row.append(elem('strong',part.songTitle),elem('span',part.partName+' ('+part.category+')'));article.append(row);}root.append(article);}
        if(!value.events.length)root.append(elem('p','表示できる参加登録履歴はありません。'));status.textContent='現在の登録情報を読み込みました。';
    }catch(error){if(currentOwner(mine)){root.replaceChildren();status.textContent='登録履歴を取得できませんでした。再取得できます。';}}
    finally{if(currentOwner(mine)){historyBusy=false;$('profile-history-refresh').disabled=false;}}
}

export async function init() {
    cleanup();ownerRoot=$('profile-settings');const mine=ownerGeneration;if(!ownerRoot)return;resultPresenter=createResultPresenter(ownerRoot,()=>currentOwner(mine));
    bind();
    try { await load(mine); }
    catch (error) { if(currentOwner(mine)){setError(error.message || 'プロフィールを読み込めません。');setStatus('');} }
}