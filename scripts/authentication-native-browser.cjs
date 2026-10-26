'use strict';
// localhost合成DB＋CDP仮想認証器だけを使用。認証状態・credentialをファイル保存しない。
const assert=require('node:assert/strict');
const {chromium}=require('playwright');
const fs=require('node:fs');const os=require('node:os');const path=require('node:path');
const {spawn}=require('node:child_process');
const https=require('node:https');
const fixture=process.env.SEM_AUTH_FIXTURE_DLL;
let fixtureProcess;let fixtureDirectory;
async function stopFixture(){
 if(!fixtureProcess||fixtureProcess.exitCode!==null||!fixtureProcess.pid)return;
 const child=fixtureProcess;await new Promise(resolve=>{const timer=setTimeout(()=>{child.kill('SIGKILL');},2000);child.once('exit',()=>{clearTimeout(timer);resolve();});child.kill('SIGTERM');});
 fixtureProcess=null;
}
async function startFixture(resume=false,trustProxy=false){
 if(!fixture)return;
 assert(path.basename(fixture)==='Sem.Authentication.Fixture.dll'&&fs.existsSync(fixture),'合成ホストのbuildが必要です');
 fixtureDirectory??=fs.mkdtempSync(path.join(os.tmpdir(),'sem-auth-browser-'));
 fixtureProcess=spawn(process.env.DOTNET_COMMAND||'dotnet',[fixture],{stdio:'ignore',env:{...process.env,SEM_AUTH_FIXTURE_DIRECTORY:path.join(fixtureDirectory,'state'),SEM_AUTH_FIXTURE_PORT:origin.port,SEM_AUTH_FIXTURE_RESUME:resume?'1':'0',SEM_AUTH_FIXTURE_TRUST_LOOPBACK:trustProxy?'1':'0'}});
 for(let n=0;n<80;n++){
  assert(fixtureProcess.exitCode===null,'合成ホストの起動が失敗しました');
  const ready=await new Promise(resolve=>{const req=https.get(base,{rejectUnauthorized:false,timeout:500},res=>{res.resume();resolve(res.statusCode===200);});req.on('timeout',()=>req.destroy());req.on('error',()=>resolve(false));});
  if(ready)return;
  await new Promise(resolve=>setTimeout(resolve,250));
 }
 throw Error('合成ホストの起動上限を超えました');
}
const base=process.env.SEM_AUTH_FIXTURE_URL||'https://localhost:18766';
const origin=new URL(base);
assert(origin.protocol==='https:'&&['localhost','127.0.0.1','[::1]'].includes(origin.hostname)&&origin.pathname==='/'&&!origin.search&&!origin.hash&&!origin.username&&!origin.password,'隔離したHTTPSホストだけを指定してください');
(async()=>{
 await startFixture();
 const browser=await chromium.launch({executablePath:process.env.CHROMIUM_PATH||undefined,args:['--no-sandbox']});
 let checks=0;const errors=[];const check=(value,label)=>{assert(value,label);checks++;};
 try{
  const context=await browser.newContext({ignoreHTTPSErrors:true});
  const page=await context.newPage();page.on('pageerror',e=>errors.push(e.message));await page.goto(base);
  let priorLoginCookies;let priorLoginToken;
  await page.exposeFunction('captureLoginState',async token=>{if(!priorLoginCookies){priorLoginCookies=await context.cookies();priorLoginToken=token;}});
  const cdp=await context.newCDPSession(page);await cdp.send('WebAuthn.enable');
  await cdp.send('WebAuthn.addVirtualAuthenticator',{options:{protocol:'ctap2',transport:'internal',hasResidentKey:true,hasUserVerification:true,isUserVerified:true,automaticPresenceSimulation:true}});
  // 合成の標準Identity初回登録。製品コードには登録fixtureを設けない。
  check(await page.evaluate(async()=>{
   const token=(await(await fetch('/api/auth/csrf')).json()).token;
   const headers={'Content-Type':'application/json','X-CSRF-TOKEN':token};
   const options=await(await fetch('/fixture/create-options',{method:'POST',headers,body:'{}'})).json();
   const credential=await navigator.credentials.create({publicKey:PublicKeyCredential.parseCreationOptionsFromJSON(options)});
   const response=await fetch('/fixture/create',{method:'POST',headers,body:JSON.stringify(credential.toJSON())});
   return response.ok;
  }),'標準Identity＋仮想認証器で合成キーを登録');
  const results=await page.evaluate(async()=>{
   const checks=[];const ok=(value,label)=>{if(!value)throw Error(label);checks.push(label);};
   const csrf=async()=> (await(await fetch('/api/auth/csrf')).json()).token;
   const send=async(path,body,headers={})=>fetch(path,{method:'POST',headers:{'Content-Type':'application/json','X-CSRF-TOKEN':await csrf(),...headers},body:typeof body==='string'?body:JSON.stringify(body)});
   const session=()=>fetch('/api/auth/session');
   let r=await session();ok(r.status===401,'匿名では本人情報を返さない');
   r=await fetch('/api/auth/login-options');ok(r.status===405&&r.headers.get('Allow')==='POST','GETログイン拒否');
   r=await send('/api/auth/login-options?x=1',{email:'synthetic@example.invalid'});ok(r.status===400,'query拒否');
   r=await send('/api/auth/login-options',{email:'synthetic@example.invalid'},{'Content-Type':'text/plain'});ok(r.status===415,'非JSON拒否');
   r=await send('/api/auth/login-options',{email:'synthetic@example.invalid'},{'Content-Encoding':'gzip'});ok(r.status===415,'圧縮拒否');
   r=await send('/api/auth/login-options',{email:'synthetic@example.invalid'},{'X-CSRF-TOKEN':'invalid'});ok(r.status===400,'CSRF不正拒否');
   r=await send('/api/auth/login-options','{"email":"synthetic@example.invalid","email":"other@example.invalid"}');ok(r.status===400,'重複項目拒否');
   r=await send('/api/auth/login-options',{email:'synthetic@example.invalid',admin:true});ok(r.status===400,'余分な項目拒否');
   r=await send('/api/auth/login-options','[');ok(r.status===400,'不正JSONは固定400');
   r=await send('/api/auth/login-options','x'.repeat(4097));ok(r.status===413,'本文上限拒否');
   r=await send('/api/auth/login-options',{email:'missing@example.invalid'});const absent=await r.text();ok(r.status===400,'未登録は失敗');
   const login=async()=>{
    const optionsResponse=await send('/api/auth/login-options',{email:'synthetic@example.invalid'});
    ok(optionsResponse.ok,'メール指定の標準options');
    const options=await optionsResponse.json();ok(options.userVerification==='required','ユーザー検証required');
    await window.captureLoginState(await csrf());
    const key=await navigator.credentials.get({publicKey:PublicKeyCredential.parseRequestOptionsFromJSON(options)});
    const credential=JSON.stringify(key.toJSON());
    const response=await send('/api/auth/login',{credential});ok(response.ok,'署名検証した標準パスキーログイン');
    return credential;
   };
   const credential=await login();
   r=await session();const state=await r.json();
   ok(r.ok&&state.id==='synthetic-user'&&state.displayName==='合成管理者'&&state.isAdmin===true,'本人最小DTO');
   ok(Object.keys(state).sort().join(',')==='displayName,id,isAdmin','メール・キー・stamp・sessionをDTOへ出さない');
   ok(r.headers.get('cache-control').split(',').some(s=>s.trim()==='no-store'),'本人応答no-store');
   r=await session();ok(r.ok,'stamp再生成後もsession claimを保持');
   r=await send('/api/auth/login',{credential});ok(r.status===400,'challenge／署名の再利用拒否 status='+r.status);
   await send('/fixture/change/revoke',{});r=await session();ok(r.status===401,'失効直後のCookie拒否');
   await login();r=await session();ok(r.ok,'元のキーで再ログイン');
   await send('/fixture/change/stamp',{});r=await session();ok(r.status===401,'stamp回転後のCookie拒否');
   await login();await send('/fixture/change/disable',{});r=await session();ok(r.status===401,'無効化直後拒否');
   r=await send('/api/auth/login-options',{email:'synthetic@example.invalid'});ok(r.status===400&&(await r.text())===absent,'無効と不在で同じエラー本文');
   await send('/fixture/change/enable',{});await login();await send('/fixture/change/unconfirm',{});r=await session();ok(r.status===401,'メール未確認へ変更後拒否');
   await send('/fixture/change/confirm',{});
   await login();
   return {checks,credential};
  });
  checks+=results.checks.length;
  const replay=await context.request.post(base+'/api/auth/login',{headers:{Origin:origin.origin,'Content-Type':'application/json',Cookie:priorLoginCookies.map(c=>c.name+'='+c.value).join('; '),'X-CSRF-TOKEN':priorLoginToken},data:{credential:results.credential}});
  check(replay.status()===400,'旧Cookieを含む再送でも成功しない');
  if(fixture){
   await stopFixture();await startFixture(true);
   check(await page.evaluate(async()=> (await fetch('/api/auth/session')).ok),'DB・鍵を維持した再起動後に既存Cookieを検証');
  }
  const cookies=await context.cookies();
  check(cookies.some(c=>c.name==='__Host-SEM-Auth'),'互換する認証Cookie名');
  const staleCookie=cookies.map(c=>c.name+'='+c.value).join('; ');
  check(await page.evaluate(async()=>{
   const token=(await(await fetch('/api/auth/csrf')).json()).token;
   return (await fetch('/api/auth/logout',{method:'POST',headers:{'Content-Type':'application/json','X-CSRF-TOKEN':token},body:'{}'})).ok;
  }),'Origin・CSRF付きログアウト');
  check((await context.request.get(base+'/api/auth/session',{headers:{Cookie:staleCookie}})).status()===401,'ログアウト前Cookieの再利用拒否');
  check(cookies.filter(c=>c.name.startsWith('__Host-SEM-')).every(c=>c.secure&&c.httpOnly&&c.path==='/'),'認証関連CookieのSecure・HttpOnly・root path');
  if(fixture){
   check(await page.evaluate(async()=>{
    const csrf=async()=> (await(await fetch('/api/auth/csrf')).json()).token;
    const options=await(await fetch('/api/auth/login-options',{method:'POST',headers:{'Content-Type':'application/json','X-CSRF-TOKEN':await csrf()},body:JSON.stringify({email:'synthetic@example.invalid'})})).json();
    const key=await navigator.credentials.get({publicKey:PublicKeyCredential.parseRequestOptionsFromJSON(options)});
    return (await fetch('/api/auth/login',{method:'POST',headers:{'Content-Type':'application/json','X-CSRF-TOKEN':await csrf()},body:JSON.stringify({credential:JSON.stringify(key.toJSON())})})).ok;
   }),'再起動後も同じネイティブパスキーで再ログイン');
  }
  const response=await context.request.post(base+'/api/auth/login-options',{headers:{Origin:'https://wrong.example.invalid','Content-Type':'application/json'},data:{email:'synthetic@example.invalid'}});
  check(response.status()===403,'別Origin拒否');
  const noOrigin=await context.request.post(base+'/api/auth/login-options',{headers:{'Content-Type':'application/json'},data:{email:'synthetic@example.invalid'}});
  check(noOrigin.status()===403,'Originなし拒否');
  const badHost=await context.request.get(base+'/api/auth/session',{headers:{Host:'wrong.example.invalid'}});
  check(badHost.status()===400,'不正Host拒否');
  const plain=new URL(base);plain.protocol='http:';plain.port=String(Number(origin.port)+1);
  const spoof=await context.request.get(plain.href,{headers:{Host:origin.host,'X-Forwarded-Proto':'https','X-Forwarded-For':'203.0.113.8'}});
  check(spoof.status()===400,'信頼未設定の転送ヘッダーでHTTPS偽装不可');
  if(fixture){
   await stopFixture();await startFixture(true,true);
   const currentCookies=(await context.cookies()).map(c=>c.name+'='+c.value).join('; ');
   const proxied=await context.request.get(plain.href+'api/auth/session',{headers:{Host:origin.host,'X-Forwarded-Proto':'https','X-Forwarded-For':'203.0.113.8',Cookie:currentCookies}});
   check(proxied.status()===200,'明示したloopbackプロキシ経由でHTTPS終端・現Cookieを検証');
  }
  // 上限を緩めず、残りの匿名CSRF要求で実際の429を確認する。
  let limited=false;for(let i=0;i<65;i++){if((await context.request.get(base+'/api/auth/csrf')).status()===429){limited=true;break;}}
  check(limited,'ログイン系の実レート制限429');
  check(errors.length===0,'クライアント例外なし');
  await context.close();
  console.log(JSON.stringify({checks,errors:errors.length,scope:'隔離HTTPS・標準Identity・CDP仮想キー。物理端末・実DB・公開ホスト接続ではない。'}));
 }finally{await browser.close();}
})().finally(async()=>{await stopFixture();if(fixtureDirectory)fs.rmSync(fixtureDirectory,{recursive:true,force:true});}).catch(error=>{console.error('合成認証検証失敗:',error.message);process.exit(1);});
