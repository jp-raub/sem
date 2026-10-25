// 匿名の初回SSRと対話化を両方確認する。管理者fixtureの成功だけで代用しない。
module.exports=async function({p,ok}){
 const context=await p.context().browser().newContext({ignoreHTTPSErrors:true});
 const page=await context.newPage();const errors=[];page.on('pageerror',e=>errors.push(e.message));
 const origin=new URL(p.url()).origin;
 try{
  for(const [route,login]of [['/workspace','#workspace-auth-link'],['/service','#service-auth-link']]){
   const response=await page.goto(origin+route);
   ok(response?.status()===200,route+' 匿名の初回文書がHTTP200で表示される');
   await page.locator('#core-title[data-workspace-interactive="true"]').waitFor();
   ok((await page.locator('#core-error').innerText()).includes('再ログイン'),route+' 匿名は通常認証を案内する');
   ok(await page.locator(login).getAttribute('href')==='/auth?returnUrl='+encodeURIComponent(route),route+' ログイン後の戻り先を保持する');
   ok(await page.locator('#core-service,#global-admin-accounts,#core-events').count()===0,route+' 匿名へ管理・イベント情報を描画しない');
  }
  ok(errors.length===0,'匿名の業務基盤・サービス管理でブラウザー実行時エラーなし');
 }finally{await context.close();}
};
