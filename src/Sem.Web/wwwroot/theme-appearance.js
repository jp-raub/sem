const API = '/api/core/';
const MAX_JSON = 4 * 1024 * 1024;
const MAX_CSRF = 64 * 1024;
const HEX = /^#[0-9a-fA-F]{6}$/;
const FALLBACK = {accent:'#1e6557',background:'#f5f7f3',surface:'#fffefa',text:'#18312e'};
const DARK = {background:'#15201c',surface:'#202d27',text:'#e1ebe5'};
const modeQuery = matchMedia('(prefers-color-scheme: dark)');
let generation = 0;
let activeController = null;
let snapshot = null;
let eventId = null;
let editorController = null;
let editorSnapshot = null;
let draft = null;
let editingCatalog = null;
let unknownOutcome = false;
let busy = false;

function isObject(x) { return x !== null && typeof x === 'object' && !Array.isArray(x); }
function validColor(x) { return typeof x === 'string' && HEX.test(x); }
function validMode(x) { return x === null || x === 0 || x === 1 || x === 2; }
function validPref(x) { return isObject(x) && (x.themeId === null || typeof x.themeId === 'string') && validMode(x.mode) && Number.isSafeInteger(x.rowVersion) && x.rowVersion >= 0; }
function validCatalogEntry(x) { return isObject(x)&&typeof x.id==='string'&&typeof x.name==='string'&&x.name.length<=30&&['accent','background','surface','text'].every(k=>validColor(x[k]))&&typeof x.builtIn==='boolean'&&Number.isSafeInteger(x.rowVersion)&&x.rowVersion>=0; }
function parseHex(x) { return validColor(x) ? x.toUpperCase() : null; }
function validateDto(d, expectedEvent) {
  if (!isObject(d) || typeof d.actorId !== 'string' || !d.actorId || typeof d.canManageEvent !== 'boolean' || typeof d.canManageSite !== 'boolean' ||
      !Array.isArray(d.catalog) || !validPref(d.personal) || !validPref(d.site) ||
      (expectedEvent === null ? d.event !== null : !validPref(d.event)) || !isObject(d.resolved) ||
      !['accent','background','surface','text'].every(k => isObject(d.resolved[k]) && parseHex(d.resolved[k].value)) || !validMode(d.resolved.mode)) return false;
  if (expectedEvent !== null && typeof d.eventId === 'string' && d.eventId !== expectedEvent) return false;
  const ids = new Set();
  for (const t of d.catalog) {
    if (!isObject(t) || typeof t.id !== 'string' || !t.id || ids.has(t.id) || typeof t.name !== 'string' || t.name.length > 30 ||
        !['accent','background','surface','text'].every(k => parseHex(t[k])) || typeof t.builtIn !== 'boolean' || !Number.isSafeInteger(t.rowVersion) || t.rowVersion < 0) return false;
    ids.add(t.id);
  }
  return true;
}
function safeEventId(raw) {
  if (typeof raw !== 'string' || !raw || raw.length > 256) return null;
  try { const id = decodeURIComponent(raw); return id && id.length <= 128 && !/[\u0000-\u001f\u007f/\\]/.test(id) ? id : null; } catch { return null; }
}
function currentEventId() {
  const match = /^\/events\/([^/]+)(?:\/|$)/.exec(location.pathname);
  if (match) return safeEventId(match[1]);
  if (location.pathname === '/appearance') {
    const value = new URL(location.href).searchParams.get('eventId');
    return value ? safeEventId(value) : null;
  }
  return null;
}
function setStatus(text, kind='info') {
  const el = document.getElementById('appearance-status');
  if (el) { el.textContent = text; el.dataset.kind = kind; }
}
function setEnabled(enabled) {
  document.querySelectorAll('#appearance-editor select,#appearance-editor button,#theme-catalog-editor input,#theme-catalog-save,#theme-catalog-cancel,#theme-catalog-list button').forEach(x => x.disabled = !enabled || busy || unknownOutcome);
  const refresh = document.getElementById('appearance-refresh'); if (refresh) refresh.disabled = busy;
}
async function decodeJsonResponse(r, limit) {
  const contentType=(r.headers.get('Content-Type')||'').split(';',1)[0].trim().toLowerCase();
  if(contentType!=='application/json') throw new Error('JSON応答ではありません。');
  const declared=r.headers.get('Content-Length');
  if(declared!==null&&(!/^\d+$/.test(declared.trim())||Number(declared)>limit)) throw new Error('応答サイズが上限を超えています。');
  if(!r.body) throw new Error('応答本文がありません。');
  const reader=r.body.getReader(),chunks=[]; let total=0;
  while(true){const {done,value}=await reader.read();if(done)break;total+=value.byteLength;if(total>limit){await reader.cancel();throw new Error('応答サイズが上限を超えています。');}chunks.push(value);}
  const bytes=new Uint8Array(total);let offset=0;for(const chunk of chunks){bytes.set(chunk,offset);offset+=chunk.byteLength;}
  return JSON.parse(new TextDecoder('utf-8',{fatal:true}).decode(bytes));
}
function readEndpoint(url, limit, signal) {
  return fetch(url, {method:'GET',credentials:'same-origin',mode:'same-origin',redirect:'error',cache:'no-store',headers:{Accept:'application/json'},signal})
    .then(async r => { if (!r.ok || r.redirected) {try{await r.body?.cancel();}catch{} throw Object.assign(new Error(`読み込みに失敗しました（${r.status}）。`),{status:r.status});} return decodeJsonResponse(r,limit); });
}
async function loadContext({manual=false}={}) {
  const myGeneration = ++generation; activeController?.abort(); const controller = new AbortController(); activeController = controller;
  const timer = setTimeout(()=>controller.abort(),25000);
  eventId = currentEventId();
  try {
    const url = new URL(API+'theme-appearance', location.origin); if (eventId !== null) url.searchParams.set('id',eventId);
    const envelope = await readEndpoint(url, MAX_JSON, controller.signal);
    if (!isObject(envelope) || (eventId === null ? envelope.eventId !== null : envelope.eventId !== eventId) || myGeneration !== generation || !validateDto(envelope.appearance,eventId)) throw new Error('表示設定の応答を検証できませんでした。');
    snapshot = {...envelope.appearance,eventId:envelope.eventId}; applyResolved(); unknownOutcome = false;
    if (manual && document.getElementById('appearance-editor')) setStatus('最新の設定を読み込みました。変更は行っていません。');
    if (document.getElementById('appearance-editor')) renderEditor();
    return true;
  } catch (e) {
    if (myGeneration !== generation || e.name === 'AbortError') { if (myGeneration === generation && document.getElementById('appearance-editor')) setStatus('読み込みがタイムアウトしました。再読込してください。','error'); return false; }
    if (e.status === 401 || e.status === 403) {
      snapshot=null; draft=null; unknownOutcome=false;
      for (const key of ['accent','background','surface','text']) document.documentElement.style.removeProperty('--appearance-'+key);
      delete document.documentElement.dataset.appearanceMode;
      const editor=document.getElementById('appearance-editor'),catalog=document.getElementById('appearance-catalog'); if(editor)editor.hidden=true; if(catalog)catalog.hidden=true;
    }
    if (document.getElementById('appearance-editor')) setStatus(e.status === 401 ? 'ログインが必要です。ログイン後に再読込してください。' : e.status === 403 ? 'この設定を表示する権限がありません。' : `設定を読み込めませんでした。${e.message} 再読込してください。`,'error');
    return false;
  } finally { clearTimeout(timer); }
}
function getResolvedBase() {
  if (!snapshot) return {...FALLBACK,mode:0};
  return {accent:parseHex(snapshot.resolved.accent.value)||FALLBACK.accent,background:parseHex(snapshot.resolved.background.value)||FALLBACK.background,
    surface:parseHex(snapshot.resolved.surface.value)||FALLBACK.surface,text:parseHex(snapshot.resolved.text.value)||FALLBACK.text,mode:snapshot.resolved.mode};
}
function effectiveDraft() {
  if (!snapshot) return {...FALLBACK,mode:0};
  const layers = [snapshot.personal, snapshot.event, snapshot.site];
  if (draft) { const index = draft.scope==='personal'?0:draft.scope==='event'?1:2; layers[index] = {themeId:draft.themeId,mode:draft.mode}; }
  let chosen = null, mode = null;
  for (const layer of layers) {
    if (!layer) continue;
    if (!chosen && layer.themeId) chosen = snapshot.catalog.find(t=>t.id===layer.themeId) || null;
    if (mode === null && layer.mode !== null) mode = layer.mode;
  }
  const server = getResolvedBase();
  let colors = chosen ? Object.fromEntries(['accent','background','surface','text'].map(k=>[k,parseHex(chosen[k])||server[k]])) : {...server};
  chosen ??= snapshot.catalog.find(t=>t.id==='green') || null;
  colors = chosen ? Object.fromEntries(['accent','background','surface','text'].map(k=>[k,parseHex(chosen[k])||server[k]])) : {...server};
  const resolvedMode = mode === null ? 0 : mode;
  const actualDark = resolvedMode === 2 || resolvedMode === 0 && modeQuery.matches;
  if (actualDark) colors = {accent:mixWhite(parseHex(chosen?.accent)||server.accent,.5),background:DARK.background,surface:DARK.surface,text:DARK.text};
  return {...colors,mode:resolvedMode,actualDark};
}
function mixWhite(hex, amount) { const n=hex.slice(1).match(/../g).map(v=>parseInt(v,16)); return '#'+n.map(v=>Math.round(v+(255-v)*amount).toString(16).padStart(2,'0')).join(''); }
function applyResolved() {
  const t = effectiveDraft(); const root=document.documentElement;
  for (const [name,value] of Object.entries({accent:t.accent,background:t.background,surface:t.surface,text:t.text})) if (parseHex(value)) root.style.setProperty('--appearance-'+name,value);
  root.dataset.appearanceMode = t.actualDark ? 'dark' : 'light';
}
function option(value,label,selected) { const o=document.createElement('option'); o.value=value; o.textContent=label; o.selected=value===selected; return o; }
function scopePreference(scope) { return scope==='personal'?snapshot.personal:scope==='event'?snapshot.event:snapshot.site; }
function renderEditor() {
  const page=document.getElementById('appearance-page'); if (!page || !snapshot) return;
  const editor=document.getElementById('appearance-editor'); editor.hidden=false;
  const scopes=document.getElementById('appearance-scope'); const selectedScope=draft?.scope || scopes.value || 'personal';
  const nextScopes=[['personal','自分の表示（このアカウント）']];
  if (snapshot.event && snapshot.canManageEvent) nextScopes.push(['event','このイベントの既定値']);
  if (snapshot.canManageSite) nextScopes.push(['site','サイト全体の既定値']);
  scopes.replaceChildren(...nextScopes.map(([v,l])=>option(v,l,selectedScope))); scopes.disabled=busy||unknownOutcome;
  const scope=nextScopes.some(([v])=>v===selectedScope)?selectedScope:'personal';
  if (draft && draft.scope!==scope) draft=null;
  const pref=scopePreference(scope); const themeSelect=document.getElementById('appearance-theme');
  themeSelect.replaceChildren(option('','上位設定を継承',draft?draft.themeId??'':pref?.themeId??''),...snapshot.catalog.map(t=>option(t.id,t.name,draft?draft.themeId??'':pref?.themeId??'')));
  themeSelect.disabled=busy||unknownOutcome;
  document.getElementById('appearance-mode').value = (draft?draft.mode:pref?.mode)===null|| (draft?draft.mode:pref?.mode)===undefined ? 'inherit' : String(draft?draft.mode:pref.mode);
  document.getElementById('appearance-mode').disabled=busy||unknownOutcome;
  const apply=document.getElementById('appearance-apply'),cancel=document.getElementById('appearance-cancel');
  apply.disabled=busy||unknownOutcome||!draft; cancel.disabled=busy||unknownOutcome||!draft;
  const note=document.getElementById('appearance-preview-note'); note.textContent=draft?'未保存のプレビューです。適用するまでサーバーには保存されません。':'設定の優先順位は個人 → イベント → サイトです。各設定は個別に継承されます。';
  const cat=document.getElementById('appearance-catalog'); cat.hidden=!snapshot.canManageSite;
  renderCatalog(); setEnabled(true);
}
function renderCatalog() {
  if (!snapshot) return;
  const list=document.getElementById('theme-catalog-list'); if (!list) return; list.replaceChildren();
  for (const t of snapshot.catalog) {
    const li=document.createElement('li'),swatch=document.createElement('span'),label=document.createElement('span'); swatch.className='theme-swatch'; swatch.style.backgroundColor=parseHex(t.accent)||FALLBACK.accent;
    swatch.setAttribute('aria-hidden','true'); label.className='theme-label'; label.textContent=t.name+(t.builtIn?'（組み込み）':''); li.append(swatch,label);
    if (snapshot.canManageSite) { const edit=document.createElement('button'); edit.type='button'; edit.textContent='編集'; edit.dataset.catalogEdit=t.id; edit.addEventListener('click',()=>openCatalog(t)); li.append(edit); if (!t.builtIn) { const del=document.createElement('button'); del.type='button'; del.textContent='削除'; del.dataset.catalogDelete=t.id; del.addEventListener('click',()=>deleteCatalog(t)); li.append(del); } }
    list.append(li);
  }
  const details=document.getElementById('theme-catalog-editor'); if (details) details.hidden=!snapshot.canManageSite;
}
function openCatalog(t=null) {
  editingCatalog=t?{id:t.id,rowVersion:t.rowVersion}:null;
  document.getElementById('theme-catalog-add').textContent=t?'テーマを編集':'テーマを追加';
  document.getElementById('theme-catalog-name').value=t?.name||'';
  for (const k of ['accent','background','surface','text']) document.getElementById('theme-catalog-'+k).value=parseHex(t?.[k])||FALLBACK[k];
  document.getElementById('theme-catalog-editor').open=true;
  setEnabled(true); document.getElementById('theme-catalog-name').focus();
}
function bindEditor() {
  const page=document.getElementById('appearance-page'); if (!page || page.dataset.appearanceBound) return;
  page.dataset.appearanceBound='true'; editorController=new AbortController(); const signal=editorController.signal;
  document.getElementById('appearance-scope').addEventListener('change',e=>{draft=null; renderEditor(); applyResolved();},{signal});
  document.getElementById('appearance-theme').addEventListener('change',e=>updateDraft({themeId:e.target.value||null}),{signal});
  document.getElementById('appearance-mode').addEventListener('change',e=>updateDraft({mode:e.target.value==='inherit'?null:Number(e.target.value)}),{signal});
  document.getElementById('appearance-cancel').addEventListener('click',()=>{draft=null;renderEditor();applyResolved();setStatus('プレビューをキャンセルしました。保存済み設定に戻しました。');},{signal});
  document.getElementById('appearance-apply').addEventListener('click',applyPreference,{signal});
  document.getElementById('appearance-refresh').addEventListener('click',async()=>{draft=null;busy=true;setEnabled(false);try{await loadContext({manual:true});}finally{busy=false;renderEditor();setEnabled(true);}},{signal});
  document.getElementById('theme-catalog-add').addEventListener('click',e=>{e.preventDefault();openCatalog();},{signal});
  document.getElementById('theme-catalog-save').addEventListener('click',saveCatalog,{signal});
  document.getElementById('theme-catalog-cancel').addEventListener('click',()=>{editingCatalog=null;document.getElementById('theme-catalog-editor').open=false;},{signal});
}
function updateDraft(changes) {
  const scope=document.getElementById('appearance-scope').value; const pref=scopePreference(scope);
  if (!pref || scope==='event'&&!snapshot.canManageEvent || scope==='site'&&!snapshot.canManageSite) return;
  draft={scope,themeId:draft?.scope===scope?draft.themeId:pref.themeId,mode:draft?.scope===scope?draft.mode:pref.mode,...changes};
  renderEditor(); applyResolved(); setStatus('未保存のプレビューです。適用するまで保存されません。');
}
async function getCsrf(signal) {
  const c=await readEndpoint('/api/csrf',MAX_CSRF,signal); if (!isObject(c)||typeof c.token!=='string'||c.token.length===0||c.token.length>8192) throw new Error('CSRFトークンを検証できませんでした。'); return c.token;
}
async function postOnce(url,payload,responseValidator) {
  const controller=new AbortController(),timer=setTimeout(()=>controller.abort(),25000); let attempted=false;
  try {
    const csrf=await getCsrf(controller.signal);
    attempted=true;
    const response=await fetch(url,{method:'POST',credentials:'same-origin',mode:'same-origin',redirect:'error',cache:'no-store',headers:{Accept:'application/json','Content-Type':'application/json','X-CSRF-TOKEN':csrf},body:JSON.stringify(payload),signal:controller.signal});
    if (response.redirected) throw Object.assign(new Error('リダイレクトされたため保存結果を確認できません。'),{unknown:true});
    if (!response.ok) {try{await response.body?.cancel();}catch{} throw Object.assign(new Error(`保存に失敗しました（${response.status}）。`),{status:response.status,unknown:response.status>=500});}
    const result=await decodeJsonResponse(response,MAX_JSON);
    if(!responseValidator(result)) throw new Error('保存応答を検証できませんでした。');
    return true;
  } catch(e) { if (attempted && e.status===undefined) e.unknown=true; throw e; }
  finally { clearTimeout(timer); }
}
function endpoint(path,params) { const u=new URL(API+path,location.origin); u.searchParams.set('expectedActor',snapshot.actorId); for(const [k,v] of Object.entries(params||{})) if(v!==null&&v!==undefined) u.searchParams.set(k,String(v)); return u; }
async function applyPreference() {
  if (!draft||busy||unknownOutcome) return;
  const scope=draft.scope, event=snapshot.eventId || eventId; let path,params={};
  if(scope==='personal')path='theme-personal';
  else if(scope==='event'&&snapshot.canManageEvent&&event){path='theme-event';params.id=event;}
  else if(scope==='site'&&snapshot.canManageSite)path='theme-site';
  else return setStatus('この範囲を変更する権限がありません。','error');
  const pref=scopePreference(scope); const body={themeId:draft.themeId,mode:draft.mode===null?null:Number(draft.mode),rowVersion:pref.rowVersion};
  await mutate(endpoint(path,params),body,'表示設定を保存しました。',validPref);
}
async function mutate(url,body,successMessage,responseValidator) {
  let completed=false; busy=true; setEnabled(false); setStatus('保存しています…');
  try { await postOnce(url,body,responseValidator); draft=null; editingCatalog=null; const ok=await loadContext(); if (!ok) { unknownOutcome=true; setStatus('保存要求は成功応答でしたが、最新状態を確認できません。自動再送しません。「設定を再読込」で状態を確認してください。','warning'); }
    else {completed=true;setStatus(successMessage);} }
  catch(e) { if (e.unknown) {unknownOutcome=true;setStatus('保存結果が不明です。二重登録を避けるため自動再送しません。「設定を再読込」で現在の状態を確認してください。','warning');}
    else if (e.status===401||e.status===403) { snapshot=null; draft=null; for(const k of ['accent','background','surface','text'])document.documentElement.style.removeProperty('--appearance-'+k); delete document.documentElement.dataset.appearanceMode; const ed=document.getElementById('appearance-editor'),cat=document.getElementById('appearance-catalog');if(ed)ed.hidden=true;if(cat)cat.hidden=true;setStatus('権限またはログイン状態が変更されたため、編集画面を閉じました。再読込してください。','error');}
    else setStatus(e.message||'保存できませんでした。変更内容を確認して再読込してください。','error'); }
  finally {busy=false;renderEditor();setEnabled(true);}
  return completed;
}
async function saveCatalog() {
  if(!snapshot?.canManageSite||busy||unknownOutcome)return;
  const name=document.getElementById('theme-catalog-name').value.trim();
  const body={name,accent:document.getElementById('theme-catalog-accent').value,background:document.getElementById('theme-catalog-background').value,surface:document.getElementById('theme-catalog-surface').value,text:document.getElementById('theme-catalog-text').value,rowVersion:editingCatalog?.rowVersion??0};
  if(!name||name.length>30||/[\u0000-\u001f\u007f]/.test(name)||!['accent','background','surface','text'].every(k=>validColor(body[k])))return setStatus('テーマ名（1〜30文字）と4色の値を確認してください。','error');
  const params={}; if(editingCatalog) params.id=editingCatalog.id;
  const saved=await mutate(endpoint('theme-catalog-save',params),body,editingCatalog?'テーマを更新しました。':'テーマを追加しました。',validCatalogEntry);
  if(saved) document.getElementById('theme-catalog-editor').open=false;
}
async function deleteCatalog(t) {
  if(!snapshot?.canManageSite||t.builtIn||busy||unknownOutcome)return;
  if(!window.confirm(`「${t.name}」を削除しますか？使用中のテーマは削除できません。`))return;
  await mutate(endpoint('theme-catalog-delete',{id:t.id}),{rowVersion:t.rowVersion},'テーマを削除しました。',x=>isObject(x)&&typeof x.message==='string');
}
function attachPage() { bindEditor(); if (snapshot) renderEditor(); }
function init() {
  const nextEvent=currentEventId(); if(nextEvent!==eventId||!snapshot) {
    if(nextEvent!==eventId){snapshot=null;draft=null;for(const k of ['accent','background','surface','text'])document.documentElement.style.removeProperty('--appearance-'+k);delete document.documentElement.dataset.appearanceMode;const ed=document.getElementById('appearance-editor'),cat=document.getElementById('appearance-catalog');if(ed)ed.hidden=true;if(cat)cat.hidden=true;}
    eventId=nextEvent; loadContext();
  }
  const appearanceLink=document.querySelector('#navigation-panel a[href^="/appearance"]');
  if(appearanceLink) appearanceLink.href=nextEvent?'/appearance?eventId='+encodeURIComponent(nextEvent):'/appearance';
  attachPage();
}
modeQuery.addEventListener('change',()=>{ if(snapshot){applyResolved();} });
document.addEventListener('DOMContentLoaded',init,{once:true});
document.addEventListener('enhancedload',init);
document.addEventListener('blazor:navigation-end',init);
window.addEventListener('popstate',init);
if(document.readyState!=='loading') init();
