// Fresh synthetic event. No shared fixture rows or production data are changed.
module.exports=async({p,ok})=>{
 const assert=require('node:assert/strict');
 const api=(action,data,query='')=>p.evaluate(async({action,data,query})=>{
  const{token}=await(await fetch('/api/csrf')).json();
  const r=await fetch('/api/core/'+action+query,{method:data===undefined?'GET':'POST',headers:data===undefined?{}:{'Content-Type':'application/json','X-CSRF-TOKEN':token},body:data===undefined?undefined:JSON.stringify(data)});
  return{status:r.status,body:await r.json()};
 },{action,data,query});
 const created=await api('event',{title:'Isolated edit guard event',description:'',location:'',scheduledDate:new Date(Date.now()+86400000).toISOString(),opensAt:null,announcementMessage:'',phaseKind:'Hidden',managerIds:[],rowVersion:0});
 assert.equal(created.status,200);const id=created.body.id,query='?id='+id;
 const w=await p.context().newPage();const errors=[];w.on('pageerror',e=>errors.push(e.message));
 const resultClose=async()=>{await w.getByRole('button',{name:'結果を確認して閉じる',exact:true}).click();};
 try {
  for(const [route,prefix,input,apply,key]of [
   ['phases','phase','phase-name','phase-draft-apply','name'],
   ['definitions','definition','definition-name','definition-apply','name'],
   ['questions','question','question-text','question-apply','text'],
   ['songs','song','song-title','song-apply','title'],
   ['service/part-presets','definition','definition-name','definition-apply','name']
  ]) {
   const isPreset=route==='service/part-presets';
   const action=isPreset?'definitions':route, q=isPreset?'':query;
   const url='https://localhost:18443/'+(isPreset?route:'events/'+id+'/'+route);
   const before=await api(action,undefined,q);
   await w.goto(url);await w.waitForSelector('#'+prefix+'-edit');await w.waitForSelector('[data-draft-interactive=true]',{state:'attached'});
   await w.locator('#'+prefix+'-edit').click();await w.waitForSelector('#'+prefix+'-add');
   await w.getByRole('link',{name:'業務基盤へ戻る',exact:true}).click();await w.waitForURL('**/workspace');
   ok(true,prefix+': unchanged edit permits internal navigation');
   await w.goto(url);await w.waitForSelector('#'+prefix+'-edit');await w.waitForSelector('[data-draft-interactive=true]',{state:'attached'});
   await w.locator('#'+prefix+'-edit').click();await w.locator('#'+prefix+'-add').click();
   await w.waitForFunction(()=>document.querySelector('[data-draft-dialog]')?.open);
   await w.keyboard.press('Tab');
   assert(await w.evaluate(()=>document.querySelector('[data-draft-dialog]').contains(document.activeElement)));
   const local='Local '+prefix+' draft';await w.locator('#'+input).fill(local);
   await w.keyboard.press('Escape');await w.waitForSelector('[data-draft-keep]');
   ok(await w.locator('[data-draft-dialog]').evaluate(e=>e.open),prefix+': Escape keeps native dialog open for discard confirmation');
   await w.locator('[data-draft-keep]').click();assert.equal(await w.locator('#'+input).inputValue(),local);
   await w.mouse.click(1,1);await w.waitForSelector('[data-draft-keep]');
   await w.locator('[data-draft-keep]').click();assert.equal(await w.locator('#'+input).inputValue(),local);
   ok(true,prefix+': backdrop also asks before discarding and retains input on continue');
   await w.locator('#'+prefix+'-close').click();await w.locator('[data-draft-discard]').click();
   await w.waitForSelector('[data-draft-dialog]',{state:'detached'});
   await w.waitForFunction(sel=>document.activeElement===document.querySelector(sel),'#'+prefix+'-add');
   assert.deepEqual((await api(action,undefined,q)).body,before.body);
   ok(true,prefix+': modal-only discard restores focus and writes nothing');
   await w.locator('#'+prefix+'-add').click();await w.locator('#'+input).fill(local);await w.locator('#'+apply).click();
   await w.waitForSelector('[data-draft-dialog]',{state:'detached'});
   await w.getByRole('link',{name:'業務基盤へ戻る',exact:true}).click();await w.waitForSelector('.draft-navigation-warning');
   assert.equal(w.url(),url);ok(true,prefix+': changed page blocks internal navigation without losing its draft');
   let warned=false;const handler=async d=>{assert.equal(d.type(),'beforeunload');warned=true;await d.dismiss();};w.once('dialog',handler);
   await w.reload({timeout:5000}).catch(e=>{if(!warned)throw e;});assert.equal(warned,true);
   ok((await w.locator('tbody').innerText()).includes(local),prefix+': dismissed browser reload preserves the draft');
   const row=w.locator('tbody button').filter({hasText:local}).first();await row.click();await w.locator('#'+input).fill('Discard modal only');
   await w.locator('#'+prefix+'-close').click();await w.locator('[data-draft-discard]').click();await w.waitForSelector('[data-draft-dialog]',{state:'detached'});
   ok((await w.locator('tbody').innerText()).includes(local)&&!(await w.locator('tbody').innerText()).includes('Discard modal only'),prefix+': cancelling reopened modal leaves previously applied page draft intact');
   await w.locator('#'+prefix+'-cancel').click();await w.waitForSelector('#'+prefix+'-edit');
   assert.deepEqual((await api(action,undefined,q)).body,before.body);

   await w.locator('#'+prefix+'-edit').click();await w.locator('#'+prefix+'-add').click();
   await w.locator('#'+input).fill('Saved '+prefix+' draft');await w.locator('#'+apply).click();await w.waitForSelector('[data-draft-dialog]',{state:'detached'});
   await w.locator('#'+prefix+'-save').dblclick();await w.waitForSelector('#'+prefix+'-edit');
   const saved=(await api(action,undefined,q)).body;
   const collection=action==='phases'?'phases':action==='definitions'?'definitions':action==='questions'?'questions':'songs';
   assert.equal(saved[collection].filter(x=>x[key]==='Saved '+prefix+' draft').length,1);
   await w.getByRole('link',{name:'業務基盤へ戻る',exact:true}).click();await w.waitForURL('**/workspace');
   ok(true,prefix+': final save persists once and releases navigation guard');
   await w.goto(url);await w.waitForSelector('#'+prefix+'-edit');await w.waitForSelector('[data-draft-interactive=true]',{state:'attached'});
   await w.locator('#'+prefix+'-edit').click();await w.locator('#'+prefix+'-add').click();await w.locator('#'+input).fill('Conflicting '+prefix+' draft');await w.locator('#'+apply).click();await w.waitForSelector('[data-draft-dialog]',{state:'detached'});
   const newer=action==='phases'?{rowVersion:saved.rowVersion,manualOverride:saved.manualOverride,activePhaseId:saved.phases.find(x=>x.isActive).id,customPhases:saved.phases.filter(x=>x.kind==='Custom')}:
    action==='definitions'?{rowVersion:saved.rowVersion,definitions:saved.definitions.map(({id,name,category})=>({id,name,category}))}:
    action==='questions'?{rowVersion:saved.rowVersion,questions:saved.questions.map(({id,text,type,required,disabled,public:pub,publicText})=>({id,text,type,required,disabled,public:pub,publicText}))}:
    {rowVersion:saved.rowVersion,defaultPrepSeconds:saved.defaultPrepSeconds,songs:saved.songs.map(x=>({id:x.id,title:x.title,artist:x.artist,performanceSeconds:x.performanceSeconds,prepSeconds:x.prepSeconds,definitionIds:x.parts.map(y=>y.definitionId)}))};
   const changed=await api(action,newer,q);assert.equal(changed.status,200);
   await w.locator('#'+prefix+'-save').click();await resultClose();
   ok((await w.locator('tbody').innerText()).includes('Conflicting '+prefix+' draft'),prefix+': conflict keeps local rows');
   await w.getByRole('link',{name:'業務基盤へ戻る',exact:true}).click();await w.waitForSelector('.draft-navigation-warning');assert.equal(w.url(),url);
   assert(!JSON.stringify((await api(action,undefined,q)).body).includes('Conflicting '+prefix+' draft'));
   await w.locator('#'+prefix+'-cancel').click();await w.waitForSelector('#'+prefix+'-edit');

   await w.getByRole('link',{name:'業務基盤へ戻る',exact:true}).click();await w.waitForURL('**/workspace');ok(true,prefix+': whole-page cancel clears the guard without saving');
   if(isPreset){
    const current=(await api(action,undefined,q)).body;
    const restored=await api(action,{rowVersion:current.rowVersion,definitions:before.body.definitions.map(({id,name,category})=>({id,name,category}))},q);
    assert.equal(restored.status,200);
    assert.deepEqual((await api(action,undefined,q)).body.definitions,before.body.definitions);
    ok(true,'Service presets restored before subsequent acceptance checks');
   }
  }
  ok(errors.length===0,'Edit guards and modal lifecycle have no browser runtime errors');
 }finally{await w.close();}
};
