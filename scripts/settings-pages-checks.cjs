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
  const profileReady=async()=>{await page.locator('#profile-settings:not([hidden])').waitFor();await page.locator('#profile-card-name').waitFor();};
  await accountReady();
  ok(new URL(page.url()).pathname==='/settings/account'&&await page.locator('#account-passkeys').isVisible(),'Account navigation opens the authenticated dedicated account page');
  ok(await page.locator('main').count()===1&&await page.locator('.profile-settings-sections a').count()===2&&await page.locator('#profile-editor-form').count()===0,'Account page has one main landmark, two section links, and no profile editor');
  ok(await page.locator('#profile-email-change-address').isEnabled()&&await page.locator('#account-passkeys-add').isEnabled(),'Dedicated account modules independently authenticate and enable real controls');
  await page.locator('main a[href="/profile/settings#profile-edit-heading"]').click();await profileReady();
  ok(new URL(page.url()).hash==='#profile-edit-heading'&&await page.locator('#profile-card-name').isVisible()&&await page.locator('#profile-display-name').isHidden(),'Profile cross-link opens the authenticated read card without automatically opening its editor');
  ok(await page.locator('#account-passkeys').count()===0&&await page.locator('#profile-email-change').count()===0&&await page.locator('.profile-settings-sections a').count()===2,'Profile page retains profile and QR links without mounting account controls');
  await page.waitForFunction(()=>document.getElementById('profile-history-status')?.textContent.includes('読み込みました'));
  ok(await page.locator('#profile-history').isVisible()&&await page.locator('.profile-qr').isVisible(),'本人の閲覧カードと参加登録履歴・QRが一画面にある');
  ok(await page.locator('#profile-qr-enlarge').isHidden(),'初回読込はQRを勝手に発行せず秘密画像を用意しない');
  let qrWrites=0;const qrRequest=request=>{if(request.method()==='POST'&&new URL(request.url()).pathname==='/api/core/profile-qr')qrWrites++;};page.on('request',qrRequest);
  const qrConfirm=dialog=>dialog.accept();page.on('dialog',qrConfirm);await page.locator('#profile-qr-issue').click();await page.locator('#profile-qr-image img').waitFor();page.off('dialog',qrConfirm);
  const qrSource=await page.locator('#profile-qr-image img').getAttribute('src');await page.locator('#profile-qr-enlarge').click();
  ok(await page.locator('#profile-qr-dialog').evaluate(e=>e.open)&&await page.locator('#profile-qr-large-image img').getAttribute('src')===qrSource&&qrWrites===1,'QR拡大は同じ発行済み画像を表示し再発行しない');
  await page.keyboard.press('Tab');ok(await page.locator('#profile-qr-close').evaluate(e=>e===document.activeElement),'QR拡大のTab操作を閉じるボタンへ保つ');await page.keyboard.press('Escape');
  ok(await page.locator('#profile-qr-enlarge').evaluate(e=>e===document.activeElement)&&await page.locator('#profile-qr-large-image img').count()===0&&qrWrites===1,'QR拡大を閉じて追加画像を消しフォーカスを戻す');page.off('request',qrRequest);
  const savedName=await page.locator('#profile-card-name').textContent();
  await page.locator('#profile-edit-open').click();await page.locator('#profile-edit-dialog[open]').waitFor();
  ok(await page.locator('#profile-display-name').evaluate(e=>e===document.activeElement),'明示編集でモーダルを開き表示名へフォーカスする');
  await page.locator('#profile-display-name').fill('取消する未保存の表示名');
  await page.locator('#profile-social-add').click();
  await page.keyboard.press('Escape');
  ok(await page.locator('#profile-display-name').isHidden()&&await page.locator('#profile-card-name').textContent()===savedName&&await page.locator('#profile-edit-open').evaluate(e=>e===document.activeElement),'Escapeは未保存のプロフィールを破棄し閲覧カードへ戻る');
  await page.locator('#profile-edit-open').click();
  ok(await page.locator('#profile-display-name').inputValue()===savedName&&await page.locator('#profile-social-links [data-social]').count()===0,'再表示は保存値へ戻り取消したSNS行を残さない');
  await page.locator('#profile-display-name').fill('Unsubmitted layout draft');
  const fs=require('node:fs'),path=require('node:path');
  const output=path.join(process.env.SEM_E2E_RESULTS||'verification','settings-layout');fs.mkdirSync(output,{recursive:true});
  for(const scheme of ['light','dark'])for(const width of [390,768,1440]){
   await page.emulateMedia({colorScheme:scheme});await page.setViewportSize({width,height:900});
   ok(await page.evaluate(()=>document.documentElement.scrollWidth<=innerWidth+1),'Profile layout stays within viewport: '+scheme+' '+width);
   ok(await page.locator('#profile-settings .panel input').evaluateAll(nodes=>nodes.every(n=>n.getBoundingClientRect().right<=(n.closest('dialog')||n.closest('.panel')).getBoundingClientRect().right+1)),'Profile inputs remain inside their panel: '+scheme+' '+width);
   ok(await page.locator('#profile-display-name').inputValue()==='Unsubmitted layout draft','Profile unsaved draft survives width/theme changes: '+scheme+' '+width);
   const box=await page.locator('#profile-edit-dialog').boundingBox();
   ok(box.x>=0&&box.y>=0&&box.x+box.width<=width+1&&Math.abs(box.x+box.width/2-width/2)<2,'プロフィール編集モーダルが中央・画面内にある '+scheme+' '+width);
   for(const key of['Tab','Shift+Tab'])for(let n=0;n<24;n++){await page.keyboard.press(key);ok(await page.locator('#profile-edit-dialog').evaluate(e=>e.contains(document.activeElement)),'プロフィール編集の各Tabでフォーカスを内側に保つ '+scheme+' '+width+' '+key+' '+n);}
   await page.screenshot({path:path.join(output,'profile-'+scheme+'-'+width+'.png'),fullPage:true});
  }
  await page.locator('#profile-image-file').setInputFiles({name:'synthetic-profile.png',mimeType:'image/png',buffer:Buffer.from('iVBORw0KGgoAAAANSUhEUgAAAAIAAAACCAIAAAD91JpzAAAAFklEQVR4nGMUqQhgYGBgYmBgYGBgAAAKPgDg63ilzwAAAABJRU5ErkJggg==','base64')});
  await page.locator('#profile-image-upload').click();await page.locator('#profile-image-preview:not([hidden])').waitFor();await page.waitForFunction(()=>document.getElementById('profile-save')?.disabled===false);
  ok(await page.locator('#profile-display-name').inputValue()==='Unsubmitted layout draft','独立した画像即時保存は編集中の名前下書きを消さない');
  await page.locator('#profile-edit-cancel').click();
  ok(await page.locator('#profile-display-name').inputValue()===savedName,'明示取消は保存前の値へ戻す');
  ok(await page.locator('#profile-card-avatar img').count()===1,'編集取消でも確定済み画像を巻き戻したように表示しない');
  await page.locator('#profile-edit-open').click();
  page.once('dialog',dialog=>dialog.accept());await page.locator('#profile-image-delete').click();await page.waitForFunction(()=>document.getElementById('profile-image-preview')?.hidden===true&&document.getElementById('profile-save')?.disabled===false);
  ok(await page.locator('#profile-card-avatar img').count()===0,'画像の明示削除も即時確定し閲覧カードへ反映する');
  const changedName='プロフィール保存 <img src=x>';
  await page.locator('#profile-display-name').fill(changedName);await page.locator('#profile-save').click();
  await page.waitForFunction(name=>document.getElementById('profile-card-name')?.textContent===name&&!document.getElementById('profile-edit-dialog')?.open,changedName);
  ok(await page.locator('#profile-card-name img').count()===0&&await page.locator('#profile-edit-open').evaluate(e=>e===document.activeElement),'本人APIで保存した値を閲覧カードへ安全に反映し起動ボタンへ戻る');
  await page.locator('#profile-edit-open').click();await page.locator('#profile-display-name').fill(savedName);await page.locator('#profile-save').click();
  await page.waitForFunction(name=>document.getElementById('profile-card-name')?.textContent===name&&!document.getElementById('profile-edit-dialog')?.open,savedName);
  let writes=0,releaseWrite;const writeGate=new Promise(resolve=>releaseWrite=resolve);
  const uncertain=async route=>{if(route.request().method()!=='POST')return route.continue();writes++;await writeGate;await route.fulfill({status:500,contentType:'application/json',body:'{}'});};
  await page.route('**/api/core/profile-editor?*',uncertain);
  try{
   await page.locator('#profile-edit-open').click();await page.locator('#profile-display-name').fill('結果不明の入力は破棄しない');await page.locator('#profile-save').click();
   await page.waitForFunction(()=>document.getElementById('profile-save')?.disabled);
   await page.keyboard.press('Escape');
   ok(await page.locator('#profile-edit-dialog').evaluate(e=>e.open)&&await page.locator('#profile-edit-cancel').isDisabled(),'通信中はEscapeと取消で未送信・結果待ちの入力を隠さない');
   releaseWrite();await page.locator('.operation-result-dialog[open]').waitFor();await page.locator('.operation-result-dialog button').click();
   ok(await page.locator('#profile-edit-cancel').isDisabled()&&await page.locator('#profile-save').isDisabled()&&await page.locator('#profile-dialog-refresh').isVisible()&&await page.locator('#profile-display-name').inputValue()==='結果不明の入力は破棄しない'&&writes===1,'結果不明は本文を保持して再保存・取消を止め再取得をモーダル内で操作できる');
   await page.locator('#profile-dialog-refresh').click();await page.waitForFunction(()=>document.getElementById('profile-save')?.disabled===false);
   ok(await page.locator('#profile-display-name').inputValue()===savedName&&writes===1,'明示再取得で確定値へ戻り書込みを自動再送しない');
   await page.locator('#profile-edit-cancel').click();
  }finally{releaseWrite();await page.unroute('**/api/core/profile-editor?*',uncertain);}
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
   ok(await page.locator('#account-settings .panel input').evaluateAll(nodes=>nodes.every(n=>n.getBoundingClientRect().right<=(n.closest('dialog')||n.closest('.panel')).getBoundingClientRect().right+1)),'Account inputs remain inside their panel: '+scheme+' '+width);
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
