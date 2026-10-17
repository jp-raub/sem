'use strict';
module.exports=async({p,g,ok})=>{
 const fixture=await g.evaluate(async()=>{
  const w=await(await fetch('/api/core/workspace')).json();
  for(const event of w.events){const r=await fetch('/api/core/participation?id='+event.id);if(!r.ok)continue;const plan=await r.json();if(plan.slots.some(s=>s.assignedUserId)&&plan.slots.some(s=>!s.assignedUserId))return{event:event.id,plan};}
  throw Error('Populated profile fixture missing');
 });
 const page=await g.context().newPage(),errors=[];page.on('pageerror',e=>errors.push(e.message));
 try{
  await page.goto('https://localhost:18443/events/'+fixture.event);
  await page.locator('[data-participation-interactive="true"]').waitFor();
  await page.locator('[data-event-profile]').first().waitFor();
  const free=fixture.plan.slots.find(s=>!s.assignedUserId);const freeButton=page.locator('[data-event-matrix-slot="'+free.id+'"]');
  await freeButton.click();await page.waitForFunction(id=>document.querySelector('[data-event-matrix-slot="'+id+'"]')?.getAttribute('aria-pressed')==='true',free.id);
  const target=page.locator('[data-event-profile]').first();const userId=await target.getAttribute('data-event-profile');await target.click();
  await page.locator('.profile-card[open] #member-profile').waitFor();
  ok(await page.locator('.profile-card #member-profile').getAttribute('data-user')===userId,'Assigned slot opens the real selected member profile');
  ok(await page.locator('.profile-card #member-profile-history-heading').count()===0&&await page.locator('#profile-card-details').getAttribute('href')==='/profiles/'+encodeURIComponent(userId),'Compact profile excludes history and links to the full profile');
  ok(!(/@example\.test|securityStamp|protectedToken/i.test(await page.locator('.profile-card').innerText())),'Compact member card displays no email or authentication secrets');
  await page.locator('#profile-card-close').click();await page.waitForFunction(()=>!document.querySelector('.profile-card'));
  ok(await freeButton.getAttribute('aria-pressed')==='true'&&await target.evaluate(e=>e===document.activeElement),'Closing profile restores its trigger and preserves the participation selection');
  await target.click();await page.locator('.profile-card[open]').waitFor();await page.keyboard.press('Escape');await page.waitForFunction(()=>!document.querySelector('.profile-card'));
  ok(await freeButton.getAttribute('aria-pressed')==='true','Escape closes profile without submitting or resetting a selection');
  const duplicates=await page.evaluate(()=>{const ids=[...document.querySelectorAll('[id]')].map(e=>e.id);return ids.some((id,i)=>ids.indexOf(id)!==i);});
  ok(!duplicates&&errors.length===0,'Profile dialog introduces no duplicate IDs or client errors');
 }finally{await page.close();}
};
