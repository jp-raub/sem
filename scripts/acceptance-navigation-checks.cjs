'use strict';
module.exports=async({p,g,ok})=>{
 const page=await g.context().newPage();const errors=[];page.on('pageerror',e=>errors.push(e.message));
 try{
  await page.goto('https://localhost:18443/workspace');
  await page.locator('[data-workspace-interactive="true"]').waitFor();
  await page.locator('#event-sort-status').waitFor();
  const nameHeader=page.locator('.workspace-event-table th').nth(0),dateHeader=page.locator('.workspace-event-table th').nth(1);
  ok(await dateHeader.getAttribute('aria-sort')==='descending','Workspace starts with explicit date-descending state');
  // A changed aria-sort proves that this click reached hydrated Blazor, not only SSR HTML.
  await nameHeader.locator('button').click();await page.waitForFunction(()=>document.querySelector('.workspace-event-table th')?.getAttribute('aria-sort')==='ascending');
  ok((await page.locator('#event-sort-status').innerText()).includes('イベント名 ／ 昇順'),'Name ascending updates readable sort status');
  await nameHeader.locator('button').click();await page.waitForFunction(()=>document.querySelector('.workspace-event-table th')?.getAttribute('aria-sort')==='descending');
  ok(await dateHeader.getAttribute('aria-sort')==='none','Only the selected column announces an active order');
  await dateHeader.locator('button').click();await page.waitForFunction(()=>document.querySelectorAll('.workspace-event-table th')[1]?.getAttribute('aria-sort')==='ascending');
  const instants=await page.locator('.workspace-event-date time').evaluateAll(nodes=>nodes.map(e=>Date.parse(e.getAttribute('datetime'))));
  ok(instants.every((n,i)=>i===0||instants[i-1]<=n),'Date ascending orders real event instants without changing stored events');
  await require('./admin-search-audit-checks.cjs')({p,g,ok});
  await page.locator('#navigation-toggle').waitFor();await page.locator('#navigation-toggle').click();
  const profile=page.locator('#navigation-panel a[href="/profile/settings#profile-edit-heading"]');
  const account=page.locator('#navigation-panel a[href="/profile/settings#account-passkeys"]');
  ok(await profile.count()===1&&await account.count()===1,'Common menu routes profile and account to their real settings sections');
  await account.click();await page.locator('#profile-settings:not([hidden])').waitFor();
  await page.locator('#account-passkeys-list').waitFor();
  ok(new URL(page.url()).hash==='#account-passkeys'&&await page.locator('#account-passkeys').isVisible(),'Account navigation opens the authenticated passkey management section');
  ok(await page.locator('main').count()===1&&await page.locator('.profile-settings-sections a').count()===4,'Settings keep one main landmark and four distinct section links');
  await page.locator('.profile-settings-sections a[href="#profile-edit-heading"]').click();
  ok(new URL(page.url()).hash==='#profile-edit-heading'&&await page.locator('#profile-display-name').isVisible(),'Profile section remains available without switching to a prototype screen');
  const duplicates=await page.evaluate(()=>{const ids=[...document.querySelectorAll('[id]')].map(x=>x.id);return ids.filter((id,index)=>ids.indexOf(id)!==index);});
  ok(duplicates.length===0&&errors.length===0,'Unified settings navigation has no duplicate IDs or client errors');
 }finally{await page.close();}
};
