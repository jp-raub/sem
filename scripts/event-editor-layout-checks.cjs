'use strict';
// Runs inside the real authenticated Workspace editor, never a copied HTML fixture.
module.exports=async({page,ok})=>{
 const viewport=page.viewportSize();
 const previousMode=await page.evaluate(()=>document.documentElement.getAttribute('data-appearance-mode'));
 try{
  const ids=['event-title','event-opens','event-starts','event-location','event-location-url','event-description-format','event-description-source','event-description-preview','event-announcement','event-header-file'];
  const order=await page.locator('#event-core-form').evaluate((form,ids)=>Array.from(form.querySelectorAll('[id]')).map(e=>e.id).filter(id=>ids.includes(id)),ids);
  ok(JSON.stringify(order)===JSON.stringify(ids),'Overview editor DOM order matches title, dates, venue, description and header: '+JSON.stringify(order));
  ok((await page.locator('#event-editor-availability').innerText()).includes('アイコンは未実装'),'Unavailable event icon is explicitly identified, not a fake editable control');
  for(const width of [390,768,1440])for(const mode of ['light','dark']){
   await page.setViewportSize({width,height:950});
   await page.evaluate(mode=>document.documentElement.dataset.appearanceMode=mode,mode);
   await page.waitForFunction(()=>Array.from(document.querySelectorAll('.event-editor-pair')).every(e=>getComputedStyle(e).display==='grid'));
   const state=await page.locator('#event-core-form').evaluate(form=>{
    const bounds=form.getBoundingClientRect();
    const pairs=Array.from(form.querySelectorAll('.event-editor-pair')).map(pair=>{
     const boxes=Array.from(pair.querySelectorAll('input')).map(e=>e.getBoundingClientRect());
     return boxes.length===2&&Math.abs(boxes[0].top-boxes[1].top)<2&&boxes[0].right<=boxes[1].left&&boxes.every(b=>b.width>60&&b.left>=bounds.left-1&&b.right<=bounds.right+1);
    });
    const controls=Array.from(form.querySelectorAll('input,select,textarea')).filter(e=>e.getClientRects().length).every(e=>{const b=e.getBoundingClientRect();return b.left>=bounds.left-1&&b.right<=bounds.right+1;});
    return {pairs,controls,overflow:document.documentElement.scrollWidth>window.innerWidth+1, width:bounds.width, documentWidth:document.documentElement.scrollWidth, outside:Array.from(form.querySelectorAll('input,select,textarea')).filter(e=>{const b=e.getBoundingClientRect();return b.width&& (b.left<bounds.left-1||b.right>bounds.right+1);}).map(e=>e.id)};
   });
   ok(state.pairs.length===2&&state.pairs.every(Boolean)&&state.controls&&!state.overflow,`Real overview pairs and controls fit ${width}px ${mode} without page overflow: ${JSON.stringify(state)}`);
  }
  await page.locator('#event-title').focus();await page.keyboard.press('Tab');
  ok(await page.locator('#event-opens').evaluate(e=>e===document.activeElement),'DOM order and forward Tab both reach opening before start');
  for(let n=0;n<16&&await page.locator('#event-opens').evaluate(e=>e===document.activeElement);n++)await page.keyboard.press('Tab');
  ok(await page.locator('#event-starts').evaluate(e=>e===document.activeElement),'Tab follows the paired opening/start sequence');
  for(let n=0;n<16&&await page.locator('#event-starts').evaluate(e=>e===document.activeElement);n++)await page.keyboard.press('Shift+Tab');
  ok(await page.locator('#event-opens').evaluate(e=>e===document.activeElement),'Reverse Tab follows the same visible date sequence');
 }finally{
  if(viewport)await page.setViewportSize(viewport);
  await page.evaluate(mode=>{if(mode===null)document.documentElement.removeAttribute('data-appearance-mode');else document.documentElement.setAttribute('data-appearance-mode',mode);},previousMode);
 }
};
