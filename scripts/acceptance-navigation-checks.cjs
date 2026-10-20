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
  await require('./settings-pages-checks.cjs')({g,ok});
  ok(errors.length===0,'Workspace navigation checks leave no client errors');
 }finally{await page.close();}
};
