module.exports=async({p,g,ok})=>{
 const api=(page,action,data,q='')=>page.evaluate(async({action,data,q})=>{const{token}=await(await fetch('/api/csrf')).json();const r=await fetch('/api/core/'+action+q,{method:data===undefined?'GET':'POST',headers:data===undefined?{}:{'Content-Type':'application/json','X-CSRF-TOKEN':token},body:data===undefined?undefined:JSON.stringify(data)});return{status:r.status,body:await r.json()};},{action,data,q});
 const data={title:'Native push rule arming',description:'',location:'',scheduledDate:new Date(Date.now()+30*60000).toISOString(),opensAt:null,announcementMessage:'',phaseKind:'Hidden',managerIds:[],rowVersion:0};
 const create=await api(p,'event',data);ok(create.status===200,'Native event with historical opening reminder created');const id=create.body.id,q='?id='+id;
 let view=(await api(p,'event-push-rules',undefined,q)).body;ok(view.rules.length===2&&view.rules.every(x=>!x.enabled)&&view.entries[0].state==='Disabled'&&!view.deliveryConnected,'Native manager rules default OFF and dormant delivery explicit');
 ok((await api(g,'event-push-rules',undefined,q)).status===403&&(await api(g,'event-push-rules',{kind:'OpeningReminder',enabled:true,version:0,eventVersion:view.eventVersion},q)).status===403,'Native non-manager denied rule read and mutation');
 const csrf=await p.evaluate(async q=>(await fetch('/api/core/event-push-rules'+q,{method:'POST',headers:{'Content-Type':'application/json'},body:'{}'})).status,q);ok(csrf===400,'Rule activation POST rejects missing CSRF');
 const a=await p.context().newPage(),member=await g.context().newPage(),errors=[];a.on('pageerror',e=>errors.push(e.message));
 try{
  await a.goto('https://localhost:18443/events/'+id+'/event-push-rules');await a.waitForFunction(()=>document.getElementById('event-push-rules')?.dataset.interactive==='true');await a.setViewportSize({width:390,height:844});
  ok(await a.locator('[data-push-rule="OpeningReminder"]').isVisible()&&(await a.locator('#event-push-rules').innerText()).includes('まだPushは送信されません'),'Actual mobile manager switch and no-send boundary visible');
  await a.locator('[data-push-rule="OpeningReminder"]').click();await a.waitForFunction(()=>document.querySelector('[data-push-rule="OpeningReminder"]')?.getAttribute('aria-checked')==='true');view=(await api(p,'event-push-rules',undefined,q)).body;
  ok(view.rules[0].version===1&&view.entries[0].state==='BeforeActivation','Actual manager ON stores activation and excludes old due without sending');
  ok((await api(p,'event-push-rules',{kind:'OpeningReminder',enabled:false,version:0,eventVersion:view.eventVersion},q)).status===409,'Stale native rule editor returns409');
  await a.locator('[data-push-rule="OpeningReminder"]').click();await a.waitForFunction(()=>document.querySelector('[data-push-rule="OpeningReminder"]')?.getAttribute('aria-checked')==='false');view=(await api(p,'event-push-rules',undefined,q)).body;
  ok(view.rules[0].version===2&&view.rules[0].armedAt===null&&view.entries[0].state==='Disabled','Actual OFF clears activation while keeping generation');
  await a.locator('[data-push-rule="OpeningReminder"]').click();await a.waitForFunction(()=>document.querySelector('[data-push-rule="OpeningReminder"]')?.getAttribute('aria-checked')==='true');view=(await api(p,'event-push-rules',undefined,q)).body;
  ok(view.rules[0].version===3&&view.entries[0].state==='BeforeActivation','Reactivation does not release previous dormant due entry');
  await a.reload();await a.waitForFunction(()=>document.getElementById('event-push-rules')?.dataset.interactive==='true');ok(await a.locator('[data-push-rule="OpeningReminder"]').getAttribute('aria-checked')==='true','Reload retains manager rule state');
  await member.goto('https://localhost:18443/events/'+id+'/event-push-rules');await member.waitForFunction(()=>document.getElementById('event-push-rule-error')?.textContent.includes('管理権限'));ok(await member.locator('#event-push-rules').count()===0,'Member actual rule screen hides all arming details');
  await a.goto('https://localhost:18443/events/'+id+'/notification-plan');await a.locator('#event-push-rules-link').waitFor();ok((await a.locator('#event-push-rules-link').getAttribute('href')).endsWith('/event-push-rules'),'Manager notification plan links to rule switches');
  ok(errors.length===0,'Event push rule actual Blazor has no runtime errors');
 }finally{await a.close();await member.close();}
};
