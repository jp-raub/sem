module.exports=async({p,g,ok})=>{
 const api=(page,action,data,query='')=>page.evaluate(async({action,data,query})=>{
  const{token}=await(await fetch('/api/csrf')).json();
  const r=await fetch('/api/core/'+action+query,{method:data===undefined?'GET':'POST',
   headers:data===undefined?{}:{'Content-Type':'application/json','X-CSRF-TOKEN':token},
   body:data===undefined?undefined:JSON.stringify(data)});
  return{status:r.status,body:await r.json()};
 },{action,data,query});
 const random=()=>require('crypto').randomUUID(),scheduledDate=new Date().toISOString();
 const created=await api(p,'event',{title:'Native shortening',description:'',location:'',scheduledDate,
  opensAt:null,announcementMessage:'',phaseKind:'View',managerIds:[],rowVersion:0});
 ok(created.status===200,'Native real-clock event for shortening created');
 const id=created.body.id,q='?id='+id,part=random();
 let table=(await api(p,'timetable',undefined,q)).body;
 const saved=await api(p,'timetable',{rowVersion:table.rowVersion,roundStarts:false,clockFormat:'seconds',
  durationFormat:'colon',songIcon:'🎵',plannedEndAt:new Date(Date.parse(scheduledDate)+480000).toISOString(),
  parts:[{id:part,kind:'Transition',songId:null,name:'15 minute break',durationSeconds:900,icon:null}],
  acknowledgeOverrun:true},q);
 ok(saved.status===200,'Native planned 15-minute transition with8-minute target saved');
 const read=async()=>{const r=await api(p,'shortening',undefined,q);ok(r.status===200,'Native authorized proposal reads coherent state');return r.body;};
 let plan=await read();
 ok(!plan.live&&!plan.proposal.hasCuts&&plan.progressVersion===0,'Native default protects time without live capture');
 ok((await api(g,'shortening',undefined,q)).status===403,'Native member cannot read manager proposal');
 const edit=()=>({requestId:random(),rowVersion:plan.rowVersion,progressVersion:plan.progressVersion,
  partId:part,allowPrep:false,minPrepSeconds:0,allowDuration:true,minDurationSeconds:300});
 ok((await api(g,'shortening-policy',edit(),q)).status===403,'Native member cannot forge shortening conditions');
 ok((await api(g,'shortening-approve',{requestId:random(),rowVersion:plan.rowVersion,
  progressVersion:plan.progressVersion,token:plan.proposal.token},q)).status===403,'Native member cannot forge approval');
 const missingFloor=edit();delete missingFloor.minDurationSeconds;
 ok((await api(p,'shortening-policy',missingFloor,q)).status===400,'Native policy requires explicit minimum rather than implicit zero');
 ok((await api(p,'shortening-approve',{requestId:random(),rowVersion:plan.rowVersion,token:plan.proposal.token},q)).status===400,'Native approval requires explicit live version');
 const a=await p.context().newPage(),m=await g.context().newPage(),errors=[];
 a.on('pageerror',e=>errors.push(e.message));
 try{
  await a.goto('https://localhost:18443/events/'+id+'/progress');
  await a.locator('#shortening-panel').waitFor();
  ok(await a.locator('#shortening-approve').isDisabled(),'Actual manager panel shows protected empty state');
  await a.locator('#shortening-panel details > summary').click();
  await a.locator('[data-shortening-edit="'+part+'"]').click();
  await a.locator('#shortening-allow').check();await a.locator('#shortening-minimum').fill('5:00');
  ok(await a.locator('#progress-record').isDisabled(),'Policy draft disables adjacent live-record action');
  await a.locator('#shortening-policy-cancel').click();
  await a.locator('#shortening-minimum').waitFor({state:'hidden'});
  plan=await read();
  ok(plan.rowVersion===table.rowVersion+1&&!plan.proposal.hasCuts&&!plan.live,'Policy cancel leaves saved plan and live state unchanged');
  await a.locator('[data-shortening-edit="'+part+'"]').click();
  await a.locator('#shortening-allow').check();await a.locator('#shortening-minimum').fill('5:00');
  const previous=plan.rowVersion;
  await a.locator('#shortening-policy-save').click();
  await a.waitForFunction(v=>document.getElementById('shortening-panel')?.dataset.eventVersion===String(v),previous+1);
  await a.locator('#shortening-minimum').waitFor({state:'hidden'});
  plan=await read();
  ok(plan.proposal.cuts.length===1&&plan.proposal.cuts[0].afterSeconds===480&&
   plan.proposal.delaySeconds===420&&plan.proposal.remainingSeconds===0,'Actual condition save creates latest8-minute proposal');
  await a.setViewportSize({width:390,height:844});
  ok(await a.locator('#shortening-panel').isVisible()&&await a.locator('#shortening-cuts').isVisible(),
   'Mobile manager sees proposal without opening comparison table');
  const stale={requestId:random(),rowVersion:plan.rowVersion,progressVersion:plan.progressVersion,token:plan.proposal.token};
  const changed=await api(p,'shortening-policy',{...edit(),minDurationSeconds:400},q);
  ok(changed.status===200,'A separate current manager update changes proposal fingerprint');
  ok((await api(p,'shortening-approve',stale,q)).status===409,'Native stale proposal rejected with no application');
  plan=await read();
  ok(!plan.live&&plan.progressVersion===0,'Stale rejection leaves original capture and live estimates untouched');
  await a.locator('#progress-refresh').click();
  await a.waitForFunction(v=>document.getElementById('shortening-panel')?.dataset.eventVersion===String(v),plan.rowVersion);
  await a.locator('#shortening-approve').click();
  await a.waitForFunction(()=>document.getElementById('shortening-panel')?.dataset.progressVersion==='1');
  ok((await a.locator('#shortening-message').innerText()).includes('承認しました')&&
   await a.locator('#shortening-approve').isDisabled(),'One actual UI click applies proposal without a second modal or double approval');
  plan=await read();
  ok(plan.live&&plan.progressVersion===1&&!plan.proposal.hasCuts,'Actual approval persists live version and clears proposal');
  const original=(await api(p,'progress',undefined,q)).body;
  ok(original.managerRows[0].original.lengthSeconds===900&&original.managerRows[0].latest.lengthSeconds===480&&
   original.managerRows[0].points.every(x=>x.actual===null),'Native approval preserves original duration and creates no fake actual');
  await m.goto('https://localhost:18443/events/'+id+'/progress');await m.locator('#progress-state').waitFor();
  ok(await m.locator('#shortening-panel').count()===0,'Ordinary participant UI has no proposal or approval controls');
  await a.locator('#progress-participant-view').click();await a.locator('#shortening-panel').waitFor({state:'hidden'});
  ok(await a.locator('#shortening-panel').count()===0,'Manager participant perspective also removes proposal panel');
  await a.locator('#progress-manager-view').click();await a.locator('#shortening-panel').waitFor();
  await a.locator('#progress-record').click();
  await a.waitForFunction(()=>document.getElementById('progress-record')?.dataset.progressVersion==='2');
  await a.waitForFunction(()=>document.querySelector('[data-shortening-edit]')?.disabled===true);
  ok(await a.locator('[data-shortening-edit="'+part+'"]').isDisabled(),'Recorded transition protects policy editor immediately');
  plan=await read();
  ok((await api(p,'shortening-policy',edit(),q)).status===400,'Native forged started-policy update rejected');
  const beforeRestart=JSON.stringify((await api(p,'progress',undefined,q)).body.managerRows);
  if(process.env.SEM_PHASE_RESTART_COMMAND){
   require('child_process').execSync(process.env.SEM_PHASE_RESTART_COMMAND,{stdio:'inherit'});
   let ready=false;for(let n=0;n<60;n++){try{if((await fetch('http://127.0.0.1:18080/health/ready')).ok){ready=true;break;}}catch{}await new Promise(r=>setTimeout(r,1000));}
   ok(ready,'Shortening restart waits actual storage-ready');
   await p.goto('https://localhost:18443/');await p.waitForSelector('#issue');
   await g.goto('https://localhost:18443/');await g.waitForSelector('#request');
   const retained=await read();
   ok(retained.live&&retained.progressVersion===2&&retained.policies[0].policy.minDurationSeconds===400&&
    JSON.stringify((await api(p,'progress',undefined,q)).body.managerRows)===beforeRestart,
    'Restart retains policy original/latest/actual and authenticated management access');
  }
  ok(errors.length===0,'Actual shortening Blazor UI has no browser runtime errors');
 }finally{await a.close();await m.close();}
};