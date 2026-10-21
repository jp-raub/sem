'use strict';
// Real dedicated settings pages; callable without an event fixture.
module.exports=async({g,ok})=>{
 const page=await g.context().newPage();const errors=[];page.on('pageerror',e=>errors.push(e.message));
 try{
  await page.goto('https://localhost:18443/workspace');
  await page.locator('#navigation-toggle').waitFor();await page.locator('#navigation-toggle').click();
  const profile=page.locator('#navigation-panel a[href="/profile/settings#profile-edit-heading"]');
  const account=page.locator('#navigation-panel a[href="/settings/account#account-passkeys"]');
  ok(await profile.count()===1&&await account.count()===1,'Common menu routes profile and account to distinct real pages');
  await account.click();
  const accountReady=async()=>{
   await page.locator('#account-passkeys-list .account-passkey-item').first().waitFor().catch(async error=>{
    console.error('Settings initialization diagnostic:',JSON.stringify({path:new URL(page.url()).pathname,heading:await page.locator('h1').allTextContents(),accountRoots:await page.locator('#account-settings').count(),passkeyError:await page.locator('#account-passkeys-error').textContent().catch(()=>null),emailError:await page.locator('#profile-email-change-error').textContent().catch(()=>null)}));throw error;
   });
   await page.waitForFunction(()=>document.querySelector('#profile-email-change-begin')?.disabled===false);
  };
  const profileReady=async()=>{await page.locator('#profile-settings:not([hidden])').waitFor();await page.locator('#profile-display-name').waitFor();};
  await accountReady();
  ok(new URL(page.url()).pathname==='/settings/account'&&await page.locator('#account-passkeys').isVisible(),'Account navigation opens the authenticated dedicated account page');
  ok(await page.locator('main').count()===1&&await page.locator('.profile-settings-sections a').count()===2&&await page.locator('#profile-editor-form').count()===0,'Account page has one main landmark, two section links, and no profile editor');
  ok(await page.locator('#profile-email-change-address').isEnabled()&&await page.locator('#account-passkeys-add').isEnabled(),'Dedicated account modules independently authenticate and enable real controls');
  await page.locator('main a[href="/profile/settings#profile-edit-heading"]').click();await profileReady();
  ok(new URL(page.url()).hash==='#profile-edit-heading'&&await page.locator('#profile-display-name').isVisible(),'Profile cross-link opens the authenticated profile editor');
  ok(await page.locator('#account-passkeys').count()===0&&await page.locator('#profile-email-change').count()===0&&await page.locator('.profile-settings-sections a').count()===2,'Profile page retains profile and QR links without mounting account controls');
  const savedName=await page.locator('#profile-display-name').inputValue();
  await page.locator('#profile-display-name').fill('Unsubmitted layout draft');
  const fs=require('node:fs'),path=require('node:path');
  const output=path.join(process.env.SEM_E2E_RESULTS||'verification','settings-layout');fs.mkdirSync(output,{recursive:true});
  for(const scheme of ['light','dark'])for(const width of [390,768,1440]){
   await page.emulateMedia({colorScheme:scheme});await page.setViewportSize({width,height:900});
   ok(await page.evaluate(()=>document.documentElement.scrollWidth<=innerWidth+1),'Profile layout stays within viewport: '+scheme+' '+width);
   ok(await page.locator('#profile-settings .panel input').evaluateAll(nodes=>nodes.every(n=>n.getBoundingClientRect().right<=n.closest('.panel').getBoundingClientRect().right+1)),'Profile inputs remain inside their panel: '+scheme+' '+width);
   ok(await page.locator('#profile-display-name').inputValue()==='Unsubmitted layout draft','Profile unsaved draft survives width/theme changes: '+scheme+' '+width);
   await page.screenshot({path:path.join(output,'profile-'+scheme+'-'+width+'.png'),fullPage:true});
  }
  await page.locator('#profile-display-name').fill(savedName);
  await page.goBack();await accountReady();
  ok(new URL(page.url()).pathname==='/settings/account','Browser Back restores the account page with live modules');
  await page.goForward();await profileReady();
  ok(await page.locator('#profile-display-name').inputValue()===savedName,'Browser Forward restores saved profile values without submitting the discarded draft');
  for(const anchor of ['account-passkeys','profile-email-change']){
   await page.goto('https://localhost:18443/profile/settings?ignored=not-forwarded#'+anchor);await accountReady();
   ok(new URL(page.url()).pathname==='/settings/account'&&new URL(page.url()).hash==='#'+anchor&&!new URL(page.url()).search,'Legacy '+anchor+' link moves only its known anchor to the account page');
  }
  await page.goto('https://localhost:18443/profile/settings');await profileReady();
  await page.evaluate(()=>{location.hash='profile-email-change';});await accountReady();
  ok(new URL(page.url()).pathname==='/settings/account','Legacy anchor changed within the profile document also reaches account management');
  await page.locator('.profile-settings-sections a[href="/settings/account#profile-email-change"]').click();
  ok(new URL(page.url()).hash==='#profile-email-change'&&await page.locator('#profile-email-change-address').isVisible(),'Account email-change anchor remains reachable without hiding its form');
  await page.locator('#profile-email-change-address').fill('unsent@example.test');
  for(const scheme of ['light','dark'])for(const width of [390,768,1440]){
   await page.emulateMedia({colorScheme:scheme});await page.setViewportSize({width,height:900});
   ok(await page.evaluate(()=>document.documentElement.scrollWidth<=innerWidth+1),'Account layout stays within viewport: '+scheme+' '+width);
   ok(await page.locator('#account-settings .panel input').evaluateAll(nodes=>nodes.every(n=>n.getBoundingClientRect().right<=n.closest('.panel').getBoundingClientRect().right+1)),'Account inputs remain inside their panel: '+scheme+' '+width);
   ok(await page.locator('#profile-email-change-address').inputValue()==='unsent@example.test','Account unsent email survives width/theme changes: '+scheme+' '+width);
   await page.screenshot({path:path.join(output,'account-'+scheme+'-'+width+'.png'),fullPage:true});
  }
  await page.locator('#profile-email-change-address').fill('');
  await page.goto('https://localhost:18443/profile/settings#unknown-section');await profileReady();
  ok(new URL(page.url()).pathname==='/profile/settings','Unknown profile anchor does not redirect to an arbitrary destination');
  const anonymous=await page.context().browser().newContext({ignoreHTTPSErrors:true});
  try{
   const anon=await anonymous.newPage();const blockedTemplate=await anonymous.request.get('https://localhost:18443/Account/Login');ok(blockedTemplate.status()===404,'Built-in Identity template routes remain closed');await anon.goto('https://localhost:18443/settings/account');
   await anon.waitForFunction(()=>document.querySelector('#account-passkeys-error')?.textContent.length>0);
   ok(await anon.locator('#account-passkeys-add').isDisabled()&&await anon.locator('#profile-email-change-begin').isDisabled()&&await anon.locator('#account-passkeys-list .account-passkey-item').count()===0,'Unauthenticated account route exposes no keys or enabled mutations');
  }finally{await anonymous.close();}
  const duplicates=await page.evaluate(()=>{const ids=[...document.querySelectorAll('[id]')].map(x=>x.id);return ids.filter((id,index)=>ids.indexOf(id)!==index);});
  ok(duplicates.length===0&&errors.length===0,'Separated settings navigation has no duplicate IDs or client errors');
 }finally{await page.close();}
};
