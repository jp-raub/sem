'use strict';
// Synthetic local HTTP boundary; no real identity, settings, cookies or private data.
const assert=require('node:assert/strict'),fs=require('node:fs'),http=require('node:http');
const {chromium}=require('playwright');
const root='src/Sem.Web/wwwroot/';
const html='<!doctype html><html><head><meta name="viewport" content="width=device-width"><link rel="stylesheet" href="/theme-appearance.css"></head><body><input id="unsent" value="draft"><script>window.Blazor=new EventTarget();</script><script type="module" src="/theme-appearance.js"></script></body></html>';
const server=http.createServer((req,res)=>{if(['/theme-appearance.js','/theme-appearance.css'].includes(req.url)){res.setHeader('Content-Type',req.url.endsWith('.js')?'text/javascript':'text/css');res.end(fs.readFileSync(root+req.url.slice(1)));}else{res.setHeader('Content-Type','text/html');res.end(html);}});
const pref=(themeId=null,mode=null)=>({themeId,mode,rowVersion:1});
function dto(id,color='#244466',mode=1){const colors={accent:color,background:'#eef4fa',surface:'#ffffff',text:'#112233'};return {eventId:id,appearance:{actorId:'synthetic-actor',canManageEvent:false,canManageSite:false,catalog:[{id:'green',name:'Synthetic',builtIn:true,rowVersion:1,...colors}],personal:pref(null,mode),site:pref('green'),event:id===null?null:pref(),resolved:{...Object.fromEntries(Object.entries(colors).map(([k,value])=>[k,{value}])),mode}}};}
(async()=>{
 await new Promise(r=>server.listen(0,'127.0.0.1',r));
 const browser=await chromium.launch({executablePath:process.env.SEM_CHROMIUM||undefined,args:['--no-sandbox']});let checks=0;
 const ok=(v,t)=>{assert(v,t);checks++;console.log('PASS '+t);};
 try{
  const p=await browser.newPage();const errors=[];let requests=0,writes=0,status=200,color='#244466',mode=1,hold=null;
  p.on('pageerror',e=>errors.push(e.message));p.on('request',r=>{if(r.method()!=='GET')writes++;});
  await p.route('**/api/core/theme-appearance*',async route=>{requests++;const id=new URL(route.request().url()).searchParams.get('id'),body=dto(id,color,mode),code=status;if(hold){const h=hold;hold=null;await h;}await route.fulfill({status:code,contentType:'application/json',body:JSON.stringify(body)}).catch(()=>{});});
  const origin='http://127.0.0.1:'+server.address().port;
  const accent=()=>p.evaluate(()=>document.documentElement.style.getPropertyValue('--appearance-accent'));
  const waitAccent=value=>p.waitForFunction(v=>document.documentElement.style.getPropertyValue('--appearance-accent').toLowerCase()===v,value,{timeout:4000});
  const navigate=path=>p.evaluate(path=>{history.pushState({},'',path);document.documentElement.removeAttribute('style');delete document.documentElement.dataset.appearanceMode;window.Blazor.dispatchEvent(new Event('enhancedload'));},path);
  await p.goto(origin+'/');await waitAccent('#244466');ok(true,'Cold first document applies saved theme without reload');
  await navigate('/workspace');await waitAccent('#244466');ok(true,'First enhanced page reapplies tokens after document merge without reload');
  const before=requests;color='#663344';await navigate('/events/event-a');await waitAccent('#663344');ok(requests>before,'First event entry loads its own context through Blazor enhancedload');
  color='#224466';await navigate('/events/event-b');await waitAccent('#224466');ok(true,'Event context switch replaces previous palette');
  let release;hold=new Promise(r=>release=r);const sent=requests;color='#772244';await navigate('/events/slow');await p.waitForFunction(()=>true);for(let i=0;i<40&&requests===sent;i++)await p.waitForTimeout(10);ok(requests>sent,'Delayed context request has started');
  color='#225544';await navigate('/events/latest');await waitAccent('#225544');release();await p.waitForTimeout(120);ok((await accent()).toLowerCase()==='#225544','Late obsolete response cannot overwrite latest context');
  status=401;await navigate('/auth');await p.waitForFunction(()=>!document.documentElement.hasAttribute('data-appearance-mode'),null,{timeout:4000});ok(await accent()==='','Denied identity clears cached private palette');
  status=200;color='#775522';await navigate('/workspace');await waitAccent('#775522');ok(true,'First page after authentication recovers without reload');
  mode=2;await navigate('/events/dark');await p.waitForFunction(()=>document.documentElement.dataset.appearanceMode==='dark');ok(true,'Explicit dark mode survives first enhanced navigation');
  mode=0;await p.emulateMedia({colorScheme:'light'});await navigate('/events/system');await p.waitForFunction(()=>document.documentElement.dataset.appearanceMode==='light');await p.emulateMedia({colorScheme:'dark'});await p.waitForFunction(()=>document.documentElement.dataset.appearanceMode==='dark');ok(true,'System mode still follows media changes');
  mode=1;await p.evaluate(()=>{document.documentElement.removeAttribute('style');window.dispatchEvent(new PageTransitionEvent('pageshow',{persisted:true}));});await waitAccent('#775522');await p.waitForFunction(()=>document.documentElement.dataset.appearanceMode==='light');ok(true,'Back-forward cache restoration refreshes theme');
  ok(await p.locator('#unsent').inputValue()==='draft','Theme lifecycle preserves unrelated unsent input');ok(writes===0,'Automatic lifecycle performs reads only, never mutation retries');ok(errors.length===0,'No browser runtime errors');
  console.log('Theme lifecycle browser checks passed: '+checks);
 }finally{await browser.close();await new Promise(r=>server.close(r));}
})().catch(e=>{console.error(e);process.exit(1);});
