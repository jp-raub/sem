'use strict';
// Real product routes before enrollment. No accounts, secrets or writes.
module.exports=async({p,ok})=>{
 const origin='https://localhost:18443';
 const originalViewport=p.viewportSize();
 const home=await p.goto(origin+'/');
 ok(home.status()===200,'Runtime footer is available on anonymous home');
 const footer=p.locator('.runtime-version-footer a');
 ok(await footer.count()===1&&await footer.getAttribute('href')==='/about','Home footer links to runtime information');
 await footer.click();await p.waitForURL('**/about');
 ok(await p.locator('h1').innerText()==='このアプリについて','Footer opens human-readable about page');
 const about=await p.reload();
 ok(about.headers()['cache-control']?.includes('no-store'),'Human runtime page is not cacheable');
 const data=await p.evaluate(async()=>{
  const nonce=Array.from(crypto.getRandomValues(new Uint8Array(16)),x=>x.toString(16).padStart(2,'0')).join('');
  const version=await fetch('/api/version?probe='+nonce,{cache:'no-store',redirect:'error'});
  const body=await version.json();
  const ready=await fetch('/health/ready?probe='+nonce,{cache:'no-store',redirect:'error'});
  return {nonce,body,status:version.status,cache:version.headers.get('cache-control'),ready:await ready.json(),readyStatus:ready.status};
 });
 ok(data.status===200&&data.body.schema===1&&data.body.application==='SEM','Real anonymous version route returns explicit schema');
 ok(data.body.probe===data.nonce&&data.cache.includes('no-store'),'Version response proves current probe and no-store');
 ok(data.readyStatus===200&&data.ready.status==='storage-ready'&&data.ready.probe===data.nonce,'Readiness also proves current probe');
 ok(Object.keys(data.body).sort().join(',')==='application,buildId,channel,commit,probe,publicationAttempt,publicationRun,schema','Only allowlisted non-secret runtime fields are public');
 if(data.body.channel==='development'){
  ok(data.body.commit===null&&data.body.buildId===null,'Unlabelled development build does not impersonate published build');
  ok((await p.locator('main').innerText()).includes('開発版'),'Human page explicitly identifies development build');
 }else{
  ok(/^[a-f0-9]{40}$/.test(data.body.commit)&&data.body.buildId===`run-${data.body.publicationRun}-${data.body.publicationAttempt}`,'Published runtime reports full exact build identity');
  ok((await p.locator('main').innerText()).includes(data.body.commit),'Human page shows full runtime commit');
 }
 for(const width of [320,390,768,1440]){
  await p.setViewportSize({width,height:844});
  ok(await p.evaluate(()=>document.documentElement.scrollWidth<=innerWidth),'About and footer stay within '+width+'px');
 }
 await p.locator('a[href="/api/version"]').focus();
 ok(await p.locator('a[href="/api/version"]').evaluate(e=>e===document.activeElement),'Machine link is keyboard-focusable');
 if(originalViewport)await p.setViewportSize(originalViewport);
 await p.goto(origin+'/');await p.waitForSelector('#setup');
};
