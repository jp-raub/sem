'use strict';
module.exports=async(page,eventId)=>{
 await page.goto('https://localhost:18443/events/'+encodeURIComponent(eventId)+'?tab=main');
 await page.locator('[data-event-interactive="true"]').waitFor();
 await page.locator('#event-edit').click();
 await page.locator('#event-core-form').waitFor();
 await page.waitForFunction(()=>{const b=document.querySelector('#event-core-save');return b&&!b.disabled;});
};
