const API = '/api/core/';
let state = null;
let busy = false;
let qrObjectUrl = null;
let ownerGeneration=0, ownerRoot=null;
const activeRequests=new Set();
const currentOwner=mine=>mine===ownerGeneration && ownerRoot?.isConnected && ownerRoot===document.getElementById('profile-settings');
export function cleanup(){ ownerGeneration++;for(const c of activeRequests)c.abort();activeRequests.clear();state=null;busy=false;if(qrObjectUrl){URL.revokeObjectURL(qrObjectUrl);qrObjectUrl=null;}ownerRoot=null; }

const $ = id => document.getElementById(id);
const elem = (tag, text = null) => {
    const el = document.createElement(tag);
    if (text !== null) el.textContent = text;
    return el;
};
function setError(text = '') { const node=$('profile-settings-error');if(node)node.textContent=text; }
function setStatus(text) { const node=$('profile-settings-status');if(node)node.textContent=text; }
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
function clearIdentity() { state=null; const root=$('profile-settings');if(root)root.hidden=true;const image=$('profile-qr-image');if(image)image.replaceChildren(); if(qrObjectUrl){URL.revokeObjectURL(qrObjectUrl);qrObjectUrl=null;} }
async function csrf(signal) {
    const response=await fetch('/api/csrf',{method:'GET',credentials:'same-origin',mode:'same-origin',cache:'no-store',redirect:'error',headers:{Accept:'application/json'},signal});
    if(!response.ok)throw new Error('CSRF確認に失敗しました。再読込してください。');
    const value=await boundedJson(response,64*1024);if(typeof value.token!=='string'||!value.token||value.token.length>8192)throw new Error('CSRFトークンを確認できません。');return value.token;
}
async function post(url,body,headers={}) {
    const mine=ownerGeneration,controller=new AbortController(),timer=setTimeout(()=>controller.abort(),25000);activeRequests.add(controller);
    try {
        const token=await csrf(controller.signal);
        const response=await fetch(url,{method:'POST',credentials:'same-origin',mode:'same-origin',cache:'no-store',redirect:'error',headers:{Accept:'application/json','Content-Type':'application/json','X-CSRF-TOKEN':token,...headers},body:JSON.stringify(body),signal:controller.signal});
        if(!response.ok||response.redirected){if(currentOwner(mine)&&(response.status===401||response.status===403))clearIdentity();await response.body?.cancel();throw new Error(`処理に失敗しました（${response.status}）。再読込して状態を確認してください。`);}
        const value=await boundedJson(response,2*1024*1024);return {json:async()=>value};
    } finally {clearTimeout(timer);activeRequests.delete(controller);}
}
async function readJson(url) {
    const mine=ownerGeneration,controller=new AbortController(),timer=setTimeout(()=>controller.abort(),25000);activeRequests.add(controller);
    try {const response=await fetch(url,{method:'GET',credentials:'same-origin',mode:'same-origin',cache:'no-store',redirect:'error',headers:{Accept:'application/json'},signal:controller.signal});
        if(!response.ok||response.redirected){if(currentOwner(mine)&&(response.status===401||response.status===403))clearIdentity();await response.body?.cancel();throw new Error('ログイン状態または閲覧権限を確認してください。');}return await boundedJson(response,2*1024*1024);
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
function renderSocial() {
    const root = $('profile-social-links');
    root.replaceChildren();
    for (const social of state.profile.social || []) {
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
    const qr=await readJson(endpoint('profile-qr'));if(!currentOwner(mine)||!state)return;state.qr=qr;
    const text = state.qr.active
        ? `発行済み・有効期限 ${state.qr.expiresAt}。再発行すると以前のQRは直ちに無効になります。`
        : state.qr.exists
            ? `期限切れ（${state.qr.expiresAt}）。`
            : 'QRはまだ発行されていません。';
    $('profile-qr-status').textContent = text;
    $('profile-qr-issue').textContent = state.qr.exists ? 'QRを再発行' : 'QRを発行';
}
async function load(mine=ownerGeneration) {
    setError('');
    const loaded=await readJson(endpoint('profile-editor'));if(!currentOwner(mine))return;state=loaded;
    $('profile-display-name').value = state.profile.displayName;
    $('profile-reading').value = state.profile.reading;
    $('profile-call-name').value = state.profile.callName;
    $('profile-icon').value = state.profile.icon || '';
    renderSocial(); renderAnswers(); renderQuestionAdmin();
    $('profile-settings').hidden = false;
    await loadQrStatus(mine);if(!currentOwner(mine))return;
    setStatus('現在のプロフィールを読み込みました。');
}
async function mutate(action) {
    if (busy) return;
    const mine=ownerGeneration;busy = true; setError(''); setStatus('保存しています…');
    try { await action();if(!currentOwner(mine))return;await load(mine);if(currentOwner(mine))setStatus('保存しました。'); }
    catch (error) { if(currentOwner(mine)){setError(error.message || '保存できませんでした。再読込して状態を確認してください。');setStatus('');} }
    finally { if(currentOwner(mine))busy = false; }
}
async function saveProfile(event) {
    event.preventDefault();
    if (!state || busy) return;
    await mutate(() => post(endpoint('profile-editor', { expectedActor: state.actorId }), collectProfile()));
}
async function addQuestion(event) {
    event.preventDefault();
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
    if (!state || busy) return;
    const reissue = state.qr.exists;
    if (reissue && !confirm('以前のQRを無効にして、新しい24時間QRを発行しますか？')) return;
    const mine=ownerGeneration;let svgController=null,svgTimer=null;busy = true; setError(''); setStatus('QRを発行しています…');
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
        qrObjectUrl = URL.createObjectURL(svg);
        const img = elem('img'); img.src = qrObjectUrl; img.alt = 'プロフィールの24時間QRコード';
        const root = $('profile-qr-image'); root.replaceChildren(img);
        state.qr = { exists: true, active: true, expiresAt: issued.expiresAt, rowVersion: issued.rowVersion };
        $('profile-qr-status').textContent = `発行済み・有効期限 ${issued.expiresAt}。再発行すると以前のQRは直ちに無効になります。`;
        $('profile-qr-issue').textContent = 'QRを再発行';
        setStatus('QRを発行しました。画像はこの画面のメモリにのみ保持されます。');
    } catch (error) {
        if(!currentOwner(mine))return;
        setError(error.message || 'QRを発行できませんでした。'); setStatus('');
        try { await loadQrStatus(); } catch { /* keep the current state and never retry issuance */ }
    } finally { clearTimeout(svgTimer);activeRequests.delete(svgController);if(currentOwner(mine))busy = false; }
}
function bind() {
    $('profile-editor-form').addEventListener('submit', saveProfile);
    $('profile-social-add').addEventListener('click', () => {
        state.profile.social.push({ service: '', url: '' }); renderSocial();
    });
    $('profile-qr-issue').addEventListener('click', issueQr);
    $('profile-question-add').addEventListener('submit', addQuestion);
}
export async function init() {
    cleanup();ownerRoot=$('profile-settings');const mine=ownerGeneration;if(!ownerRoot)return;
    bind();
    try { await load(mine); }
    catch (error) { if(currentOwner(mine)){setError(error.message || 'プロフィールを読み込めません。');setStatus('');} }
}