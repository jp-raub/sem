'use strict';
// Real native-authenticated editor and persisted APIs. Synthetic, isolated fixture only.
module.exports=async({p,ok})=>{
 const page=await p.context().newPage();
 const title='Overview settings browser fixture';
 const api=(action,data,query='')=>page.evaluate(async({action,data,query})=>{
  const {token}=await(await fetch('/api/csrf')).json();
  const r=await fetch('/api/core/'+action+query,{method:data===undefined?'GET':'POST',headers:data===undefined?{}:{'Content-Type':'application/json','X-CSRF-TOKEN':token},body:data===undefined?undefined:JSON.stringify(data)});
  return {status:r.status,body:await r.json()};
 },{action,data,query});
 const requireOk=(r)=>{if(r.status!==200)throw new Error('Settings fixture HTTP '+r.status+' '+JSON.stringify(r.body));return r.body;};
 const workspace=async()=>{await page.goto('https://localhost:18443/workspace');await page.locator('[data-workspace-interactive="true"]').waitFor();};
 const saved=async()=>requireOk(await api('workspace')).events.find(e=>e.title===title);
 let id;
 const edit=async()=>require('./event-detail-editor-helper.cjs')(page,id);
 const save=async()=>{await page.locator('#event-core-save').click();await page.waitForFunction(()=>!document.querySelector('#event-core-form'));};
 const cancel=async()=>{await page.locator('#event-core-cancel').click();await page.waitForFunction(()=>!document.querySelector('#event-core-form'));};
 try{
  await workspace();await page.locator('#event-new').click();await page.waitForFunction(()=>!document.querySelector('#event-core-save')?.disabled);
  ok(await page.locator('#event-capacity').inputValue()===''&&await page.locator('#event-theme').inputValue()===''&&await page.locator('#event-theme-mode').inputValue()==='','New overview defaults to unlimited capacity and inherited event appearance');
  await page.locator('#event-title').fill(title);await page.locator('#event-starts').fill('2099-02-01T12:00');await page.locator('#event-phase').selectOption('View');
  await page.locator('#event-capacity').fill('3');await page.locator('#event-theme').selectOption('rose');await page.locator('#event-theme-mode').selectOption('2');await save();
  let event=await saved();id=event.id;
  ok(event.capacity===3&&event.appearance.themeId==='rose'&&event.appearance.mode===2,'Real overview save creates one capacity and event appearance setting');
  let appearance=requireOk(await api('theme-appearance',undefined,'?id='+id)).appearance;
  ok(appearance.event.themeId==='rose'&&appearance.event.mode===2&&requireOk(await api('participation',undefined,'?id='+id)).capacity===3,'Dedicated appearance and participation screens read the same persisted settings');
  await edit();ok(await page.locator('#event-capacity').inputValue()==='3'&&await page.locator('#event-theme').inputValue()==='rose'&&await page.locator('#event-theme-mode').inputValue()==='2','Reopening editor restores exact saved settings');
  await page.locator('#event-capacity').fill('9');await page.locator('#event-theme').selectOption('');await page.locator('#event-theme-mode').selectOption('');await cancel();
  let after=await saved();ok(after.capacity===3&&JSON.stringify(after.appearance)===JSON.stringify(event.appearance),'Cancel leaves capacity, theme, mode and independent version unchanged');
  await edit();
  const viewport=page.viewportSize();
  for(const width of [390,768,1440])for(const mode of ['light','dark']){
   await page.setViewportSize({width,height:950});await page.evaluate(mode=>document.documentElement.dataset.appearanceMode=mode,mode);
   ok(await page.locator('#event-settings-editor').evaluate(el=>{const b=el.getBoundingClientRect();return [...el.querySelectorAll('input,select')].every(c=>{const r=c.getBoundingClientRect();return r.width>60&&r.left>=b.left&&r.right<=b.right;})&&document.documentElement.scrollWidth<=innerWidth+1;}),`Real capacity and appearance controls fit ${width}px ${mode}`);
  }
  if(viewport)await page.setViewportSize(viewport);
  // A theme change has its own version and must reject the entire stale overview, not only theme fields.
  appearance=requireOk(await api('theme-appearance',undefined,'?id='+id)).appearance;
  const actor=appearance.actorId;
  requireOk(await api('theme-event',{themeId:'rose',mode:1,rowVersion:appearance.event.rowVersion},'?id='+id+'&expectedActor='+encodeURIComponent(actor)));
  await page.locator('#event-capacity').fill('8');await page.locator('#event-core-save').click();
  await page.waitForFunction(()=>document.querySelector('#event-editor-error')?.textContent.includes('ほかの画面で更新'));
  after=await saved();ok(after.capacity===3&&after.appearance.mode===1&&await page.locator('#event-capacity').inputValue()==='8','Dedicated appearance change rejects stale overview atomically and retains local draft');
  await page.getByRole('button',{name:'結果を確認して閉じる',exact:true}).click();
  await cancel();await edit();
  event=await saved();requireOk(await api('capacity',{capacity:4,rowVersion:event.rowVersion},'?id='+id));
  await page.locator('#event-theme-mode').selectOption('2');await page.locator('#event-core-save').click();
  await page.waitForFunction(()=>document.querySelector('#event-editor-error')?.textContent.includes('ほかの画面で更新'));
  after=await saved();ok(after.capacity===4&&after.appearance.mode===1&&await page.locator('#event-theme-mode').inputValue()==='2','Dedicated capacity change rejects stale event version without saving the draft appearance');
  await page.getByRole('button',{name:'結果を確認して閉じる',exact:true}).click();
  await cancel();await edit();
  await page.locator('#event-capacity').fill('');await page.locator('#event-theme').selectOption('');await page.locator('#event-theme-mode').selectOption('');await save();
  after=await saved();ok(after.capacity===null&&after.appearance.themeId===null&&after.appearance.mode===null,'Explicit blank overview settings restore unlimited capacity and site inheritance');
  await edit();await page.locator('#event-capacity').fill('6');await page.locator('#event-theme').selectOption('rose');await page.locator('#event-theme-mode').selectOption('0');await save();
  after=await saved();ok(after.capacity===6&&after.appearance.themeId==='rose'&&after.appearance.mode===0,'Explicit system mode stays distinct from inherited mode in populated retention fixture');
 }finally{await page.close();}
};
