// 既存の手動受付fixtureで実行する。保存前のUIだけを調べ、元の確定・取消回帰へ戻す。
module.exports=async({a,ok})=>{
 await a.waitForFunction(()=>document.getElementById('reception-dialog')?.open);
 const columns=await a.locator('#reception-roster thead th').allTextContents();
 ok(JSON.stringify(columns)===JSON.stringify(['表示名／読み','申込状況','受付','直近QR読取り','受付完了の記録','操作']),'受付表は採用モックの6列で読取と受付完了を分離する');
 await a.locator('#reception-note').fill('未送信の受付メモ');
 await a.keyboard.press('Escape');await a.locator('#reception-close-confirm').waitFor();ok(await a.locator('#reception-note').inputValue()==='未送信の受付メモ','入力欄を離れずEscapeしても最新のメモを破棄しない');await a.locator('#reception-close-keep').click();
 for(const scheme of ['light','dark'])for(const [width,height]of [[320,568],[390,844],[768,1024],[1440,900],[844,390]]){
  await a.emulateMedia({colorScheme:scheme});await a.setViewportSize({width,height});
  const box=await a.locator('#reception-dialog').evaluate(d=>{const r=d.getBoundingClientRect();return{x:r.x,y:r.y,w:r.width,h:r.height,viewW:innerWidth,viewH:innerHeight,overflow:document.documentElement.scrollWidth>innerWidth+1};});
  ok(Math.abs(box.x+box.w/2-width/2)<2&&Math.abs(box.y+box.h/2-height/2)<2&&box.w<=width-30&&box.h<=height-30&&!box.overflow,'受付確認は画面中央・画面内で本文は横にはみ出さない '+scheme+' '+width+'x'+height);
  ok(await a.locator('#reception-note').inputValue()==='未送信の受付メモ','画面幅と配色が変わっても未送信メモを保持 '+scheme+' '+width);
  ok(await a.locator('#reception-dialog-help').evaluate(el=>getComputedStyle(el).borderLeftWidth==='3px'&&getComputedStyle(el).backgroundColor!==getComputedStyle(document.getElementById('reception-dialog')).backgroundColor),'受付案内は実在するテーマ色で区別される '+scheme+' '+width);
  if([390,1440].includes(width))await a.screenshot({path:require('path').join(process.env.SEM_E2E_RESULTS||'verification',`reception-dialog-${scheme}-${width}.png`)});
 }
 await a.setViewportSize({width:390,height:844});
 await a.locator('#reception-close').focus();await a.keyboard.press('Tab');
 ok(await a.locator('[data-dialog-close]').evaluate(el=>document.activeElement===el),'受付確認のTabは最後から先頭へ戻る');
 await a.keyboard.press('Shift+Tab');ok(await a.locator('#reception-close').evaluate(el=>document.activeElement===el),'受付確認の逆Tabは先頭から最後へ戻る');
 await a.keyboard.press('Escape');await a.locator('#reception-close-confirm').waitFor();
 ok(await a.locator('#reception-dialog').evaluate(d=>d.open)&&await a.locator('#reception-note').inputValue()==='未送信の受付メモ','Escapeで未送信のメモを黙って破棄せず取消を確認する');
 await a.locator('#reception-close-keep').click();ok(await a.locator('#reception-note').inputValue()==='未送信の受付メモ','取消確認から入力に戻ってもメモを保持');
 await a.mouse.click(2,2);ok(await a.locator('#reception-dialog').evaluate(d=>d.open),'背景タップでは本人確認や入力を破棄しない');
 await a.locator('#reception-close').click();await a.locator('#reception-close-discard').click();await a.waitForFunction(()=>!document.getElementById('reception-dialog').open);
 ok(await a.locator('[data-reception-select]').evaluate(el=>document.activeElement===el),'未送信入力を破棄して閉じると元の対象者ボタンへフォーカスを戻す');
 await a.evaluate(()=>{window.__receptionUnexpectedOpen=0;window.__receptionOpenObserver=new MutationObserver(records=>{window.__receptionUnexpectedOpen+=records.filter(r=>r.attributeName==='open'&&r.oldValue===null).length;});window.__receptionOpenObserver.observe(document.getElementById('reception-dialog'),{attributes:true,attributeOldValue:true,attributeFilter:['open']});});
 await a.locator('#reception-search-submit').click();await a.locator('[data-reception-select]').waitFor();
 await a.waitForFunction(()=>!document.getElementById('reception-search-submit').disabled);
 const unexpected=await a.evaluate(()=>{window.__receptionOpenObserver.disconnect();return window.__receptionUnexpectedOpen;});
 ok(unexpected===0,'閉じた後の一覧再検索で本人確認モーダルを勝手に開かない');
 await a.locator('[data-reception-select]').click();await a.locator('#reception-target').waitFor();await a.waitForFunction(()=>document.getElementById('reception-dialog').open);
 ok(await a.locator('#reception-note').inputValue()===''&&!await a.locator('#reception-done').isChecked(),'再表示は破棄前の下書きや確認済み状態を復活させない');
};
