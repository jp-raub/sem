'use strict';
// 合成データのみ。基準ファイルの自動更新は行わない。
const assert=require('node:assert/strict');
const fs=require('node:fs');
const path=require('node:path');
const {chromium}=require('playwright');
const baseline=JSON.parse(fs.readFileSync(path.join(__dirname,'../tests/fixtures/ui-route-baseline.json'),'utf8'));
const base=process.env.MOCK_URL||'http://127.0.0.1:8765';
// 公開サイトへデモ操作を誤送信しない。CI・ローカルの隔離ホストに限定。
assert(['127.0.0.1','localhost','[::1]'].includes(new URL(base).hostname),'隔離したローカルホストを指定してください');
(async()=>{
 const browser=await chromium.launch({executablePath:process.env.CHROMIUM_PATH||undefined,args:['--no-sandbox']});
 let routes=0,tabs=0,modals=0,checks=0;const errors=[];const network=[];
 try{
  assert.equal(baseline.cases.length,72);
  for(const actor of ['guest','u1','u2','u3']){
   const context=await browser.newContext();
   try{
    const page=await context.newPage();
    page.on('pageerror',error=>errors.push({actor,message:error.message}));
    page.on('request',request=>{if(!['GET','HEAD'].includes(request.method())||new URL(request.url()).origin!==new URL(base).origin)network.push({method:request.method(),url:request.url()});});
    await page.goto(base);
    await page.locator('.demo-bar').waitFor();
    if(actor!=='guest')await page.locator(`[data-demo="${actor}"]`).click();
    for(const item of baseline.cases.filter(x=>x.actor===actor)){
     await page.evaluate(route=>{location.hash=route;},item.route);
     await page.waitForFunction(route=>location.hash===route,item.route);
     // hashchange、全ラッパー、MutationObserver、requestAnimationFrameまで待つ。
     await page.evaluate(()=>new Promise(resolve=>requestAnimationFrame(()=>requestAnimationFrame(resolve))));
     assert.deepEqual(await page.locator('#app h1,#app h2').allTextContents(),item.headings,`${actor} ${item.route}: 見出し`);checks++;
     assert.deepEqual(await page.locator('[role=tab]').allTextContents(),item.tabs,`${actor} ${item.route}: タブ`);checks++;
     if(item.deniedText){assert((await page.locator('#app').innerText()).includes(item.deniedText),`${actor} ${item.route}: 拒否表示`);checks++;}
     assert.equal(await page.locator('.demo-bar').count(),1);checks++;
     // 同じURLにある全タブの到達性とARIA関連付け。業務保存はしない。
     const controls=await page.locator('[role=tab]').evaluateAll(nodes=>nodes.map(n=>n.id));
     for(const id of controls){
      const tab=page.locator(`[id="${id}"]`);await tab.click();
      assert.equal(await tab.getAttribute('aria-selected'),'true');
      const panel=page.locator(`[id="${await tab.getAttribute('aria-controls')}"]`);
      assert.equal(await panel.getAttribute('aria-hidden'),'false');
      assert.equal(await panel.getAttribute('aria-labelledby'),id);
      await panel.waitFor({state:'visible'});tabs++;checks+=4;
     }
     routes++;
    }
    for(const item of baseline.modals.filter(x=>x.actor===actor)){
     await page.evaluate(route=>{location.hash=route;},item.route);
     await page.locator(item.selector).click();
     await page.locator('#dialog').waitFor({state:'visible'});
     assert.equal(await page.locator('#dialog-title').innerText(),item.title);
     assert(await page.locator('#dialog').evaluate(node=>node.contains(document.activeElement)),'ダイアログ内のフォーカス');
     await page.locator('#dialog-close').click();
     await page.locator('#dialog').waitFor({state:'hidden'});
     modals++;checks+=3;
    }
   }finally{await context.close();}
  }
  assert.deepEqual(errors,[],'クライアント例外');assert.deepEqual(network,[],'書込み・外部通信なし');checks+=2;
  console.log(JSON.stringify({mockCommit:baseline.mockCommit,routes,tabs,modals,checks,errors,unexpectedRequests:network,scope:'合成データの画面基準。実認証・DB・保存・実機能の検証ではない。'}));
 }finally{await browser.close();}
})().catch(error=>{console.error(error);process.exit(1);});
