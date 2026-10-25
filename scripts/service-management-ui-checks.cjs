// 採用v17/v31のサービス専用画面。実ユーザー・実メール送信は使用しない。
module.exports=async({p,g,ok})=>{
 const origin='https://localhost:18443',a=await p.context().newPage(),m=await g.context().newPage(),errors=[];
 for(const page of[a,m])page.on('pageerror',e=>errors.push(e.message));
 const member=(await g.evaluate(async()=>await(await fetch('/api/core/workspace')).json())).profile;
 const account=()=>a.locator('#global-admin-accounts').getByRole('button',{name:member.displayName,exact:true});
 const open=async()=>{await account().click();await a.locator('.admin-account-dialog').waitFor();};
 const close=async()=>{await a.locator('.admin-account-dialog').getByRole('button',{name:'閉じる',exact:true}).click();await a.locator('.admin-account-dialog').waitFor({state:'hidden'});};
 const ready=async()=>{await a.goto(origin+'/service');await a.locator('#admin-roles-refresh:enabled').waitFor();};
 try{
  await ready();
  ok(await a.locator('#core-title').innerText()==='サービス管理','サービス管理は専用見出しで表示される');
  ok(await a.locator('#core-events,#core-account,#core-profile').count()===0,'専用画面にはイベント一覧と本人編集が混在しない');
  ok(await account().isVisible()&&await a.locator('#service-name').count()===0,'編集中でなくてもアカウント一覧を閲覧できる');
  ok(await a.locator('#navigation-service').getAttribute('href')==='/service','共通メニューはサービス専用画面を指す');
  ok(await a.locator('#service-invitations-link').getAttribute('href')==='/invitations','招待の操作は専用画面へ案内する');
  let writes=0,challenges=0,targetReads=0;
  const observer=req=>{if(req.method()==='POST'&&new URL(req.url()).pathname.startsWith('/api/'))writes++;if(/passkey-recovery-.*options/.test(req.url()))challenges++;if(req.url().includes('passkey-recovery-admin-context?'))targetReads++;};a.on('request',observer);
  await open();await a.locator('#member-profile[data-user="'+member.userId+'"]').waitFor();
  ok(await a.locator('.admin-account-dialog input[type=checkbox]').isDisabled(),'閲覧時の権限は読取専用');
  ok(await a.locator('.admin-profile-readonly input,.admin-profile-readonly textarea').count()===0,'管理者から他人のプロフィールを編集できない');
  ok(await a.locator('#admin-profile-page').getAttribute('href')==='/profiles/'+encodeURIComponent(member.userId),'プロフィールは選択した本人の既存ページへ進む');
  const recovery=await a.locator('#admin-account-recovery').getAttribute('href');
  ok(new URL(recovery,origin).searchParams.get('target')===member.userId,'復旧入口は明示選択した対象だけを引き継ぐ');
  await a.keyboard.press('Escape');await a.locator('.admin-account-dialog').waitFor({state:'hidden'});
  ok(await account().evaluate(e=>e===document.activeElement),'アカウント表示のEscapeは元の行へフォーカスを戻す');
  for(const[width,height]of[[320,568],[390,844],[768,1024],[1440,900],[844,390]])for(const mode of['light','dark']){
   await a.setViewportSize({width,height});await a.emulateMedia({colorScheme:mode});await open();
   const b=await a.locator('.admin-account-dialog').evaluate(e=>{const r=e.getBoundingClientRect();return{x:r.x,y:r.y,right:r.right,bottom:r.bottom,cx:r.x+r.width/2,cy:r.y+r.height/2,w:innerWidth,h:innerHeight,sw:e.scrollWidth,cw:e.clientWidth};});
   ok(b.x>=0&&b.y>=0&&b.right<=width+1&&b.bottom<=height+1&&b.sw<=b.cw+1,'アカウント表示が画面内に収まる '+width+'x'+height+' '+mode);
   ok(Math.abs(b.cx-width/2)<2&&Math.abs(b.cy-height/2)<2,'アカウント表示が中央にある '+width+'x'+height+' '+mode);
   await a.locator('.admin-account-dialog').focus();for(let i=0;i<12;i++)await a.keyboard.press('Tab');
   ok(await a.locator('.admin-account-dialog').evaluate(e=>e.contains(document.activeElement)),'キーボードのフォーカスを表示内に保つ '+width+' '+mode);
   await close();
  }
  await a.setViewportSize({width:1000,height:800});
  await a.goto(new URL(recovery,origin).href);await a.locator('#recovery-admin-panel:not([hidden])').waitFor();
  ok(await a.locator('#recovery-admin-target-id').inputValue()===member.userId,'復旧画面は選択対象を入力候補へ反映する');
  ok(await a.locator('#recovery-admin-target-summary').isHidden()&&writes===0&&challenges===0&&targetReads===0,'入口を開くだけでは対象照会・再認証・復旧・メール送信を実行しない');
  await a.goto(origin+'/service/passkey-recovery?target='+encodeURIComponent('invalid\nuser'));await a.locator('#recovery-admin-panel:not([hidden])').waitFor();
  ok(await a.locator('#recovery-admin-target-id').inputValue()==='','制御文字を含む対象引継ぎを拒否する');
  a.off('request',observer);
  await m.goto(origin+'/service');await m.locator('#service-access-denied').waitFor();
  ok(await m.locator('#global-admin-accounts,.admin-account-dialog,#service-edit').count()===0,'一般ユーザーへサービス編集と管理者専用一覧を表示しない');
  await ready();await a.locator('#service-edit').click();await a.locator('#service-name').waitFor();
  const name=await a.locator('#service-name').inputValue();await a.locator('#service-name').fill('未保存サービス名');
  await open();await a.locator('.admin-account-dialog input[type=checkbox]').check();await close();
  ok(await a.locator('#service-save').isDisabled(),'サービス保存と独立した権限下書きを混ぜない');
  await a.getByRole('button',{name:'サービス編集を取消',exact:true}).click();await a.locator('#admin-roles-refresh:enabled').waitFor();
  ok(await account().isVisible()&&await a.locator('#service-name').count()===0,'全体取消後も閲覧用アカウント一覧を取得できる');
  await a.locator('#service-edit').click();await a.locator('#service-name').waitFor();
  ok(await a.locator('#service-name').inputValue()===name,'全体取消はサービス名の下書きも破棄する');
  await open();ok(!await a.locator('.admin-account-dialog input[type=checkbox]').isChecked(),'全体取消は未保存の権限も破棄する');await close();
  await a.getByRole('button',{name:'サービス編集を取消',exact:true}).click();
  ok(errors.length===0,'サービス専用画面とプロフィール・復旧入口にクライアント例外がない');
 }finally{await a.close();await m.close();}
};
