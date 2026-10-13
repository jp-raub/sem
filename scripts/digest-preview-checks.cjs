module.exports=async({p,g,ok})=>{
 const today=new Intl.DateTimeFormat('sv-SE',{timeZone:'Asia/Tokyo'}).format(new Date());
 const yesterday=new Intl.DateTimeFormat('sv-SE',{timeZone:'Asia/Tokyo'}).format(new Date(Date.now()-86400000));
 const api=(page,query='')=>page.evaluate(async query=>{const r=await fetch('/api/core/digest-preview'+query);return{status:r.status,body:await r.json()};},query);
 const before=await(await fetch('http://127.0.0.1:18025')).json();
 const preview=await api(p);ok(preview.status===200&&preview.body.day===today&&preview.body.timeZone==='Asia/Tokyo','Native digest defaults to actual Japanese date');
 ok(preview.body.operationCount>0&&preview.body.registrationSlots>0&&preview.body.releaseSlots>0,'Native digest covers committed business register and release history');
 ok(preview.body.body.includes('Email Song A')&&preview.body.body.includes('Native performer request')&&preview.body.body.includes('現在の表示名'),'Native preview contains frozen successful slots and release reason with current-name caveat');
 ok(!JSON.stringify(preview.body).includes('protectedPayload')&&!preview.body.body.includes('@example.test'),'Digest DTO exposes neither ciphertext nor recipient email list');
 ok((await api(g)).status===403,'Native ordinary member cannot fetch global digest');
 ok((await api(p,'?day=2026-02-30')).status===400&&(await api(p,'?day=2026-11-02T00%3A00%3A00Z')).status===400,'Native API refuses invalid calendar and timestamp instead of date');
 const a=await p.context().newPage(),m=await g.context().newPage(),errors=[];a.on('pageerror',e=>errors.push(e.message));
 try{
  await a.goto('https://localhost:18443/service/digest-preview');await a.waitForFunction(()=>document.getElementById('digest-preview')?.dataset.interactive==='true');
  ok((await a.locator('#digest-policy').innerText()).includes('00:05')&&(await a.locator('#digest-policy').innerText()).includes('1人1通'),'Real preview UI states approved automatic policy without a send control');
  ok((await a.locator('#digest-body').innerText()).includes('送信予約・実送信は行いません')&&await a.locator('#digest-day').inputValue()===today,'Actual Blazor mail preview labels no-send and Japanese date');
  await a.setViewportSize({width:390,height:844});ok(await a.locator('#digest-day').isVisible()&&await a.locator('#digest-body').isVisible(),'Mobile global administrator can select date and read preview');
  await a.locator('#digest-day').fill(yesterday);await a.locator('#digest-refresh').click();await a.waitForFunction(day=>document.getElementById('digest-preview')?.dataset.day===day,yesterday);
  ok((await a.locator('#digest-summary').innerText()).includes('0 件')&&(await a.locator('#digest-body').innerText()).includes('ありません'),'Actual date selection reports empty prior synthetic day without backfill');
  await a.locator('#digest-day').fill(today);await a.locator('#digest-refresh').click();await a.waitForFunction(day=>document.getElementById('digest-preview')?.dataset.day===day,today);
  ok((await a.locator('#digest-body').innerText()).includes('Native performer request'),'Switching back regenerates correct current-day frozen preview');
  await m.goto('https://localhost:18443/service/digest-preview');await m.locator('#digest-error').waitFor();await m.waitForFunction(()=>document.getElementById('digest-error')?.textContent.includes('全体管理者'));
  ok(await m.locator('#digest-preview').count()===0,'Unauthorized member UI exposes neither subject body nor date controls');
  await a.goto('https://localhost:18443/workspace');await a.locator('#digest-preview-link').waitFor();ok(await a.locator('#digest-preview-link').getAttribute('href')==='/service/digest-preview','Actual global service menu provides digest preview navigation');
  await m.goto('https://localhost:18443/workspace');await m.locator('#core-title').waitFor();ok(await m.locator('#digest-preview-link').count()===0,'Member service menu has no global mail-preview entry');
  const after=await(await fetch('http://127.0.0.1:18025')).json();ok(after.filter(x=>x.subject.includes('登録・解除ダイジェスト')).length===before.filter(x=>x.subject.includes('登録・解除ダイジェスト')).length,'Preview and date changes never submit digest SMTP mail');
  ok(errors.length===0,'Actual digest preview Blazor UI has no browser runtime errors');
 }finally{await a.close();await m.close();}
};
