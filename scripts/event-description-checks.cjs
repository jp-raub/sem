'use strict';
module.exports=async({p,g,ok})=>{
 const page=await p.context().newPage();let external=0;
 page.on('request',r=>{if(r.url().includes('untrusted-description.example'))external++;});
 try{
  await page.goto('https://localhost:18443/workspace');await page.locator('[data-workspace-interactive="true"]').waitFor();
  await page.locator('#event-new').click();await page.locator('#event-title').fill('Rich description browser fixture');await page.locator('#event-starts').fill('2099-01-03T12:00');
  await require('./event-editor-layout-checks.cjs')({page,ok});
  await page.locator('#event-title').locator('..').click({position:{x:1,y:1}});
  ok(await page.locator('#event-title').evaluate(e=>e===document.activeElement)&&await page.locator('.field-help-panel').count()===0,'Real event label focuses title without triggering field help');
  await page.keyboard.press('Tab');
  ok(await page.locator('#event-opens').evaluate(e=>e===document.activeElement),'Real editor Tab moves directly to the next input, skipping help');
  await page.locator('#event-title').focus();await page.keyboard.press('Alt+F1');await page.locator('.field-help-panel').waitFor();
  ok(await page.locator('[data-field-help="event.title"]').getAttribute('aria-expanded')==='true','Real editor keyboard shortcut opens the focused title help');
  await page.keyboard.press('Escape');
  ok(await page.locator('#event-title').inputValue()==='Rich description browser fixture','Help open and close preserves the event draft');
  await page.locator('#event-description-format').selectOption('markdown');await page.locator('#event-description-source').fill('## Music\n**Preview** and `code`');
  await page.locator('#event-description-preview strong').waitFor();
  ok(await page.locator('#event-description-preview h2').innerText()==='Music'&&await page.locator('#event-description-preview code').innerText()==='code','Markdown draft preview updates before any save');
  await page.locator('#event-phase').selectOption('View');await page.locator('#event-core-save').click();await page.waitForFunction(()=>!document.querySelector('#event-core-form'));
  let saved=await page.evaluate(async()=> (await(await fetch('/api/core/workspace')).json()).events.find(e=>e.title==='Rich description browser fixture'));
  ok(await page.locator(`[data-core-event="${saved.id}"] .workspace-event-description`).count()===0,'Saved Markdown source is absent from the event list');
  await require('./event-detail-editor-helper.cjs')(page,saved.id);await page.locator('#event-description-source').waitFor();
  await page.locator('#event-title').fill('Cancelled layout draft');await page.locator('#event-core-cancel').click();await page.waitForFunction(()=>!document.querySelector('#event-core-form'));
  ok((await page.locator('#event-overview-title').innerText()).includes('Rich description browser fixture')&&!(await page.locator('#event-overview-title').innerText()).includes('Cancelled layout draft'),'Reordered editor cancellation does not save draft changes');
  await require('./event-detail-editor-helper.cjs')(page,saved.id);await page.locator('#event-description-source').waitFor();
  ok(await page.locator('#event-description-source').inputValue()==='## Music\n**Preview** and `code`'&&await page.locator('#event-description-format').inputValue()==='markdown','Hiding list summary does not discard Markdown source or editor format');
  const source='<h2>Music</h2><p><strong>safe</strong><img src="https://untrusted-description.example/image" onerror="window.__descriptionExecuted=true"><script>window.__descriptionExecuted=true</script><a href="javascript:alert(1)" onclick="alert(1)">unsafe</a><a href="https://venue.example/">map</a></p>';
  await page.locator('#event-description-format').selectOption('html');await page.locator('#event-description-source').fill(source);
  await page.waitForFunction(()=>document.querySelector('#event-description-preview strong')?.textContent==='safe');
  ok(await page.locator('#event-description-preview img,#event-description-preview script,#event-description-preview [onclick]').count()===0&&await page.evaluate(()=>!window.__descriptionExecuted),'HTML preview removes active tags and attributes before rendering');
  await page.locator('#event-phase').selectOption('View');await page.locator('#event-core-save').click();await page.waitForFunction(()=>!document.querySelector('#event-core-form'));
  const event=await page.evaluate(async()=> (await(await fetch('/api/core/workspace')).json()).events.find(e=>e.title==='Rich description browser fixture'));
  ok(event.description===source&&event.descriptionFormat==='html','Event persists editable source and explicit description format');
  await page.goto('https://localhost:18443/workspace');await page.locator('[data-workspace-interactive="true"]').waitFor();
  ok(await page.locator(`[data-core-event="${event.id}"] .workspace-event-description`).count()===0&&!((await page.locator(`[data-core-event="${event.id}"]`).innerText()).includes('<h2>')),'Saved HTML source is absent from the event list');
  await page.goto('https://localhost:18443/events/'+event.id);const rendered=page.locator('.event-overview-hero .event-description-content');await rendered.locator('strong').waitFor();
  ok(await rendered.locator('img,script,[onclick]').count()===0&&await rendered.locator('a[href]').count()===1&&external===0,'Saved description uses the same sanitizer and never loads removed external image');
  const member=await g.context().newPage();try{await member.goto('https://localhost:18443/events/'+event.id);await member.locator('.event-overview-hero .event-description-content strong').waitFor();ok(await member.locator('.event-overview-hero .event-description-content script,.event-overview-hero .event-description-content img').count()===0,'Ordinary authorized participant receives sanitized saved description');}finally{await member.close();}
  await require('./event-detail-editor-checks.cjs')({p,g,ok,eventId:event.id});
  await require('./event-icon-checks.cjs')({p,g,ok,eventId:event.id});
  await require('./event-editor-settings-checks.cjs')({p,ok});
 }finally{await page.close();}
};
