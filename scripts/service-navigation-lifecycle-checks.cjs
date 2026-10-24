'use strict';
// Browser fixture uses no real accounts, cookies, state files or credential snapshots.
const fs=require('fs'),http=require('http'),assert=require('assert/strict');
const {chromium}=require('playwright');
(async()=>{
 const markup=fs.readFileSync('src/Sem.Web/Components/Layout/ServiceNavigation.razor','utf8');
 const server=http.createServer((req,res)=>{
  if(req.url==='/service-navigation.js'){res.setHeader('Content-Type','text/javascript');res.end(fs.readFileSync('src/Sem.Web/wwwroot/service-navigation.js'));return;}
  if(req.url==='/sem.css'){res.setHeader('Content-Type','text/css');res.end(fs.readFileSync('src/Sem.Web/wwwroot/sem.css'));return;}
  res.end('<!doctype html><html lang="ja"><head><meta charset="utf-8"><meta name="viewport" content="width=device-width,initial-scale=1"><link rel="stylesheet" href="/sem.css"></head><body>'+markup+'<main><h1>合成画面</h1><button id="outside">画面操作</button><div id="draft"></div></main><script type="module" src="/service-navigation.js"></script></body></html>');
 });
 await new Promise(r=>server.listen(0,'127.0.0.1',r));
 const browser=await chromium.launch({executablePath:process.env.SEM_CHROMIUM||undefined,args:['--no-sandbox']});let count=0;
 const check=(v,label)=>{assert(v,label);count++;console.log('PASS '+label);};
 try{
  const p=await browser.newPage({viewport:{width:390,height:844}});let response={userId:'synthetic',displayName:'合成ユーザー',serviceName:'合成サービス',description:'共通ナビゲーションの合成試験',isAdmin:true,unreadCount:3},status=200,hold=null;
  await p.route('**/api/core/navigation',async r=>{const code=status,body={...response};if(hold){const wait=hold;hold=null;await wait;}await r.fulfill({status:code,contentType:'application/json',body:JSON.stringify(body)});});
  await p.goto('http://127.0.0.1:'+server.address().port+'/');await p.waitForSelector('#navigation-controls:visible');
  check(await p.locator('#navigation-unread').innerText()==='3','Own unread count rendered');
  await p.locator('#navigation-toggle').click();await p.waitForSelector('#navigation-panel:visible');
  check(await p.locator('#navigation-service').isVisible(),'Current global admin can access service link');
  check(await p.locator('#navigation-audit').isVisible() && await p.locator('#navigation-audit').getAttribute('href') === '/service/audit','Global admin has a direct audit link in the common menu');
  check((await p.locator('#navigation-panel > a, #navigation-panel > button').allTextContents()).map(x=>x.trim()).join('|')==='サービス管理|イベント一覧|プロフィール管理|アカウント管理|通知管理|招待管理|ログアウト','Approved main menu order ends with logout');
  check((await p.locator('.navigation-auxiliary a').allTextContents()).join('|')==='表示設定|監査ログ','Appearance and audit stay discoverable in a separate auxiliary group');
  for(const width of [320,390,768,1440]){await p.setViewportSize({width,height:900});check(await p.locator('#navigation-panel').evaluate(e=>{const b=e.getBoundingClientRect();return b.left>=0&&b.right<=innerWidth&&e.scrollWidth<=e.clientWidth;}),'Menu remains within viewport '+width);}
  await p.setViewportSize({width:390,height:844});
  await p.keyboard.press('Escape');check(await p.locator('#navigation-toggle').evaluate(e=>e===document.activeElement),'Escape returns focus');
  response={...response,isAdmin:false,unreadCount:0};await p.locator('#navigation-toggle').click();await p.waitForSelector('#navigation-panel:visible');
  check(await p.locator('#navigation-service').isHidden(),'Permission removal applied before opening menu');
  check(await p.locator('#navigation-audit').isHidden(),'Permission removal also hides audit before opening menu');
  await p.locator('h1').click({position:{x:1,y:1}});check(await p.locator('#navigation-panel').isHidden(),'Outside click closes menu');
  status=403;await p.evaluate(()=>document.dispatchEvent(new Event('sem:notifications-changed')));await p.waitForSelector('#navigation-controls',{state:'hidden'});
  check(await p.locator('#navigation-user').innerText()===''&&await p.locator('#navigation-name').innerText()==='SEM','Denied current identity clears all private header state');
  check(await p.locator('#navigation-audit').isHidden(),'Denied identity never retains the private audit link');
  status=200;response={...response,isAdmin:true};await p.evaluate(()=>document.dispatchEvent(new Event('sem:notifications-changed')));await p.waitForSelector('#navigation-controls:visible');
  await p.evaluate(()=>{document.getElementById('draft').innerHTML='<input id="service-name" value="&lt;script&gt; draft"><input id="service-description" value="Description">';});await p.waitForFunction(()=>!document.getElementById('navigation-draft').hidden);
  check(await p.locator('#navigation-name').innerText()==='<script> draft'&&await p.locator('#navigation-name script').count()===0,'Preview renders literal text only');
  await p.evaluate(()=>document.getElementById('draft').replaceChildren());await p.waitForFunction(()=>document.getElementById('navigation-name').textContent==='合成サービス');check(await p.locator('#navigation-draft').isHidden(),'Draft removal restores saved header');
  let release;hold=new Promise(r=>release=r);await p.evaluate(()=>document.dispatchEvent(new Event('sem:notifications-changed')));await p.waitForTimeout(60);response={...response,userId:'replacement-member',displayName:'次の合成人物',isAdmin:false};
  await p.evaluate(()=>{const old=document.getElementById('service-navigation');const fresh=old.cloneNode(true);old.replaceWith(fresh);});await p.waitForSelector('#navigation-controls:visible');release();await p.waitForTimeout(100);check(await p.locator('#navigation-user').innerText()==='次の合成人物'&&await p.locator('#navigation-service').isHidden(),'Late old-admin response cannot overwrite replaced member header');
  check(await p.locator('#navigation-audit').isHidden(),'Stale admin response cannot re-expose audit for a replacement member');
  let logoutCalls=0;await p.route('**/api/csrf',r=>r.fulfill({contentType:'application/json',body:'{"token":"synthetic-csrf"}'}));
  await p.route('**/api/logout',async r=>{logoutCalls++;check(r.request().headers()['x-csrf-token']==='synthetic-csrf','Logout uses real endpoint CSRF convention');await r.abort();});
  await p.locator('#navigation-toggle').click();await p.waitForSelector('#navigation-panel:visible');await p.locator('#navigation-logout').click();await p.waitForFunction(()=>document.getElementById('navigation-status').textContent.includes('自動再送'));
  check(logoutCalls===1&&await p.locator('#navigation-controls').isHidden(),'Unknown logout result remains private-hidden and is not automatically resubmitted');
  await p.evaluate(()=>document.dispatchEvent(new Event('sem:notifications-changed')));await p.waitForTimeout(100);check(await p.locator('#navigation-controls').isHidden(),'Unknown logout state cannot be exposed by automatic refresh');
  console.log('Navigation lifecycle: '+count+' passed');
 }finally{await browser.close();await new Promise(r=>server.close(r));}
})().catch(e=>{console.error(e);process.exit(1)});
