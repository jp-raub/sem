const {chromium}=require('playwright');
(async()=>{const b=await chromium.launch({executablePath:process.env.CHROMIUM_PATH||undefined,args:['--no-sandbox']});let checks=0;
const ok=(x,m)=>{if(!x)throw Error(m);checks++};try{const p=await b.newPage(),errors=[];p.on('pageerror',e=>errors.push(e.message));const base=process.env.MOCK_URL||'http://127.0.0.1:8765';
await p.goto(base);ok(await p.locator('.demo-bar').count()===1,'デモ境界');ok(await p.locator('.sem-runtime-footer a').count()===1,'版リンク');
await p.locator('[data-demo="u1"]').click();await p.locator('a[href="#/events/e1"]').click();await p.locator('.board').waitFor();ok(await p.locator('.board').count()===1,'採用モックのイベント表');
await p.locator('.sem-runtime-footer a').click();await p.waitForURL('**/about');ok((await p.locator('h1').innerText())==='このアプリについて','人向けページ');ok((await p.locator('main').innerText()).includes('UIデモ版'),'模擬動作を明記');
for(const width of [320,390,768,1440]){await p.setViewportSize({width,height:844});ok(await p.evaluate(()=>document.documentElement.scrollWidth<=innerWidth),'狭幅の読みやすさ '+width)}
await p.locator('a[href="/api/version"]').focus();ok(await p.locator('a[href="/api/version"]').evaluate(e=>e===document.activeElement),'キーボード');
ok(errors.length===0,'runtime errors 0');console.log(JSON.stringify({passed:checks,errors}));}finally{await b.close()}})().catch(e=>{console.error(e);process.exit(1)});
