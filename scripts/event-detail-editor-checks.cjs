'use strict';
// Native-authenticated existing-event editing. UI relocation must not weaken the service boundary.
module.exports=async({p,g,ok,eventId})=>{
 const page=await p.context().newPage(),member=await g.context().newPage(),errors=[];
 for(const tab of [page,member])tab.on('pageerror',e=>errors.push(e.message));
 const edit=()=>require('./event-detail-editor-helper.cjs')(page,eventId);
 const save=async()=>{await page.locator('#event-core-save').click();await page.locator('#event-core-form').waitFor({state:'detached'});};
 const workspace=async()=>{await page.goto('https://localhost:18443/workspace');await page.locator('[data-workspace-interactive="true"]').waitFor();};
 const stored=()=>page.evaluate(async id=>(await(await fetch('/api/core/workspace')).json()).events.find(x=>x.id===id),eventId);
 try{
  await workspace();const row=page.locator(`[data-core-event="${eventId}"]`);await row.locator('summary').click();
  const link=row.getByRole('link',{name:'概要・管理者を編集',exact:true});
  ok(await link.getAttribute('href')===`/events/${eventId}#event-management`&&await row.getByRole('button',{name:'概要・管理者を編集',exact:true}).count()===0&&await page.locator('#event-core-form').count()===0,'Existing-event list offers only a detail link, never an inline editing form');
  await link.click();await page.locator('[data-event-interactive="true"]').waitFor();await page.locator('#event-edit').click();await page.locator('#event-core-save:enabled').waitFor();
  ok(new URL(page.url()).pathname===`/events/${eventId}`&&await page.locator('#event-management #event-core-form').count()===1,'List management link opens the existing-event editor on its detail page');
  await require('./event-editor-layout-checks.cjs')({page,ok});
  const initial=await stored();await page.locator('#event-title').fill('Unsaved detail-only draft');await page.locator('#event-core-cancel').click();await page.locator('#event-core-form').waitFor({state:'detached'});
  ok((await stored()).title===initial.title&&!(await page.locator('#event-overview-title').innerText()).includes('Unsaved detail-only draft'),'Detail cancel discards local draft without changing the persisted event or hero');
  await edit();await page.locator('#event-title').fill(initial.title+' updated');await save();
  ok((await page.locator('#event-overview-title').innerText()).includes(initial.title+' updated')&&(await stored()).title===initial.title+' updated','Detail save refreshes the hero immediately from the persisted event');
  await edit();await page.locator('#event-title').fill(initial.title);await save();
  await edit();await page.locator('#event-title').fill('Draft must not cross navigation');
  const internalDeclined=page.waitForEvent('dialog');page.once('dialog',d=>d.dismiss());await page.locator('[data-event-tabs-ready="true"] [data-event-tab="roster"]').click();await internalDeclined;await page.waitForTimeout(400);
  ok(new URL(page.url()).pathname===`/events/${eventId}`&&await page.locator('#event-title').inputValue()==='Draft must not cross navigation','Declining event-tab navigation retains the same detail editor draft');
  const declined=page.waitForEvent('dialog');page.once('dialog',d=>d.dismiss());await page.getByRole('link',{name:'イベント一覧',exact:true}).click();await declined;await page.waitForTimeout(400);
  ok(new URL(page.url()).pathname===`/events/${eventId}`&&await page.locator('#event-title').inputValue()==='Draft must not cross navigation','Declining navigation keeps the detail editor and its unsaved draft');
  page.once('dialog',d=>d.accept());await page.getByRole('link',{name:'イベント一覧',exact:true}).click();await page.waitForURL('**/workspace#core-events');await page.locator('[data-workspace-interactive="true"]').waitFor();
  ok(await page.locator('#event-core-form').count()===0&&(await stored()).title===initial.title,'Confirmed navigation discards only local draft and never persists it to the event list');
  await member.goto('https://localhost:18443/events/'+eventId);await member.locator('[data-event-interactive="true"]').waitFor();
  ok(await member.locator('#event-management,#event-edit,#event-core-form').count()===0,'Ordinary authorized viewer has no event management editor');
  const memberInfo=await member.evaluate(async()=>(await(await fetch('/api/core/workspace')).json()).profile);
  await edit();await page.locator('.admin-candidate-picker[data-admin-picker-interactive="true"]').waitFor();await page.locator('#event-manager-search').fill(memberInfo.displayName);
  await page.locator(`[data-admin-picker-candidate="${memberInfo.userId}"]`).click();await save();
  ok((await stored()).managerIds.includes(memberInfo.userId),'Detail manager picker persists the assigned event manager');
  await require('./event-detail-editor-helper.cjs')(member,eventId);
  ok((await member.locator('.admin-candidate-search label').innerText()).includes('表示名で')&&!(await member.locator('.admin-candidate-picker').innerText()).includes('member@example.test'),'Event-only manager can edit the assigned detail without receiving global account emails');
  // Keep that editor mounted, revoke its authority in another window, then attempt its stale save.
  await edit();await page.locator(`[data-admin-picker-remove="${memberInfo.userId}"]`).click();await save();
  await member.locator('#event-title').fill('Revoked manager must not save');await member.locator('#event-core-save').click();await member.locator('#event-core-form').waitFor({state:'detached'});
  ok((await member.locator('#event-editor-error').innerText()).includes('権限')&&(await stored()).title===initial.title&&!(await stored()).managerIds.includes(memberInfo.userId),'Mounted detail editor revalidates revoked management rights and refuses the stale write');
  ok(errors.length===0,'No client runtime errors in detail navigation, save, cancel and manager revocation');
 }finally{await member.close();await page.close();}
};
