'use strict';
// Uses only the disposable acceptance fixture. Never follows the external venue URL.
module.exports=async({p,g,ok})=>{
 const page=await p.context().newPage();
 try{
  await page.goto('https://localhost:18443/workspace');await page.locator('[data-workspace-interactive="true"]').waitFor();
  await page.locator('#event-new').click();await page.locator('#event-title').fill('Separate venue URL browser fixture');
  await page.locator('#event-starts').fill('2099-01-02T12:00');
  await page.locator('#event-location').fill('Existing venue <strong>text</strong>');
  const address='https://venue.example/access?map=1&lang=ja#entry';
  await page.locator('#event-location-url').fill(address);await page.locator('#event-phase').selectOption('View');
  await page.locator('#event-core-save').click();await page.waitForFunction(()=>!document.querySelector('#event-core-form'));
  const read=()=>page.evaluate(async()=>{const r=await fetch('/api/core/workspace');if(!r.ok)throw Error('Workspace unavailable');return(await r.json()).events.find(x=>x.title==='Separate venue URL browser fixture');});
  const event=await read();ok(event.location==='Existing venue <strong>text</strong>'&&event.locationUrl===address,'UI saves venue name and URL independently');
  await page.goto('https://localhost:18443/events/'+event.id);await page.locator('#event-location-link').waitFor();
  const link=page.locator('#event-location-link');
  ok(await link.getAttribute('href')===address&&await link.getAttribute('target')==='_blank'&&(await link.getAttribute('rel')).includes('noreferrer'),'Venue link uses safe new-tab navigation without fetching external page');
  ok(await page.locator('.event-overview-hero strong').count()===0&&await page.locator('.event-overview-hero').innerText().then(x=>x.includes('<strong>text</strong>')),'Venue text remains encoded, not executable markup');
  const member=await g.context().newPage();try{await member.goto('https://localhost:18443/events/'+event.id);await member.locator('#event-location-link').waitFor();ok(await member.locator('#event-location-link').getAttribute('href')===address,'Authorized ordinary participant can read published venue URL');}finally{await member.close();}
 }finally{await page.close();}
};
