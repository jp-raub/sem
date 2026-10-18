'use strict';
module.exports=async({p,g,ok})=>{
  const adminPage=await p.context().newPage();
  try{
  await adminPage.goto('https://localhost:18443/workspace');await adminPage.locator('[data-workspace-interactive="true"]').waitFor();
  const memberEmail=await g.evaluate(async()=> (await(await fetch('/api/core/workspace')).json()).email);
  let pickerWrites=0;const watch=r=>{if(r.method()==='POST'&&r.url().includes('/api/core/'))pickerWrites++;};adminPage.on('request',watch);
  await adminPage.locator('#event-new').click();await adminPage.locator('#event-manager-search').waitFor();
  const picker=adminPage.locator('.admin-candidate-picker[data-admin-picker-interactive="true"]');await picker.waitFor();await picker.locator('label').filter({hasText:'メール'}).waitFor();
  await adminPage.locator('#event-manager-search').fill(memberEmail.toUpperCase());
  await adminPage.waitForFunction(email=>{const candidates=[...document.querySelectorAll('[data-admin-picker-candidate]')];return candidates.length===1&&candidates[0].textContent.toLowerCase().includes(email.toLowerCase());},memberEmail);
  ok(await adminPage.locator('[data-admin-picker-candidate]').count()===1,'Global event manager search matches email incrementally');
  await adminPage.locator('#event-manager-search').press('ArrowDown');
  await adminPage.waitForFunction(()=>document.activeElement?.hasAttribute('data-admin-picker-candidate'));
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
  await adminPage.locator('#navigation-toggle').click();await adminPage.locator('#navigation-panel:not([hidden])').waitFor();
  ok(await adminPage.locator('#navigation-audit').isVisible(),'Administrator can discover audit directly in the common menu');
  await adminPage.locator('#navigation-audit').click();await adminPage.locator('#admin-audit-summary').waitFor();
  // A user click can commit the new document before its stylesheets finish.
  // Unlike goto's default, locator.click does not wait for the load event.
  await adminPage.waitForLoadState('load');
  ok(new URL(adminPage.url()).pathname==='/service/audit','Visible menu link opens the authorized audit page without typing its URL');
  ok(await adminPage.locator('.audit-table thead th').count()===5,'Audit has the five approved columns');
  ok(await adminPage.locator('[data-admin-audit]').count()<=100,'Audit UI remains bounded to latest hundred entries');
  const auditLayout=await adminPage.locator('.audit-scroll').evaluate(e=>({overflow:getComputedStyle(e).overflowY,height:e.clientHeight,maxHeight:getComputedStyle(e).maxHeight}));
  ok(auditLayout.overflow==='auto'&&auditLayout.height<=322,'Audit stays within approximately five-row scroll viewport: '+JSON.stringify(auditLayout));
  const memberPage=await g.context().newPage();try{
   await memberPage.goto('https://localhost:18443/workspace');await memberPage.locator('#navigation-controls:not([hidden])').waitFor();
   await memberPage.locator('#navigation-toggle').click();await memberPage.locator('#navigation-panel:not([hidden])').waitFor();
   ok(await memberPage.locator('#navigation-audit').isHidden(),'Ordinary member menu does not expose audit navigation');
   await memberPage.goto('https://localhost:18443/service/audit');await memberPage.locator('#admin-audit-error').filter({hasText:'全体管理者'}).waitFor();
   ok(await memberPage.locator('[data-admin-audit],#admin-audit-summary').count()===0,'Direct member navigation renders no private audit entries');
  }finally{await memberPage.close();}
  }finally{await adminPage.close();}
};
