'use strict';
// 実製品ホストを合成DB／鍵とlocalhost HTTPSプロキシで起動する。実volume・実認証器は使わない。
const assert=require('node:assert/strict'),fs=require('node:fs'),os=require('node:os'),path=require('node:path');
const {spawn,execFileSync}=require('node:child_process'),http=require('node:http'),https=require('node:https');
const {chromium}=require('playwright');
const web=process.env.SEM_PRODUCT_DLL,fixture=process.env.SEM_AUTH_FIXTURE_DLL;
assert(web&&fixture&&path.basename(web)==='Sem.Web.dll'&&path.basename(fixture)==='Sem.Authentication.Fixture.dll');
const root=fs.mkdtempSync(path.join(os.tmpdir(),'sem-product-auth-')),state=path.join(root,'state'),backup=path.join(root,'backups');fs.mkdirSync(backup,{mode:0o700});
const children=new Set();let proxy,browser,checks=0;const errors=[];const dotnet=process.env.DOTNET_COMMAND||'dotnet';
const base='https://localhost:19876';
const check=(value,label)=>{assert(value,label);checks++;};
function start(dll,env){const p=spawn(dotnet,[dll],{cwd:path.dirname(dll),stdio:'ignore',env:{...process.env,...env}});children.add(p);return p;}
async function stop(p){if(!p||p.exitCode!==null)return;await new Promise(resolve=>{const timer=setTimeout(()=>p.kill('SIGKILL'),2000);p.once('exit',()=>{clearTimeout(timer);resolve();});p.kill('SIGTERM');});children.delete(p);}
async function ready(url,child){for(let n=0;n<100;n++){assert(child.exitCode===null,'合成ホストが起動できません');const ok=await new Promise(resolve=>{const q=(url.startsWith('https:')?https:http).get(url,{rejectUnauthorized:false,timeout:400},r=>{r.resume();resolve(r.statusCode===200);});q.on('timeout',()=>q.destroy());q.on('error',()=>resolve(false));});if(ok)return;await new Promise(r=>setTimeout(r,200));}throw Error('合成ホスト起動上限');}
(async()=>{try{
 const enrollment=start(fixture,{SEM_AUTH_FIXTURE_DIRECTORY:state,SEM_AUTH_FIXTURE_PORT:'18766',SEM_AUTH_FIXTURE_RESUME:'0'});await ready('https://localhost:18766',enrollment);
 browser=await chromium.launch({executablePath:process.env.CHROMIUM_PATH||undefined,args:['--no-sandbox']});const context=await browser.newContext({ignoreHTTPSErrors:true});const page=await context.newPage();page.on('pageerror',e=>errors.push(e.message));
 const external=[];await context.route('**/*',route=>{const u=new URL(route.request().url());if(u.hostname!=='localhost'){external.push(u.hostname);return route.abort();}return route.continue();});
 await page.goto('https://localhost:18766');const cdp=await context.newCDPSession(page);await cdp.send('WebAuthn.enable');await cdp.send('WebAuthn.addVirtualAuthenticator',{options:{protocol:'ctap2',transport:'internal',hasResidentKey:true,hasUserVerification:true,isUserVerified:true,automaticPresenceSimulation:true}});
 check(await page.evaluate(async()=>{const token=(await(await fetch('/api/auth/csrf')).json()).token;const headers={'Content-Type':'application/json','X-CSRF-TOKEN':token};const options=await(await fetch('/fixture/create-options',{method:'POST',headers,body:'{}'})).json();const credential=await navigator.credentials.create({publicKey:PublicKeyCredential.parseCreationOptionsFromJSON(options)});return(await fetch('/fixture/create',{method:'POST',headers,body:JSON.stringify(credential.toJSON())})).ok;}),'合成環境だけで標準登録');
 await stop(enrollment);await context.clearCookies();
 // TLS秘密鍵は合成プロキシ専用、所有者だけの一時領域に置きfinallyで破棄。WebAuthn秘密鍵はCDPメモリのみ。
 const cert=path.join(root,'proxy.crt'),key=path.join(root,'proxy.key');execFileSync('openssl',['req','-x509','-newkey','rsa:2048','-nodes','-keyout',key,'-out',cert,'-days','1','-subj','/CN=localhost','-addext','subjectAltName=DNS:localhost'],{stdio:'ignore'});fs.chmodSync(key,0o600);
 proxy=https.createServer({cert:fs.readFileSync(cert),key:fs.readFileSync(key)},(req,res)=>{const q=http.request({hostname:'127.0.0.1',port:19877,path:req.url,method:req.method,headers:{...req.headers,'x-forwarded-proto':'https','x-forwarded-for':'127.0.0.1'}},up=>{res.writeHead(up.statusCode,up.headers);up.pipe(res);});q.on('error',()=>{res.writeHead(503);res.end();});req.pipe(q);});await new Promise(resolve=>proxy.listen(19876,'127.0.0.1',resolve));
 const env={SEM_APPLICATION_MODE:'authenticated',SEM_AUTH_ORIGIN:base,SEM_AUTH_DATABASE:path.join(state,'synthetic.db'),SEM_AUTH_KEYS_DIRECTORY:path.join(state,'keys'),SEM_AUTH_BACKUP_DIRECTORY:backup,SEM_AUTH_TRUSTED_PROXIES:'127.0.0.1',ASPNETCORE_URLS:'http://127.0.0.1:19877'};
 let product=start(web,env);await ready(base+'/health/live',product);
 await page.goto(base+'/#/login');await page.waitForSelector('#login-form');
 const demo=start(web,{SEM_APPLICATION_MODE:'demo',ASPNETCORE_URLS:'http://127.0.0.1:19878'});await ready('http://localhost:19878/health/live',demo);
 const comparison=await browser.newContext();const reference=await comparison.newPage();await reference.goto('http://localhost:19878/#/login');await reference.waitForSelector('.compact-caption');
 const geometry=async p=>p.evaluate(()=>{const input=document.querySelector('#login-form input[name=email]'),panel=document.querySelector('.auth.panel'),button=document.querySelector('#login-form button.primary'),hint=document.querySelector('#login-form .help-button');const a=getComputedStyle(input),b=getComputedStyle(panel),c=getComputedStyle(button);return {panelWidth:panel.getBoundingClientRect().width,inputWidth:input.getBoundingClientRect().width,inputHeight:input.getBoundingClientRect().height,padding:b.padding,borderRadius:b.borderRadius,font:a.fontFamily,fontSize:a.fontSize,buttonPadding:c.padding,buttonRadius:c.borderRadius,panelBackground:b.backgroundColor,inputBackground:a.backgroundColor,inputColor:a.color,buttonBackground:c.backgroundColor,buttonColor:c.color,hintWidth:hint.getBoundingClientRect().width};});
 for(const width of[320,390,768,1440])for(const colorScheme of['light','dark']){await reference.setViewportSize({width,height:900});await page.setViewportSize({width,height:900});await reference.emulateMedia({colorScheme});await page.emulateMedia({colorScheme});await page.waitForTimeout(50);check(JSON.stringify(await geometry(reference))===JSON.stringify(await geometry(page)),'最終mockとlogin panel／入力／ボタン／ヘルプの寸法・字体・明暗配色一致');}
 await comparison.close();await stop(demo);
 await page.locator('#login-form .help-button').click();await page.waitForSelector('.help-float');check((await page.locator('.help-float').textContent()).includes('公開プロフィールには表示しません'),'固定の外部日本語JSONから入力ヘルプを表示');await page.keyboard.press('Escape');
 check(await page.locator('.auth.panel h1').textContent()==='また、一緒に音楽を。','採用login見出し・section');
 check(await page.locator('[data-demo],#persona,#reset,input[type=password]').count()===0,'demoロール／reset／password入口なし');
 check(await page.locator('script[src]').evaluateAll(s=>s.map(x=>new URL(x.src).pathname).join(','))==='/assets/authentication.js','demoスクリプトを実認証画面で実行しない');
 check(await page.locator('input[name=email]').inputValue()==='','実メールの初期値や端末保存なし');
 for(const width of[320,390,768,1440])for(const colorScheme of['light','dark']){await page.setViewportSize({width,height:900});await page.emulateMedia({colorScheme});check(await page.evaluate(()=>document.documentElement.scrollWidth<=innerWidth),'狭幅／明暗の横はみ出しなし');}
 await page.locator('#passkey-unavailable').click();check(await page.locator('#dialog').evaluate(e=>e.open),'採用dialogのヘルプ');await page.locator('#dialog-close').click();
 await page.locator('input[name=email]').fill('synthetic@example.invalid');await page.locator('#login-form button.primary').click();await page.waitForSelector('#signed-in-person');check((await page.locator('#signed-in-person').textContent()).includes('合成管理者'),'実製品DOMから標準署名ログイン');
 check(await page.locator('#app').textContent().then(t=>t.includes('まだ接続していません')),'未接続業務を成功表示しない');
 check(await page.evaluate(()=>localStorage.length===0&&sessionStorage.length===0),'認証・メール・デモ状態をWebStorageへ保存しない');
 const forbidden=await context.request.get(base+'/app.js');check(forbidden.status()===404,'demo scriptの直読拒否');check((await context.request.get(base+'/profile.html')).status()===404,'別HTMLのdemo入口拒否');check((await context.request.post(base+'/fixture/create')).status()===404,'製品に登録fixtureなし');
 check((await context.request.get(base+'/api/version')).status()===200,'版API維持');const health=await context.request.get(base+'/health/ready?probe=0123456789abcdef0123456789abcdef');check(health.ok()&&(await health.json()).probe==='0123456789abcdef0123456789abcdef','新鮮なreadiness維持');
 const before=await context.cookies();await stop(product);product=start(web,env);await ready(base+'/health/live',product);await page.reload();await page.waitForSelector('#signed-in-person');check(true,'実製品を再起動しDB／鍵／Cookieを維持');
 const complete=fs.readdirSync(backup).filter(n=>fs.existsSync(path.join(backup,n,'verified.json')));check(complete.length===2,'実起動ごとにDB＋鍵の検証済み保全');
 await page.locator('#menu').click();await page.locator('#logout').click();await page.waitForSelector('#login-form');check(true,'製品DOMのログアウト');
 const stale=await context.request.get(base+'/api/auth/session',{headers:{Cookie:before.map(c=>c.name+'='+c.value).join('; ')}});check(stale.status()===401,'ログアウト前Cookieの再送拒否');
 // 通信不良はログイン成功へ変換しない。
 await page.route('**/api/auth/login-options',route=>route.abort());await page.locator('input[name=email]').fill('synthetic@example.invalid');await page.locator('#login-form button.primary').click();await page.waitForFunction(()=>document.querySelector('#auth-status').textContent.includes('ログインできませんでした'));check(await page.locator('#signed-in-person').count()===0,'通信失敗で偽ログインしない');await page.unroute('**/api/auth/login-options');
 await page.locator('input[name=email]').fill('missing@example.invalid');await page.locator('#login-form button.primary').click();await page.waitForFunction(()=>!document.querySelector('#login-form button.primary').disabled);check(await page.locator('#auth-status').textContent()=== 'ログインできませんでした。ページを再読み込みしてお試しください。','存在しないアカウントは固定エラーで残る');
 let requested=0;await page.route('**/api/auth/login-options',async route=>{requested++;await new Promise(r=>setTimeout(r,150));await route.fulfill({status:400,contentType:'application/json',body:'{}'});});await page.locator('input[name=email]').fill('synthetic@example.invalid');await page.locator('#login-form button.primary').click();check(await page.locator('#login-form button.primary').isDisabled(),'処理中は重複送信を抑止');await page.waitForFunction(()=>!document.querySelector('#login-form button.primary').disabled);check(requested===1,'1操作1試行');await page.unroute('**/api/auth/login-options');
 await page.evaluate(()=>{navigator.credentials.get=()=>Promise.reject(new DOMException('synthetic cancellation','NotAllowedError'));});await page.locator('#login-form button.primary').click();await page.waitForFunction(()=>document.querySelector('#auth-status').textContent.includes('中断'));check(await page.locator('#signed-in-person').count()===0,'認証器での中断は偽ログインしない');
 // 対応APIなしの端末は、認証通信を起こさず案内する。
 const unsupported=await context.newPage();await unsupported.addInitScript(()=>Object.defineProperty(window,'PublicKeyCredential',{value:undefined}));await unsupported.goto(base+'/#/login');await unsupported.waitForSelector('#login-form');check(await unsupported.locator('#login-form button.primary').isDisabled(),'非対応環境の案内と操作抑止');await unsupported.close();
 check(errors.length===0,'クライアント例外0');check(external.length===0,'外部通信0');
 await context.close();console.log(`実製品認証UI ${checks}検査: 成功、クライアント例外0、外部通信0（合成データのみ）`);
 }finally{await browser?.close();for(const child of[...children])await stop(child);if(proxy)await new Promise(resolve=>proxy.close(resolve));fs.rmSync(root,{recursive:true,force:true});}
})().catch(e=>{console.error(e.message);process.exitCode=1;});
