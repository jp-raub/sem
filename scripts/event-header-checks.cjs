'use strict';
module.exports=async({p,g,ok})=>{
 const page=await p.context().newPage();const failedResponses=[];page.on('response',r=>{if(r.status()>=400)failedResponses.push({status:r.status(),path:new URL(r.url()).pathname});});
 const png=Buffer.from('iVBORw0KGgoAAAANSUhEUgAAAFAAAAAoCAIAAADmAupWAAAATklEQVR4nO3PAQ3AIADAMMAN7pCA9Lv4k71VsM19z/iT9XXA2wzXGa4zXGe4znCd4TrDdYbrDNcZrjNcZ7jOcJ3hOsN1husM1xmuM1z3AF2YAWlbNButAAAAAElFTkSuQmCC','base64');
 const title='Event header browser fixture';
 const workspace=async()=>{await page.goto('https://localhost:18443/workspace');try{await page.locator('[data-workspace-interactive="true"]').waitFor();}catch(error){throw new Error('Header workspace hydration failed: '+JSON.stringify({responses:failedResponses,interactive:await page.locator('#core-title').getAttribute('data-workspace-interactive').catch(()=>null)})+'; '+error.message);}};
 const edit=async()=>{await workspace();const row=page.getByRole('row').filter({hasText:title});await row.locator('summary').click();await row.getByRole('button',{name:'概要・管理者を編集'}).click();await page.locator('#event-header-preview').waitFor();};
 const save=async()=>{await page.locator('#event-core-save').click();await page.waitForFunction(()=>!document.querySelector('#event-core-form'));};
 try{
  await workspace();await page.locator('#event-new').click();await page.locator('#event-title').fill(title);await page.locator('#event-starts').fill('2099-01-04T12:00');await page.locator('#event-phase').selectOption('View');
  await page.locator('#event-header-file').setInputFiles({name:'synthetic.png',mimeType:'image/png',buffer:png});await page.locator('#event-header-preview').waitFor();
  const preview=await page.locator('#event-header-preview').getAttribute('src');ok(preview.startsWith('data:image/png;base64,'),'Header draft displays only server-normalized PNG data');
  await save();const event=await page.evaluate(async title=>(await(await fetch('/api/core/workspace')).json()).events.find(e=>e.title===title),title);
  ok(!Object.hasOwn(event,'headerImageDataUrl'),'Workspace listing does not expose every event image blob');
  await page.goto('https://localhost:18443/events/'+event.id);await page.locator('#event-header-image').waitFor();ok(await page.locator('#event-header-image').getAttribute('src')===preview,'Saved event header matches normalized preview');
  ok(Math.round((await page.locator('#event-header-image').boundingBox()).height)===220,'Desktop header uses the approved mock height');await page.setViewportSize({width:390,height:844});ok(Math.round((await page.locator('#event-header-image').boundingBox()).height)===150&&await page.evaluate(()=>document.documentElement.scrollWidth<=innerWidth),'Mobile header uses 150px height without whole-page overflow');await page.setViewportSize({width:1280,height:720});
  const member=await g.context().newPage();try{await member.goto('https://localhost:18443/events/'+event.id);await member.locator('#event-header-image').waitFor();ok(await member.locator('#event-header-image').getAttribute('src')===preview,'Authorized ordinary participant can view published event header');}finally{await member.close();}
  await edit();ok(await page.locator('#event-header-preview').getAttribute('src')===preview,'Editor reloads the saved image independently of workspace list');
  await page.locator('#event-header-file').setInputFiles({name:'active.svg',mimeType:'image/svg+xml',buffer:Buffer.from('<svg onload="alert(1)"/>')});await page.getByText('PNG・JPEG・WebPを1MiB以内で選択してください。',{exact:true}).waitFor();ok(await page.locator('#event-header-preview').getAttribute('src')===preview,'Rejected active image keeps the existing draft and saved image intact');
  await page.locator('#event-title').fill(title+' edited');await save();
  await page.goto('https://localhost:18443/events/'+event.id);await page.locator('#event-header-image').waitFor();ok(await page.locator('#event-header-image').getAttribute('src')===preview,'Editing other event fields preserves the unchanged header');
  await workspace();const editedRow=page.getByRole('row').filter({hasText:title+' edited'});await editedRow.locator('summary').click();await editedRow.getByRole('button',{name:'概要・管理者を編集'}).click();await page.locator('#event-header-preview').waitFor();await page.locator('#event-header-remove').click();await page.locator('#event-header-preview').waitFor({state:'hidden'});ok(await page.locator('#event-header-preview').count()===0,'Header deletion is visible as a local draft before save');await save();
  await page.goto('https://localhost:18443/events/'+event.id);await page.locator('#event-overview-title').waitFor();ok(await page.locator('#event-header-image').count()===0,'Explicit deletion removes saved header without deleting event');
 }finally{await page.close();}
};
