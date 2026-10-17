'use strict';
module.exports=async({p,g,ok})=>{
 const page=await g.context().newPage();const errors=[];page.on('pageerror',e=>errors.push(e.message));
 try{
  await page.goto('https://localhost:18443/workspace');
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
