'use strict';
// Fresh, isolated events; existing policies and personal preference service remain authoritative.
module.exports=async({p,g,ok})=>{
 const api=(page,action,data,q='')=>page.evaluate(async({action,data,q})=>{const{token}=await(await fetch('/api/csrf')).json();const r=await fetch('/api/core/'+action+q,{method:data===undefined?'GET':'POST',headers:data===undefined?{}:{'Content-Type':'application/json','X-CSRF-TOKEN':token},body:data===undefined?undefined:JSON.stringify(data)});return{status:r.status,body:await r.json()};},{action,data,q});
 const create=async(title,phaseKind)=>{const edit={title,description:'',location:'',scheduledDate:new Date(Date.now()+86400000).toISOString(),opensAt:new Date(Date.now()+82800000).toISOString(),announcementMessage:'',phaseKind,managerIds:[],rowVersion:0};const r=await api(p,'event',edit);ok(r.status===200,'Notification UI fixture event '+phaseKind+' created');return{id:r.body.id,edit};};
 const first=await create('通知設定テスト A','View'),second=await create('通知設定テスト B','View'),hidden=await create('非公開の通知設定','Hidden');
 const a=await g.context().newPage(),m=await p.context().newPage(),errors=[];a.on('pageerror',e=>errors.push(e.message));m.on('pageerror',e=>errors.push(e.message));let pushWrites=0;
 await a.addInitScript(()=>{window.__permissionPrompts=0;if(window.Notification)Notification.requestPermission=async()=>{window.__permissionPrompts++;return 'denied';};});
 a.on('request',r=>{if(r.method()!=='GET'&&/^\/api\/(?:push(?:\/|-)|core\/device-notifications\/(?:register|unregister)$)/.test(new URL(r.url()).pathname))pushWrites++;});
 const ready=async page=>{await page.waitForSelector('#notification-events[data-ready="true"]');await page.waitForFunction(()=>document.getElementById('notification-events-error')?.textContent==='');};
 const choose=async(page,id)=>{await page.locator('#notification-event-select').selectOption(id);await page.waitForSelector('#part-start-settings[data-interactive="true"][data-event-id="'+id+'"]');await page.waitForFunction(()=>!document.getElementById('notification-event-select').disabled);};
 const own=async(event)=>{const r=await api(g,'part-start-preferences',undefined,'?id='+event.id);ok(r.status===200,'Member reads own selected event');return r.body;};
 try{
  await a.goto('https://localhost:18443/notifications');await ready(a);
  ok(await a.locator('h1').innerText()==='通知管理'&&await a.locator('#device-notifications-title').innerText()==='このブラウザーの通知設定','Integrated page has one title and distinct browser heading');
  ok((await a.locator('#notification-event-select option').allTextContents()).includes('通知設定テスト A')&&await a.locator('#notification-event-select option[value="'+hidden.id+'"]').count()===0,'Member sees accessible events, never hidden event label or ID');
  ok(await a.locator('#notification-event-select').inputValue()===''&&await a.locator('#part-start-settings').count()===0,'No implicit event selection or preference write on initial load');
  let view=await own(first);const part=view.parts[0];ok(!!part&&!part.enabled,'Initial selected event is OFF');
  await choose(a,first.id);const sw=a.locator('[data-part-start="'+part.partId+'"]');ok(await sw.getAttribute('aria-checked')==='false','Embedded preference reflects server state');
  await sw.click();await a.waitForFunction(id=>document.querySelector('[data-part-start="'+id+'"]')?.getAttribute('aria-checked')==='true',part.partId);await a.waitForFunction(()=>!document.getElementById('notification-event-select').disabled);
  ok((await own(first)).parts.find(x=>x.partId===part.partId).enabled,'Embedded ON saves only own preference');
  await choose(a,second.id);ok((await a.locator('#part-start-settings [role=switch]').allTextContents()).every(x=>x.endsWith('OFF')),'Switching event never reuses previous event ON state');
  await choose(a,first.id);ok(await sw.getAttribute('aria-checked')==='true','Returning event loads saved ON');
  await m.goto('https://localhost:18443/notifications');await ready(m);ok(await m.locator('#notification-event-select option[value="'+hidden.id+'"]').count()===1,'Manager may select its authorized hidden event');await choose(m,first.id);ok(await m.locator('[data-part-start="'+part.partId+'"]').getAttribute('aria-checked')==='false','Manager does not inherit member preference');
  view=await own(first);const current=view.parts.find(x=>x.partId===part.partId);ok((await api(g,'part-start-preferences',{partId:part.partId,enabled:false,version:current.version,eventVersion:view.eventVersion},'?id='+first.id)).status===200,'Concurrent own context changes setting');
  await sw.click();await a.waitForFunction(()=>document.getElementById('part-start-error')?.textContent.length>0);ok(await sw.getAttribute('aria-checked')==='false'&&(await own(first)).parts.find(x=>x.partId===part.partId).version===2,'Stale embedded write rejected and latest OFF shown, not retried');
  for(const colorScheme of ['light','dark']){await a.emulateMedia({colorScheme});for(const width of [320,390,768,1440]){await a.setViewportSize({width,height:900});ok(await a.evaluate(()=>document.documentElement.scrollWidth<=innerWidth),'Integrated notification page fits '+width+' '+colorScheme);}}
  ok((await a.locator('#notification-events').innerText()).includes('Push登録・通知許可とは別')&&(await a.locator('#notification-events').innerText()).includes('遡って送信しません'),'Personal opt-in and device permission/replay boundaries explicit');
  ok(await a.evaluate(()=>window.__permissionPrompts)===0&&pushWrites===0,'Page, event selection and personal switches never prompt or register browser Push');
  const workspace=(await api(p,'workspace')).body;const event=workspace.events.find(e=>e.id===first.id);ok((await api(p,'event',{...first.edit,phaseKind:'Hidden',rowVersion:event.rowVersion},'?id='+first.id)).status===200,'Fixture removes member event access');
  await a.locator('#part-start-refresh').click();await a.waitForFunction(()=>document.getElementById('part-start-error')?.textContent.includes('閲覧権限'));ok(await a.locator('#part-start-settings').count()===0,'Revoked read clears stale private switches');
  await a.locator('#notification-events-refresh').click();await a.waitForFunction(id=>!document.querySelector('#notification-event-select option[value="'+id+'"]')&&!document.getElementById('part-start-settings')&&document.getElementById('notification-events')?.dataset.ready==='true',first.id);ok(await a.locator('#notification-event-select option[value="'+first.id+'"]').count()===0&&await a.locator('#part-start-settings').count()===0,'Explicit list refresh clears selection and revoked event');
  await a.goto('https://localhost:18443/events/'+second.id+'/part-start-preferences');await a.waitForSelector('#part-start-settings[data-interactive="true"]');ok(await a.getByRole('link',{name:'通知管理へ',exact:true}).isVisible()&&await a.locator('h1').count()===1,'Existing personal preference URL remains usable and links back to management');
  ok(errors.length===0,'Integrated and legacy Blazor pages have no runtime errors');
 }finally{await a.close();await m.close();}
};
