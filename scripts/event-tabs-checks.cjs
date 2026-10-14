'use strict';
module.exports=async({p,g,ok})=>{
 const origin='https://localhost:18443';
 const api=(action,data,q='')=>p.evaluate(async({action,data,q})=>{const{token}=await(await fetch('/api/csrf')).json();const r=await fetch('/api/core/'+action+q,{method:data===undefined?'GET':'POST',headers:data===undefined?{}:{'Content-Type':'application/json','X-CSRF-TOKEN':token},body:data===undefined?undefined:JSON.stringify(data)});return{status:r.status,body:await r.json()};},{action,data,q});
 const fixture=(await api('workspace')).body.events.find(e=>e.title==='Profile-link native fixture');if(!fixture)throw Error('Profile-link event missing');
 const today=new Date().toISOString();const created=await api('event',{title:'Native tab host <img src=x>',description:'Synthetic overview',location:'Synthetic hall',scheduledDate:today,opensAt:null,announcementMessage:'',phaseKind:'View',managerIds:[],rowVersion:0});if(created.status!==200)throw Error('Native day event creation failed');
 const a=await g.context().newPage(),m=await p.context().newPage(),errors=[];for(const page of [a,m])page.on('pageerror',e=>errors.push(e.message));
 const ready=async page=>page.locator('[data-event-tabs-ready=true]').waitFor();
 const selected=page=>page.locator('[role=tab][aria-selected=true]').getAttribute('data-event-tab');
 try{
  await a.setViewportSize({width:390,height:844});await a.goto(origin+'/events/'+fixture.id);await ready(a);
  ok((await a.locator('[role=tab]').evaluateAll(bs=>bs.map(b=>b.dataset.eventTab))).join(',')==='main,roster','Ordinary off-day event shows only main and public roster tabs');
  ok(await a.locator('#event-overview-title').innerText()===fixture.title,'Main overview reads approved event title');
  await a.locator('[data-event-tab=main]').focus();await a.keyboard.press('End');await a.waitForURL('**/participants');await ready(a);await a.locator('[data-participant-roster-user]').first().waitFor();
  ok(await selected(a)==='roster'&&await a.locator('#event-tab-panel').getAttribute('aria-labelledby')==='event-tab-roster','Keyboard End selects actual authorized roster route and labelled panel');
  const scope=await a.evaluate(()=>Object.keys(sessionStorage).filter(k=>k.startsWith('sem.event-tab.v1:')));
  ok(scope.length===1&&await a.evaluate(k=>sessionStorage.getItem(k),scope[0])==='roster','Native session storage contains only current scoped tab identifier');
  await a.reload();await ready(a);ok(await selected(a)==='roster','Native reload keeps actual selected roster route');
  await a.goto(origin+'/events/'+fixture.id);await a.waitForURL('**/participants');await ready(a);ok(await selected(a)==='roster','Returning to same event restores saved permitted tab');
  await a.locator('[data-event-tab=main]').click();await a.waitForURL('**?tab=main');await ready(a);ok(await selected(a)==='main','Explicit main selection avoids restore loop');
  await a.goto(origin+'/events/'+created.body.id);await ready(a);ok((await a.locator('[role=tab]').evaluateAll(bs=>bs.map(b=>b.dataset.eventTab))).join(',')==='main,roster,progress','Ordinary on-day event includes read-only progress but no management tabs');
  ok(await selected(a)==='main','Different event does not inherit roster selection');
  await m.goto(origin+'/events/'+created.body.id);await ready(m);ok((await m.locator('[role=tab]').evaluateAll(bs=>bs.map(b=>b.dataset.eventTab))).join(',')==='main,roster,checkin,progress,notifications','Current global manager on day sees approved five-tab order');
  ok(await m.locator('#event-overview-title img').count()===0&&await m.locator('#event-overview-title').innerText()==='Native tab host <img src=x>','Overview encodes title rather than interpreting markup');
  await m.locator('[data-event-tab=checkin]').click();await m.waitForURL('**/reception');await ready(m);await m.locator('#qr-reader-input').waitFor();await m.locator('#qr-reader-input').fill('synthetic-unsent-input');await m.locator('[data-event-tab=roster]').click();await m.waitForFunction(()=>document.getElementById('qr-reader-error').textContent.includes('未送信'));
  ok(m.url().endsWith('/reception')&&await selected(m)==='checkin'&&await m.locator('#qr-reader-input').inputValue()==='synthetic-unsent-input','Unsent QR input blocks real tab route and preserves current input');
  await m.locator('#qr-reader-discard-draft').click();await m.locator('[data-event-tab=roster]').click();await m.waitForURL('**/participants');await ready(m);ok(await selected(m)==='roster','Explicit unsent QR discard unlocks tab routing without receipt mutation');
  await m.goto(origin+'/events/'+fixture.id+'/reception');await ready(m);await m.locator('[data-reception-select]').first().waitFor();await m.locator('[data-reception-select]').first().click();await m.locator('#reception-note').waitFor();await m.locator('#reception-note').fill('synthetic unsent note');await m.locator('[data-event-tab=roster]').click();await m.waitForFunction(()=>document.getElementById('reception-error').textContent.includes('未送信'));
  ok(m.url().endsWith('/reception')&&await m.locator('#reception-note').inputValue()==='synthetic unsent note','Manual receipt draft blocks tab navigation and keeps unsent note');
  await m.locator('#reception-discard-draft').click();await m.locator('[data-event-tab=roster]').click();await m.waitForURL('**/participants');await ready(m);ok(await selected(m)==='roster','Explicit manual draft discard unlocks navigation');
  await a.goto(origin+'/events/'+fixture.id+'/participants');await ready(a);await a.screenshot({path:'verification/event-tabs-mobile.png',fullPage:false});await m.setViewportSize({width:1100,height:800});await m.goto(origin+'/events/'+created.body.id+'?tab=main');await ready(m);await m.screenshot({path:'verification/event-tabs-desktop.png',fullPage:false});
  ok(errors.length===0,'Actual event tab routes and guarded switches have no client runtime errors');
 }finally{await a.close();await m.close();}
};
