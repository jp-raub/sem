'use strict';
// ロールの合成DTOは表示比較だけ。サーバー権限や業務APIの許可へ流用しない。
module.exports=async function({page,context,reference,base,check,appearanceReady}){
 console.log('共通メニュー: 最終mockの項目順・幅・明暗・フォーカス');
 const real=await(await context.request.get(base+'/api/auth/session')).json();
 const writes=[],calls=[];const watch=r=>{const u=new URL(r.url());if(u.origin!==base)return;if(!['GET','HEAD','OPTIONS'].includes(r.method()))writes.push(r.method());if(u.pathname.startsWith('/api/ui/'))calls.push(u.pathname);};context.on('request',watch);
 const expected=['イベント一覧ページ','プロフィール管理','アカウント管理','通知管理','招待管理','ログアウト'];
 const style=p=>p.evaluate(()=>['#header-drawer','#header-drawer>a','#logout','#header-drawer .menu-utilities'].map(s=>{const e=document.querySelector(s),c=getComputedStyle(e);return[s,c.width,c.padding,c.borderRadius,c.fontFamily,c.fontSize,c.backgroundColor,c.color,c.display,c.gap];}));
 for(const admin of[false,true]){
  if(admin)await page.route('**/api/auth/session',r=>r.fulfill({status:200,contentType:'application/json',body:JSON.stringify({...real,isAdmin:true})}));
  await page.goto(base+'/#/');await page.reload();await page.waitForSelector('#signed-in-person');
  await reference.evaluate(admin=>{db.users=[{Id:'synthetic-user',Email:'synthetic@example.invalid',DisplayName:'合成管理者',IsAdmin:admin,Profile:{}}];current='synthetic-user';db.SiteTheme={};localStorage.removeItem('session-room-appearance-v2');location.hash='#/';render();},admin);
  const labels=admin?['サービス管理',...expected]:expected;
  check(JSON.stringify(await page.locator('#header-drawer > [data-management-menu]').allTextContents())===JSON.stringify(labels),'実認証の役割別メニュー順 '+admin);
  check(JSON.stringify(await page.locator('#header-drawer > [data-management-menu]').allTextContents())===JSON.stringify(await reference.locator('#header-drawer > [data-management-menu]').allTextContents()),'最終mockの主項目・文言と完全一致 '+admin);
  check(await page.locator('#header-drawer .menu-utilities #appearance').count()===1&&await page.locator('#persona,#reset,[data-demo]').count()===0,'表示設定は補助区画、デモロール切替を持ち込まない');
  for(const width of[320,390,768,1440])for(const mode of['light','dark']){
   for(const p of[page,reference]){await p.setViewportSize({width,height:900});await appearanceReady(p,mode);await p.locator('#menu').click();}
   await page.mouse.move(2,2);await reference.mouse.move(2,2);await page.waitForTimeout(30);
   const a=await style(page),b=await style(reference);if(JSON.stringify(a)!==JSON.stringify(b))console.error('メニュー比較 '+JSON.stringify({admin,width,mode,a,b}));check(JSON.stringify(a)===JSON.stringify(b),'最終mockのdrawer・リンク・ログアウト・補助区画スタイル一致');
   check(await page.evaluate(()=>{const d=document.querySelector('#header-drawer'),r=d.getBoundingClientRect();return !d.hidden&&r.left>=0&&r.right<=innerWidth&&r.height<=innerHeight*.75+1&&document.documentElement.scrollWidth<=innerWidth&&d.contains(document.activeElement); }),'狭幅で画面内・初期フォーカスはメニュー項目');
   await page.keyboard.press('Escape');await reference.keyboard.press('Escape');check(await page.evaluate(()=>document.querySelector('#header-drawer').hidden&&document.querySelector('#menu').getAttribute('aria-expanded')==='false'&&document.activeElement.id==='menu'),'Escapeで閉じて起点へフォーカス復帰');
  }
  await page.locator('#menu').click();await page.locator('#header-drawer a[href="#/"]').click();check(await page.locator('#header-drawer').isHidden(),'同じURLの項目でもメニューを閉じる');
  await page.locator('#menu').click();await page.mouse.click(2,100);check(await page.locator('#header-drawer').isHidden(),'外側操作でメニューを閉じる');
  if(admin){await page.evaluate(()=>location.hash='#/admin/users');await page.waitForSelector('#retry-invitation-administration');check((await page.locator('#app').textContent()).includes('現在権限')&&await page.locator('#new-event,table').count()===0,'合成表示ロールだけでは実DBの全体権限を取得しない');await page.unroute('**/api/auth/session');}
 }
 await page.goto(base+'/#/');await page.reload();await page.waitForSelector('#signed-in-person');check(await page.locator('#header-drawer a[href="#/admin/users"]').count()===0,'実本人へ復帰後に合成管理者表示を残さない');
 console.log('共通メニュー: 未接続経路・旧入口・不明URLを一覧へ偽装しない');
 await page.evaluate(()=>{window.__semNotificationCalls=0;if(window.Notification)Notification.requestPermission=()=>{window.__semNotificationCalls++;return Promise.reject(Error('許可要求禁止'));};});
 for(const [route,title]of[['#/not-a-page','ページを表示できません']]){
  calls.length=0;await page.evaluate(route=>location.hash=route,route);await page.waitForSelector('#unconnected-scope');check(await page.locator('#app h1').textContent()===title&&await page.locator('tbody,.stats,#signed-in-person').count()===0,'名前付き経路／不明URLの表示を一覧へ変換しない '+route);check(await page.locator('#app button,#app input,#app select').count()===0,'未接続の保存・許可・発行操作を実行可能にしない');check(calls.every(p=>['/api/ui/appearance','/api/ui/messages/unread'].includes(p)),'未接続経路で一覧や私有業務データを要求しない');
 }
 await page.evaluate(()=>location.hash='#/admin/users');await page.waitForFunction(()=>document.querySelector('#app h1')?.textContent==='サービス管理を表示できません');check(await page.locator('#app [role=alert]').textContent()==='全体管理者の権限が必要です。','非全体管理者の直接URLも管理表示へ進めない');
 check(await page.evaluate(()=>window.__semNotificationCalls===0)&&writes.length===0,'共通メニュー操作で通知許可要求・書込み0');context.off('request',watch);
 await page.locator('#app a[href="#/"]').click();await page.waitForSelector('#signed-in-person');
};
