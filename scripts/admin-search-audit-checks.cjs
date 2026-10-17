'use strict';
module.exports=async({p,g,ok})=>{
  const adminPage=await p.context().newPage();
  try{
  await adminPage.goto('https://localhost:18443/workspace');await adminPage.locator('[data-workspace-interactive="true"]').waitFor();
  const memberEmail=await g.evaluate(async()=> (await(await fetch('/api/core/workspace')).json()).email);
  let pickerWrites=0;const watch=r=>{if(r.method()==='POST'&&r.url().includes('/api/core/'))pickerWrites++;};adminPage.on('request',watch);
  await adminPage.locator('#event-new').click();await adminPage.locator('#event-manager-search').waitFor();
  const picker=adminPage.locator('.admin-candidate-picker');await picker.locator('label').filter({hasText:'メール'}).waitFor();
  await adminPage.locator('#event-manager-search').fill(memberEmail.toUpperCase());
  await adminPage.locator('[data-admin-picker-candidate]').waitFor();
  ok(await adminPage.locator('[data-admin-picker-candidate]').count()===1,'Global event manager search matches email incrementally');
  await adminPage.locator('#event-manager-search').press('ArrowDown');
  ok(await adminPage.locator('[data-admin-picker-candidate]').evaluate(e=>e===document.activeElement),'ArrowDown moves from search to first candidate');
  await adminPage.keyboard.press('Enter');await adminPage.locator('[data-admin-picker-remove]').waitFor();
  await adminPage.waitForFunction(()=>!document.querySelector('.admin-candidate-results'));
  ok(await adminPage.locator('[data-admin-picker-remove]').count()===1&&pickerWrites===0,'Keyboard selection changes only unsaved manager draft and closes candidates');
  const beforeBox=await adminPage.locator('#event-core-save').boundingBox();
  await adminPage.locator('#event-manager-search').click();
  await adminPage.locator('.admin-candidate-results').waitFor();
  const afterBox=await adminPage.locator('#event-core-save').boundingBox();
  ok(Math.abs(beforeBox.y-afterBox.y)<2,'Candidate overlay does not shift the form layout with the same selected draft');
  await adminPage.locator('#event-manager-search').press('Escape');await adminPage.waitForFunction(()=>!document.querySelector('.admin-candidate-results'));
  ok(await adminPage.locator('#event-manager-search').evaluate(e=>e===document.activeElement),'Escape hides candidates and retains search focus');
  await adminPage.locator('[data-admin-picker-remove]').click();await adminPage.waitForFunction(()=>!document.querySelector('[data-admin-picker-remove]'));
  await adminPage.locator('#event-manager-search').click();await adminPage.locator('.admin-candidate-results').waitFor();
  await adminPage.locator('#event-title').click();await adminPage.waitForFunction(()=>!document.querySelector('.admin-candidate-results'));
  ok(await adminPage.locator('#event-title').evaluate(e=>e===document.activeElement),'Outside interaction closes candidates without changing the form');
  await adminPage.locator('#event-core-form').getByRole('button',{name:'取消',exact:true}).click();adminPage.off('request',watch);
  const deniedAudit=await g.evaluate(async()=> (await fetch('/api/core/admin-audit')).status);
  ok(deniedAudit===403,'Ordinary member cannot obtain global audit over HTTP');
  await adminPage.goto('https://localhost:18443/service/audit');await adminPage.locator('#admin-audit-summary').waitFor();
  ok(await adminPage.locator('.audit-table thead th').count()===5,'Audit has the five approved columns');
  ok(await adminPage.locator('[data-admin-audit]').count()<=100,'Audit UI remains bounded to latest hundred entries');
  ok(await adminPage.locator('.audit-scroll').evaluate(e=>getComputedStyle(e).overflowY==='auto'&&e.clientHeight<=322),'Audit stays within approximately five-row scroll viewport');
  }finally{await adminPage.close();}
};
