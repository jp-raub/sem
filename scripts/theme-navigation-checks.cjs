'use strict';
// Only fresh synthetic events are changed. Existing personal/site preferences remain untouched.
module.exports=async({p,ok})=>{
 const assert=require('node:assert/strict');
 const api=(action,data,query='')=>p.evaluate(async({action,data,query})=>{
  const headers={'Accept':'application/json'};if(data!==undefined){headers['Content-Type']='application/json';headers['X-CSRF-TOKEN']=(await(await fetch('/api/csrf')).json()).token;}
  const r=await fetch('/api/core/'+action+query,{method:data===undefined?'GET':'POST',headers,body:data===undefined?undefined:JSON.stringify(data)});
  return{status:r.status,body:await r.json()};
 },{action,data,query});
 const read=async id=>{const r=await api('theme-appearance',undefined,id?'?id='+id:'');assert.equal(r.status,200);return r.body.appearance;};
 const ids=[];
 for(const [name,themeId,mode]of [['Rose','rose',1],['Blue','blue',2]]){
  const r=await api('event',{title:'Isolated theme '+name,description:'',location:'',scheduledDate:new Date(Date.now()+86400000).toISOString(),opensAt:null,announcementMessage:'',phaseKind:'Hidden',managerIds:[],rowVersion:0});assert.equal(r.status,200);const id=r.body.id;ids.push(id);
  const a=await read(id);const saved=await api('theme-event',{themeId,mode,rowVersion:a.event.rowVersion},'?id='+id+'&expectedActor='+encodeURIComponent(a.actorId));assert.equal(saved.status,200);
 }
 const w=await p.context().newPage(),errors=[];let writes=0;w.on('pageerror',e=>errors.push(e.message));w.on('request',r=>{if(r.method()!=='GET'&&new URL(r.url()).pathname.startsWith('/api/'))writes++;});
 const color=()=>w.evaluate(()=>document.documentElement.style.getPropertyValue('--appearance-accent').toLowerCase());
 const expected=async id=>{const d=await read(id);const hex=d.resolved.accent.value.toLowerCase();const accent=d.resolved.mode===2?'#'+hex.slice(1).match(/../g).map(v=>Math.round(parseInt(v,16)+(255-parseInt(v,16))*.5).toString(16).padStart(2,'0')).join(''):hex;return{accent,mode:d.resolved.mode};};
 const wait=async id=>{const e=await expected(id);await w.waitForFunction(({accent,mode})=>document.documentElement.style.getPropertyValue('--appearance-accent').toLowerCase()===accent&&document.documentElement.dataset.appearanceMode===(mode===2?'dark':'light'),e);};
 const navigate=async path=>{
  const enhancedBefore=await w.evaluate(()=>window.themeEnhancedCount);
  await w.evaluate(path=>{const a=document.createElement('a');a.id='synthetic-theme-navigation';a.href=path;a.textContent='Synthetic navigation';document.body.append(a);},path);
  await w.locator('#synthetic-theme-navigation').click();await w.waitForURL('https://localhost:18443'+path);
  await w.waitForFunction(n=>window.themeEnhancedCount>n,enhancedBefore);
  assert.equal(await w.evaluate(()=>window.themeDocumentSentinel),'unchanged-document','must use actual Blazor enhanced navigation, not full reload');
 };
 try{
  await w.goto('https://localhost:18443/workspace');await wait(null);ok(true,'Theme: cold authenticated document applies server palette without reload');
  await w.evaluate(()=>{window.themeDocumentSentinel='unchanged-document';window.themeEnhancedCount=0;window.Blazor.addEventListener('enhancedload',()=>window.themeEnhancedCount++);});
  await navigate('/events/'+ids[0]);await wait(ids[0]);ok(true,'Theme: first real enhanced event entry applies event palette without reload');
  const before=await color();await navigate('/events/'+ids[0]+'/questions');await wait(ids[0]);ok(await color()===before,'Theme: same-event enhanced page retains tokens after root patch');
  await navigate('/events/'+ids[1]);await wait(ids[1]);ok(await w.evaluate(()=>document.documentElement.dataset.appearanceMode)==='dark','Theme: first cross-event navigation applies dark event palette');
  await navigate('/workspace');await wait(null);ok(true,'Theme: leaving event restores site/personal context without reload');
  await navigate('/appearance');await w.waitForSelector('#appearance-editor:visible');await w.waitForFunction(()=>!document.getElementById('appearance-theme').disabled);
  const saved=await read(null);await w.locator('#appearance-theme').selectOption('rose');await w.locator('#appearance-mode').selectOption('1');
  ok(await w.locator('#appearance-apply').isEnabled(),'Theme: first enhanced appearance page binds editor and previews unsaved preferences');
  await w.locator('#appearance-cancel').click();await wait(null);const after=await read(null);
  ok(JSON.stringify(saved.personal)===JSON.stringify(after.personal),'Theme: preview cancel never saves personal preferences');
  ok(writes===0,'Theme: automatic navigation and preview never issue writes');ok(errors.length===0,'Theme: real application lifecycle has no browser errors');
 }finally{await w.close();}
};
