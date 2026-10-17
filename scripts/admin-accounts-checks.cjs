'use strict';
module.exports=async({p,g,ok})=>{
 const origin='https://localhost:18443';
 const read=page=>page.evaluate(async()=>{const response=await fetch('/api/core/admin-accounts',{cache:'no-store'});return {status:response.status,body:await response.json()};});
 const before=await read(p); const member=(await g.evaluate(async()=>await(await fetch('/api/core/workspace')).json())).profile;
 ok(before.status===200&&before.body.accounts.some(a=>a.userId===member.userId),'Native administrator receives separate account DTO');
 ok((await read(g)).status===403,'Native member cannot read administrator account DTO');
 const page=await p.context().newPage();const errors=[];page.on('pageerror',e=>errors.push(e.message));
 const row=()=>page.locator('#global-admin-accounts tbody tr').filter({has:page.getByRole('button',{name:member.displayName,exact:true})});
 const openMember=async()=>{await row().getByRole('button',{name:member.displayName,exact:true}).click();await page.getByRole('dialog').waitFor();};
 const close=()=>page.getByRole('dialog').getByRole('button',{name:'閉じる',exact:true}).click();
 try{
  await page.goto(origin+'/workspace');await page.locator('#service-edit').waitFor();await page.waitForTimeout(2500);
  for(let n=0;n<8&&!await page.locator('#admin-roles-refresh').count();n++){if(await page.locator('#service-edit').count())await page.locator('#service-edit').click();await page.waitForTimeout(500);}
  await page.locator('#admin-roles-refresh').waitFor();
  ok(await page.locator('#global-admin-accounts thead th').allTextContents().then(x=>x.join(',')==='表示名,権限タグ,最終ログイン'),'Native account table keeps approved three-column layout');
  ok((await row().innerText()).includes('記録なし'),'Native account table does not invent last-login time');
  await openMember();await page.getByRole('dialog').getByRole('checkbox').check();await close();
  ok((await row().innerText()).includes('未保存')&&(await read(p)).body.accounts.find(a=>a.userId===member.userId).isAdmin===false,'Native role toggle remains an unsaved local draft');
  ok(await page.locator('#service-save').isDisabled(),'Native service settings save is blocked while a role draft exists');
  await page.locator('#admin-roles-cancel').click();
  await page.waitForFunction(()=>document.querySelector('#admin-roles-refresh')?.disabled===false);
  ok(!(await row().innerText()).includes('未保存')&&(await read(g)).status===403,'Native role cancel discards the draft without granting access');
  await openMember();await page.getByRole('dialog').getByRole('checkbox').check();await close();
  await page.locator('#admin-roles-save').click();await page.waitForFunction(()=>document.querySelector('#admin-roles-refresh')?.disabled===false);
  ok((await read(p)).body.accounts.find(a=>a.userId===member.userId).isAdmin===true&&(await read(g)).status===200,'Native role save grants DB-backed access to the existing member session');
  await openMember();await page.getByRole('dialog').getByRole('checkbox').uncheck();await close();
  await page.locator('#admin-roles-save').click();await page.waitForFunction(()=>document.querySelector('#admin-roles-refresh')?.disabled===false);
  ok((await read(p)).body.accounts.find(a=>a.userId===member.userId).isAdmin===false&&(await read(g)).status===403,'Native demotion revokes access without relying on cached role claims');
  const bootstrap=before.body.accounts.find(a=>a.bootstrap&&a.isAdmin);
  if(!bootstrap)throw Error('Synthetic bootstrap administrator missing');
  await page.locator('#global-admin-accounts').getByRole('button',{name:bootstrap.displayName,exact:true}).click();
  ok(await page.getByRole('dialog').getByRole('checkbox').isDisabled(),'Native Bootstrap administrator cannot be downgraded');
  await close();
  ok(errors.length===0,'Native account modal and save/cancel have no client runtime errors');
 } finally {await page.close();}
};
