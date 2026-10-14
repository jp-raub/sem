'use strict';
module.exports=async({p,g,ok})=>{
 const origin='https://localhost:18443',api=(page,q='')=>page.evaluate(async q=>{const r=await fetch('/api/core/participant-roster'+q);return{status:r.status,cache:r.headers.get('cache-control'),body:r.ok?await r.json():null};},q);
 const e=await p.evaluate(async()=>{const w=await(await fetch('/api/core/workspace')).json();return w.events.find(e=>e.title==='Profile-link native fixture');});if(!e)throw Error('Synthetic participant roster event missing');const q='?id='+encodeURIComponent(e.id),first=await api(p,q);
 ok(first.status===200&&first.cache==='no-store'&&first.body.people.length>0&&first.body.people.length<=100&&first.body.participantCount>=first.body.people.length,'Public roster exposes bounded current registration pages without receptionist state');
 if(first.body.nextUserId){const second=await api(p,q+'&afterUserId='+encodeURIComponent(first.body.nextUserId));ok(second.status===200&&second.body.people.length>0&&second.body.people.every(x=>!first.body.people.some(y=>y.userId===x.userId)),'Public roster continuation returns distinct remaining current participants');}
 const member=await api(g,q);ok(member.status===200&&Object.keys(member.body.people[0]).sort().join(',')==='displayName,parts,spectator,userId','Ordinary event viewer receives only approved public roster fields');
 ok((await api(p,q+'&afterUserId='+encodeURIComponent('bad\n'))).status===400,'Control-bearing public cursor is rejected');
 const post=await p.evaluate(async q=>{const{token}=await(await fetch('/api/csrf')).json();return(await fetch('/api/core/participant-roster'+q,{method:'POST',headers:{'Content-Type':'application/json','X-CSRF-TOKEN':token},body:'{}'})).status;},q);ok(post===405,'Public roster has no mutation endpoint');
 const a=await g.context().newPage(),errors=[];a.on('pageerror',e=>errors.push(e.message));try{
  await a.setViewportSize({width:390,height:844});await a.goto(origin+'/events/'+e.id+'/participants');await a.locator('[data-participant-roster-user]').first().waitFor();const ids=await a.locator('[data-participant-roster-user]').evaluateAll(x=>x.map(e=>e.dataset.participantRosterUser));
  ok(ids.length===first.body.people.length&&await a.locator('#participant-roster-next').isEnabled()===!!first.body.nextUserId,'Actual member mobile roster shows current bounded page');
  if(first.body.nextUserId){
  await a.locator('#participant-roster-next').click();await a.waitForFunction(id=>document.querySelector('[data-participant-roster-user]')?.dataset.participantRosterUser!==id,ids[0]);ok((await a.locator('[data-participant-roster-user]').evaluateAll(x=>x.map(e=>e.dataset.participantRosterUser))).every(x=>!ids.includes(x)),'Actual public roster next page has no duplicates');
  await a.locator('#participant-roster-back').click();await a.waitForFunction(id=>document.querySelector('[data-participant-roster-user]')?.dataset.participantRosterUser===id,ids[0]);ok(JSON.stringify(await a.locator('[data-participant-roster-user]').evaluateAll(x=>x.map(e=>e.dataset.participantRosterUser)))===JSON.stringify(ids),'Actual public roster previous page restores stable cursor');
  }else ok(await a.locator('#participant-roster-back').isDisabled(),'Actual first complete roster page cannot navigate beyond current registrations');
  const text=await a.locator('.participant-roster-table').innerText();ok(!text.includes('@example.test')&&!text.includes('PRIVATE')&&errors.length===0,'Public roster contains no private contacts/answers or client runtime errors');
  await a.screenshot({path:'verification/participant-roster-mobile.png',fullPage:false});await a.setViewportSize({width:1100,height:800});await a.screenshot({path:'verification/participant-roster-desktop.png',fullPage:false});
 }finally{await a.close();}
};
