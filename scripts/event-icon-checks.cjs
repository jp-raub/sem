'use strict';
// Real persisted event, native-authenticated owner/member contexts; no copied HTML or fake save.
module.exports=async({p,g,ok,eventId})=>{
 const page=await p.context().newPage(),member=await g.context().newPage();
 const row=()=>page.locator(`[data-core-event="${eventId}"]`);
 async function edit(){await page.goto('https://localhost:18443/workspace');await page.locator('[data-workspace-interactive="true"]').waitFor();await row().locator('summary').click();await row().getByRole('button',{name:'概要・管理者を編集',exact:true}).click();await page.locator('#event-icon').waitFor();}
 async function save(){await page.locator('#event-core-save').click();await page.waitForFunction(()=>!document.querySelector('#event-core-form'));}
 async function stored(){return page.evaluate(async id=>(await(await fetch('/api/core/workspace')).json()).events.find(e=>e.id===id).icon,eventId);}
 try{
  await edit();ok(await page.locator('#event-icon').inputValue()==='','Previously iconless event reopens without fabricated icon');
  await page.locator('#event-icon').fill('👩‍🎤 合奏');await page.locator('#event-icon-preview .event-icon').waitFor();
  ok(await page.locator('#event-icon-preview .event-icon').innerText()==='👩‍🎤 合奏'&&await stored()==='','Icon draft previews immediately without writing saved event');
  await page.locator('#event-core-cancel').click();await page.waitForFunction(()=>!document.querySelector('#event-core-form'));
  ok(await stored()===''&&await row().locator('.event-icon').count()===0,'Cancel leaves saved icon and list unchanged');
  await edit();await page.locator('#event-icon').fill('👩‍🎤 合奏');await save();
  ok(await stored()==='👩‍🎤 合奏'&&await row().locator('.event-icon').innerText()==='👩‍🎤 合奏','Saved Unicode icon appears in real event list');
  await page.goto('https://localhost:18443/events/'+eventId);await page.locator('#event-overview-title .event-icon').waitFor();
  ok(await page.locator('#event-overview-title .event-icon').innerText()==='👩‍🎤 合奏','Saved icon appears in event overview heading');
  await member.goto('https://localhost:18443/events/'+eventId);await member.locator('#event-overview-title .event-icon').waitFor();
  ok(await member.locator('#event-overview-title .event-icon').innerText()==='👩‍🎤 合奏','Authorized ordinary participant reads same persisted event icon');
  await edit();ok(await page.locator('#event-icon').inputValue()==='👩‍🎤 合奏','Reload and reopen retain exact Unicode icon');
  const literal='<img src=x onerror=alert(1)>';
  await page.locator('#event-icon').fill(literal);
  await page.waitForFunction(text=>document.querySelector('#event-icon-preview .event-icon')?.textContent===text,literal);
  ok(await page.locator('#event-icon-preview .event-icon').innerText()===literal&&await page.locator('#event-icon-preview img').count()===0,'Icon preview encodes markup-shaped text instead of creating an image');
  await save();await page.goto('https://localhost:18443/events/'+eventId);await page.locator('#event-overview-title .event-icon').waitFor();
  ok(await page.locator('#event-overview-title .event-icon').innerText()===literal&&await page.locator('#event-overview-title img').count()===0,'Saved icon is encoded in overview, never interpreted as HTML');
  await edit();await page.locator('#event-icon').fill('');await save();
  ok(await stored()===''&&await row().locator('.event-icon').count()===0,'Explicit empty saved input removes only event icon');
  await edit();
  const viewport=page.viewportSize(),mode=await page.evaluate(()=>document.documentElement.getAttribute('data-appearance-mode'));
  await page.setViewportSize({width:390,height:950});
  await page.locator('#event-icon').fill('x'.repeat(64));
  await page.waitForFunction(()=>document.querySelector('#event-icon-preview .event-icon')?.textContent==='x'.repeat(64));
  for(const theme of ['light','dark']){
   await page.evaluate(theme=>document.documentElement.dataset.appearanceMode=theme,theme);
   ok(await page.evaluate(()=>document.documentElement.scrollWidth<=window.innerWidth+1)&&await stored()==='',`64-unit icon draft fits 390px ${theme} without page overflow or implicit save`);
  }
  if(viewport)await page.setViewportSize(viewport);
  await page.evaluate(mode=>{if(mode===null)document.documentElement.removeAttribute('data-appearance-mode');else document.documentElement.dataset.appearanceMode=mode;},mode);
  await page.locator('#event-icon').fill('🎻 retained');
  await page.waitForFunction(()=>document.querySelector('#event-icon-preview .event-icon')?.textContent==='🎻 retained');
  await save();ok(await stored()==='🎻 retained','Unicode event icon is present in populated whole-directory retention fixture');
 }finally{await member.close();await page.close();}
};
