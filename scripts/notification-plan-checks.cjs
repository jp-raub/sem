module.exports=async({p,g,ok})=>{
 const api=(page,action,data,q='')=>page.evaluate(async({action,data,q})=>{
  const {token}=await(await fetch('/api/csrf')).json();const r=await fetch('/api/core/'+action+q,{method:data===undefined?'GET':'POST',headers:data===undefined?{}:{'Content-Type':'application/json','X-CSRF-TOKEN':token},body:data===undefined?undefined:JSON.stringify(data)});return{status:r.status,body:await r.text()};
 },{action,data,q});
 const get=async(page,action,q='')=>{const r=await api(page,action,undefined,q);return{status:r.status,body:JSON.parse(r.body)};};
 const create=await api(p,'event',{title:'Native notification plan',description:'',location:'',scheduledDate:new Date(Date.now()+86400000).toISOString(),opensAt:null,announcementMessage:'',phaseKind:'Hidden',managerIds:[],rowVersion:0});
 ok(create.status===200,'Notification plan native hidden event created');const id=JSON.parse(create.body).id,q='?id='+id;
 let defs=(await get(p,'definitions',q)).body;const voice={id:require('crypto').randomUUID(),name:'Plan Voice',category:'Sing'};await api(p,'definitions',{rowVersion:defs.rowVersion,definitions:[voice]},q);
 let songs=(await get(p,'songs',q)).body;const rows=['Native notification song A','Native notification song B'].map(title=>({id:require('crypto').randomUUID(),title,artist:'',performanceSeconds:180,prepSeconds:0,definitionIds:[voice.id]}));await api(p,'songs',{rowVersion:songs.rowVersion,defaultPrepSeconds:0,songs:rows},q);
 const timetable=(await get(p,'timetable',q)).body;let read=await get(p,'notification-plan',q),v=read.body;
 ok(read.status===200&&v.previewOnly&&v.timeZone==='Asia/Tokyo'&&v.entries.length===3,'Actual API exposes read-only approved two-kind plan');
 ok(Date.parse(v.entries[0].dueAt)===Date.parse(timetable.scheduledDate)-7200000,'Native no-opening reminder exactly120minutes before start');
 ok(Date.parse(v.entries[1].dueAt)===Date.parse(timetable.calculation.rows[0].prepStart)-600000,'Native first-song notification uses latest prep minus10minutes');
 ok(v.entries[2].dueAt===null&&v.entries[2].state==='WaitingActual'&&v.entries[2].previousPartId===rows[0].id,'Native later song waits actual preceding performance start');
 ok((await api(p,'notification-plan',{},q)).status===405,'Notification plan API provides no POST queue/send action');
 ok((await get(g,'notification-plan',q)).status===403&&(await get(p,'notification-plan','?id=missing')).status===403,'Native member and missing-event probing denied');
 ok(!JSON.stringify(v).includes('@example.test')&&!JSON.stringify(v).includes('endpoint')&&!JSON.stringify(v).includes('protectedPayload'),'Plan DTO excludes recipient and subscription private data');
 const a=await p.context().newPage(),m=await g.context().newPage(),errors=[];a.on('pageerror',e=>errors.push(e.message));
 try{
  await a.goto('https://localhost:18443/events/'+id+'/notification-plan');await a.waitForFunction(()=>document.getElementById('notification-plan')?.dataset.interactive==='true');
  ok((await a.locator('#notification-plan-policy').innerText()).includes('実送信')&&await a.locator('[data-notification-plan-id]').count()===3,'Actual Blazor preview labels no-send and shows three schedule entries');
  await a.setViewportSize({width:390,height:844});ok(await a.locator('#notification-plan-refresh').isVisible()&&(await a.locator('[data-notification-plan-due="song:'+rows[1].id+'"]').innerText()).includes('未確定'),'Mobile schedule UI exposes waiting-actual instead of invented due');
  let ev=(await get(p,'workspace')).body.events.find(x=>x.id===id);const opensAt=new Date(Date.parse(ev.scheduledDate)-1800000).toISOString();
  ok((await api(p,'event',{title:ev.title,description:ev.description,location:ev.location,scheduledDate:ev.scheduledDate,opensAt,announcementMessage:ev.announcementMessage,phaseKind:ev.phaseKind,managerIds:ev.managerIds,rowVersion:ev.rowVersion},q)).status===200,'Native event opening change saved through existing formal endpoint');
  await a.locator('#notification-plan-refresh').click();await a.waitForFunction(old=>document.getElementById('notification-plan')?.dataset.fingerprint!==old,v.fingerprint);
  const fresh=(await get(p,'notification-plan',q)).body;ok(Date.parse(fresh.entries[0].dueAt)===Date.parse(opensAt)-7200000&&fresh.entries[1].previousPartId==='opening'&&fresh.entries[1].dueAt===null,'Saved opening change refreshes basis fingerprint and first-song waiting state');
  ok((await a.locator('[data-notification-plan-due="song:'+rows[0].id+'"]').innerText()).includes('未確定'),'Real UI refresh clears obsolete first-song fallback time after opening inserted');
  await m.goto('https://localhost:18443/events/'+id+'/notification-plan');await m.waitForFunction(()=>document.getElementById('notification-plan-error')?.textContent.includes('管理権限'));ok(await m.locator('#notification-plan').count()===0,'Member real UI exposes no hidden notification schedule');
  await a.goto('https://localhost:18443/events/'+id+'/timetable');await a.locator('#notification-plan-link').waitFor();ok((await a.locator('#notification-plan-link').getAttribute('href')).endsWith('/notification-plan'),'Actual manager timetable links to schedule preview');
  const emails=await(await fetch('http://127.0.0.1:18025')).json();ok(!emails.some(x=>x.body.includes('Native notification plan')),'Notification preview and event edits do not send any notification-plan SMTP');
  ok(errors.length===0,'Notification plan actual Blazor has no runtime errors');
 }finally{await a.close();await m.close();}
};
