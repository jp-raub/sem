module.exports=async({p,g,ok})=>{
 const api=(page,action,data,q='')=>page.evaluate(async({action,data,q})=>{const {token}=await(await fetch('/api/csrf')).json();const r=await fetch('/api/core/'+action+q,{method:data===undefined?'GET':'POST',headers:data===undefined?{}:{'Content-Type':'application/json','X-CSRF-TOKEN':token},body:data===undefined?undefined:JSON.stringify(data)});return{status:r.status,body:await r.text()};},{action,data,q});
 const get=async(page,action,q='')=>{const r=await api(page,action,undefined,q);return{status:r.status,body:JSON.parse(r.body)};};
 const id=(await get(p,'workspace')).body.events.find(x=>x.title==='Native notification plan').id,q='?id='+id;
 async function current(){for(let n=0;n<100;n++){const r=await get(p,'notification-reservations',q);if(r.status===200&&r.body.isCurrent)return r.body;await new Promise(r=>setTimeout(r,500));}throw Error('Reservation worker did not reconcile current event');}
 let ledger=await current();ok(ledger.dormant&&ledger.reservations.length===3&&ledger.version>0,'Native worker persists current dormant three-row ledger');
 ok(ledger.reservations.find(x=>x.id==='opening').dueAt!==null&&ledger.reservations.filter(x=>x.kind==='SongPreparation').every(x=>x.state==='WaitingActual'&&x.dueAt===null),'Native saved opening causes actual-wait song rows without stale fallback');
 ok((await api(p,'notification-reservations',{},q)).status===405&&(await get(g,'notification-reservations',q)).status===403,'Native persisted-ledger POST prohibited and ordinary member denied');
 const before=ledger.version;await new Promise(r=>setTimeout(r,6000));ok((await current()).version===before,'Worker repeated identical reconciliation does not bump generation');
 const a=await p.context().newPage(),m=await g.context().newPage(),errors=[];a.on('pageerror',e=>errors.push(e.message));
 try{
  await a.goto('https://localhost:18443/events/'+id+'/notification-reservations');await a.waitForFunction(()=>document.getElementById('reservation-ledger')?.dataset.interactive==='true');
  ok((await a.locator('#reservation-policy').innerText()).includes('実送信')&&await a.locator('[data-reservation-id]').count()===3,'Actual ledger Blazor states dormant/no-send and shows persisted rows');
  await a.setViewportSize({width:390,height:844});ok(await a.locator('#reservation-refresh').isVisible()&&(await a.locator('#reservation-freshness').innerText()).includes('一致'),'Mobile ledger shows current reconciliation state and read-only refresh');
  let songs=(await get(p,'songs',q)).body;const removed=songs.songs[0].id;
  const change=await api(p,'songs',{rowVersion:songs.rowVersion,defaultPrepSeconds:songs.defaultPrepSeconds,songs:songs.songs.slice(1).map(s=>({id:s.id,title:s.title,artist:s.artist,performanceSeconds:s.performanceSeconds,prepSeconds:s.prepSeconds,definitionIds:s.parts.map(x=>x.definitionId)}))},q);ok(change.status===200,'Native formal song deletion saved without direct ledger manipulation');
  ledger=await current();const cancelled=ledger.reservations.find(x=>x.id==='song:'+removed);ok(cancelled?.state==='Cancelled'&&cancelled?.cancelReason==='PartRemoved'&&ledger.reservations.length===3&&ledger.version>before,'Native worker cancels removed logical row in place instead of deleting history');
  await a.locator('#reservation-refresh').click();await a.waitForFunction(id=>document.querySelector('[data-reservation-id="song:'+id+'"]')?.dataset.reason==='PartRemoved',removed);ok((await a.locator('[data-reservation-id="song:'+removed+'"]').innerText()).includes('取消済み'),'Real Blazor refresh displays persisted cancellation reason');
  if(process.env.SEM_PHASE_RESTART_COMMAND){require('child_process').execSync(process.env.SEM_PHASE_RESTART_COMMAND,{stdio:'inherit'});await require('./storage-ready.cjs')();await p.goto('https://localhost:18443/');await p.waitForSelector('#issue');await g.goto('https://localhost:18443/');await g.waitForSelector('#request');}
  const restarted=await current();ok(restarted.version===ledger.version&&restarted.reservations.find(x=>x.id==='song:'+removed).cancelReason==='PartRemoved','App restart preserves same ledger and cancellation generation');
  await m.goto('https://localhost:18443/events/'+id+'/notification-reservations');await m.waitForFunction(()=>document.getElementById('reservation-error')?.textContent.includes('管理権限'));ok(await m.locator('#reservation-ledger').count()===0,'Member real ledger UI discloses no persisted rows');
  await a.goto('https://localhost:18443/events/'+id+'/notification-plan');await a.locator('#reservation-ledger-link').waitFor();ok((await a.locator('#reservation-ledger-link').getAttribute('href')).endsWith('/notification-reservations'),'Manager plan view links to dormant persisted ledger');
  const mails=await(await fetch('http://127.0.0.1:18025')).json();ok(!mails.some(x=>x.body.includes('Native notification plan'))&&!JSON.stringify(restarted).includes('endpoint'),'Ledger worker/restart and edits create no related SMTP or subscription disclosure');
  ok(errors.length===0,'Reservation ledger actual Blazor has no runtime errors');
 }finally{await a.close();await m.close();}
};
