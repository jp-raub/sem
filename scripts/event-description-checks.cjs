'use strict';
module.exports=async({p,g,ok})=>{
 const page=await p.context().newPage();let external=0;
 page.on('request',r=>{if(r.url().includes('untrusted-description.example'))external++;});
 try{
  await page.goto('https://localhost:18443/workspace');await page.locator('[data-workspace-interactive="true"]').waitFor();
  await page.locator('#event-new').click();await page.locator('#event-title').fill('Rich description browser fixture');await page.locator('#event-starts').fill('2099-01-03T12:00');
  await page.locator('#event-description-format').selectOption('markdown');await page.locator('#event-description-source').fill('## Music\n**Preview** and `code`');
  await page.locator('#event-description-preview strong').waitFor();
  ok(await page.locator('#event-description-preview h2').innerText()==='Music'&&await page.locator('#event-description-preview code').innerText()==='code','Markdown draft preview updates before any save');
  const source='<h2>Music</h2><p><strong>safe</strong><img src="https://untrusted-description.example/image" onerror="window.__descriptionExecuted=true"><script>window.__descriptionExecuted=true</script><a href="javascript:alert(1)" onclick="alert(1)">unsafe</a><a href="https://venue.example/">map</a></p>';
  await page.locator('#event-description-format').selectOption('html');await page.locator('#event-description-source').fill(source);
  await page.waitForFunction(()=>document.querySelector('#event-description-preview strong')?.textContent==='safe');
  ok(await page.locator('#event-description-preview img,#event-description-preview script,#event-description-preview [onclick]').count()===0&&await page.evaluate(()=>!window.__descriptionExecuted),'HTML preview removes active tags and attributes before rendering');
  await page.locator('#event-phase').selectOption('View');await page.locator('#event-core-save').click();await page.waitForFunction(()=>!document.querySelector('#event-core-form'));
  const event=await page.evaluate(async()=> (await(await fetch('/api/core/workspace')).json()).events.find(e=>e.title==='Rich description browser fixture'));
  ok(event.description===source&&event.descriptionFormat==='html','Event persists editable source and explicit description format');
  await page.goto('https://localhost:18443/events/'+event.id);const rendered=page.locator('.event-overview-hero .event-description-content');await rendered.locator('strong').waitFor();
  ok(await rendered.locator('img,script,[onclick]').count()===0&&await rendered.locator('a[href]').count()===1&&external===0,'Saved description uses the same sanitizer and never loads removed external image');
  const member=await g.context().newPage();try{await member.goto('https://localhost:18443/events/'+event.id);await member.locator('.event-overview-hero .event-description-content strong').waitFor();ok(await member.locator('.event-overview-hero .event-description-content script,.event-overview-hero .event-description-content img').count()===0,'Ordinary authorized participant receives sanitized saved description');}finally{await member.close();}
 }finally{await page.close();}
};
