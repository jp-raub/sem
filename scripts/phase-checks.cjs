module.exports=async({p,g,ok})=>{
 const api=(page,action,data,query='')=>page.evaluate(async({action,data,query})=>{const {token}=await(await fetch('/api/csrf')).json();const r=await fetch('/api/core/'+action+query,{method:data===undefined?'GET':'POST',headers:data===undefined?{}:{'Content-Type':'application/json','X-CSRF-TOKEN':token},body:data===undefined?undefined:JSON.stringify(data)});return {status:r.status,body:await r.json()}},{action,data,query});
 const before=(await api(p,'workspace')).body;
 if(process.env.SEM_PHASE_UPGRADE_COMMAND){require('child_process').execSync(process.env.SEM_PHASE_UPGRADE_COMMAND,{stdio:'inherit'});let ready=false;for(let n=0;n<60;n++){try{if((await fetch('http://127.0.0.1:18080/health/ready')).ok){ready=true;break}}catch{}await new Promise(r=>setTimeout(r,1000))}ok(ready,'Phase candidate starts against populated foundation DB');await p.goto('https://localhost:18443/');await p.waitForSelector('#issue');await g.goto('https://localhost:18443/');await g.waitForSelector('#request');}
 const after=(await api(p,'workspace')).body;
 ok(after.profile.displayName===before.profile.displayName&&after.service.name===before.service.name&&after.events.length===before.events.length&&after.events.every(e=>before.events.some(o=>o.id===e.id&&o.title===e.title&&o.rowVersion===e.rowVersion&&JSON.stringify(o.managerIds)===JSON.stringify(e.managerIds))),'Phase upgrade preserves formal events, managers, versions and profiles');
 const event=after.events.find(e=>e.title==='Core multi-manager event'||e.title==='Manager updated overview');const query='?id='+event.id;
 let plan=(await api(p,'phases',undefined,query)).body;
 ok(plan.phases.length===2&&plan.manualOverride&&plan.phases.filter(x=>x.isActive).length===1,'Existing event receives two defaults with preserved manual visibility');
 ok((await api(g,'phases',undefined,query)).status===403,'Non-manager cannot read private phase-management plan');
 const id=require('crypto').randomUUID();const row={id,name:'Native custom phase',maxPicks:-1,activateAt:null,deactivateAt:null};
 let edit={rowVersion:plan.rowVersion,manualOverride:true,activePhaseId:id,customPhases:[row]};
 ok((await api(p,'phases',{...edit,customPhases:[{...row,ActivateAt:'2026-11-01T12:00:00'}]},query)).status===400,'Nested phase date validation rejects timezone-free casing bypass');
 plan=(await api(p,'phases',edit,query)).body;
 ok(plan.phases.some(x=>x.id===id&&x.isActive&&x.maxPicks===-1),'Native API persists manual custom phase with unlimited song count');
 ok((await api(p,'phases',edit,query)).status===409,'Stale phase-plan version is rejected');
 ok((await api(g,'phases',{...edit,rowVersion:plan.rowVersion},query)).status===403,'Member cannot promote own phase-management access');
 ok((await api(p,'phases',{...edit,rowVersion:plan.rowVersion,customPhases:[]},query)).status===400,'Selected active phase deletion is denied');
 const scheduled={...row,activateAt:new Date(Date.now()+3000).toISOString(),deactivateAt:new Date(Date.now()+120000).toISOString()};
 plan=(await api(p,'phases',{rowVersion:plan.rowVersion,manualOverride:false,activePhaseId:id,customPhases:[scheduled]},query)).body;
 let auto=false;for(let n=0;n<12;n++){await new Promise(r=>setTimeout(r,1500));plan=(await api(p,'phases',undefined,query)).body;if(plan.phases.some(x=>x.id===id&&x.isActive)){auto=true;break}}
 ok(auto&&!plan.manualOverride,'Actual background worker activates scheduled custom phase without a page simulation');
 plan=(await api(p,'phases',{rowVersion:plan.rowVersion,manualOverride:true,activePhaseId:id,customPhases:[row]},query)).body;
 const w=await p.context().newPage();const errors=[];w.on('pageerror',e=>errors.push(e.message));
 try{
  await w.goto('https://localhost:18443/events/'+event.id+'/phases');await w.waitForSelector('#phase-edit');await w.waitForTimeout(4000);
  await w.locator('#phase-edit').click();await w.waitForSelector('#phase-add');await w.locator('#phase-add').click();await w.locator('#phase-name').fill('Cancelled custom draft');await w.locator('#phase-draft-apply').click();await w.locator('#phase-cancel').click();await w.waitForSelector('#phase-edit');
  ok(!(await api(p,'phases',undefined,query)).body.phases.some(x=>x.name==='Cancelled custom draft'),'Blazor cancellation leaves custom drafts unpersisted');
  await w.locator('#phase-edit').click();await w.locator('[data-phase-name="'+id+'"]').click();await w.locator('#phase-name').fill('Saved Blazor phase');await w.locator('#phase-max').fill('3');await w.locator('#phase-draft-apply').click();await w.locator('#phase-save').click();await w.waitForSelector('#phase-edit');
  plan=(await api(p,'phases',undefined,query)).body;ok(plan.phases.some(x=>x.id===id&&x.name==='Saved Blazor phase'&&x.maxPicks===3&&x.isActive),'Real Blazor modal draft persists through the atomic final save');
  await w.locator('#phase-edit').click();await w.locator('[data-phase-name="'+id+'"]').click();ok(await w.locator('#phase-delete').isDisabled(),'Actual modal disables deleting the selected active phase');await w.locator('#phase-close').click();await w.locator('#phase-cancel').click();
  if(process.env.SEM_PHASE_RESTART_COMMAND){require('child_process').execSync(process.env.SEM_PHASE_RESTART_COMMAND,{stdio:'inherit'});await require('./storage-ready.cjs')();await p.goto('https://localhost:18443/');await p.waitForSelector('#issue');const retained=(await api(p,'phases',undefined,query)).body;ok(retained.phases.some(x=>x.id===id&&x.isActive&&x.maxPicks===3)&&retained.manualOverride,'Restart preserves native session and manual custom phase');}
  await p.locator('[data-action=logout]').click();await p.waitForSelector('#login');await p.locator('#login [name=email]').fill('admin@example.test');await p.locator('#login button').click();await p.waitForSelector('#issue');ok((await api(p,'phases',undefined,query)).status===200,'Native newly added passkey still logs in after phase migration and restart');
  ok(errors.length===0,'Phase management has no browser runtime errors');
 }finally{await w.close();}
};
