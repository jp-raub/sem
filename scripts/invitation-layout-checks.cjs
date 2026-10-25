// Native UI, fresh synthetic fixture users only. No emails, real invitations or Push delivery.
module.exports=async({p,g,ok})=>{
 const a=await p.context().newPage(),m=await g.context().newPage(),errors=[];
 for(const page of[a,m])page.on('pageerror',e=>errors.push(e.message));
 const origin='https://localhost:18443';
 const api=(page)=>page.evaluate(async()=>{const r=await fetch('/api/core/invitations?page=1');if(!r.ok)throw Error('Read invitations '+r.status);return r.json();});
 const ready=async(page)=>{await page.goto(origin+'/invitations');await page.waitForFunction(()=>document.getElementById('invitation-management-refresh')?.disabled===false);};
 const open=async(page,kind)=>{await page.locator('#invitation-'+kind+'-open').click();await page.locator('#invitation-'+kind+'-dialog[open]').waitFor();};
 const closed=async(page,kind)=>page.locator('#invitation-'+kind+'-dialog[open]').waitFor({state:'hidden'});
 const modalFits=async(page,kind,label)=>{
  const box=await page.locator('#invitation-'+kind+'-dialog').evaluate(e=>{const r=e.getBoundingClientRect();return{x:r.x,y:r.y,right:r.right,bottom:r.bottom,cx:r.x+r.width/2,cy:r.y+r.height/2,w:innerWidth,h:innerHeight,scroll:e.scrollWidth,client:e.clientWidth};});
  ok(box.x>=0&&box.y>=0&&box.right<=box.w+1&&box.bottom<=box.h+1&&box.scroll<=box.client+1,'Invitation '+kind+' dialog fits '+label);
  ok(Math.abs(box.cx-box.w/2)<2&&Math.abs(box.cy-box.h/2)<2,'Invitation '+kind+' dialog is centered '+label);
 };
 try{
  await ready(a);await ready(m);
  ok(await a.locator('#invitation-issue-open').isVisible()&&await a.locator('#invitation-request-open').isHidden(),'Admin gets direct issue, not request creation');
  ok(await m.locator('#invitation-request-open').isVisible()&&await m.locator('#invitation-issue-open').isHidden(),'Member gets request creation, not issue/revoke authority');
  ok(await a.locator('dialog[open]').count()===0&&await m.locator('dialog[open]').count()===0,'No invitation modal opens without an explicit action');
  const before=await api(m);await open(m,'request');
  await m.locator('#invitation-request-create-reason').fill('Discard invitation modal draft');
  for(let n=0;n<8;n++)await m.keyboard.press('Tab');ok(await m.locator('#invitation-request-dialog').evaluate(e=>e.contains(document.activeElement)),'Native invitation dialog traps keyboard focus');
  await m.keyboard.press('Escape');await closed(m,'request');
  ok(await m.locator('#invitation-request-open').evaluate(e=>e===document.activeElement),'Escape restores request launcher focus');
  await open(m,'request');ok(await m.locator('#invitation-request-create-reason').inputValue()==='','Dismissal clears unsubmitted request input');
  await m.mouse.click(2,2);await closed(m,'request');ok((await api(m)).requests.length===before.requests.length,'Backdrop and Escape do not send an invitation request');
  await open(m,'request');await m.locator('#invitation-request-create-reason').fill('Synthetic UI request <not markup>');
  await m.locator('#invitation-request-create-count').fill('2');await m.locator('#invitation-request-create-days').fill('3');
  await m.locator('#invitation-request-create-submit').click();await closed(m,'request');
  await m.waitForFunction(()=>document.querySelector('#invitation-management-requests-body')?.textContent.includes('Synthetic UI request <not markup>'));
  const request=(await api(m)).requests.find(r=>r.reason==='Synthetic UI request <not markup>');ok(!!request&&request.count===2&&request.days===3,'Request modal commits actual bounded count/days and closes after success');
  ok(await m.locator('#invitation-management-requests-body not').count()===0,'Request text is rendered as text, not injected HTML');
  for(const page of[a,m])await ready(page);
  const review=()=>a.getByRole('button',{name:'依頼 '+request.id+' を確認・判断',exact:true});await review().click();
  await a.locator('#invitation-review-dialog[open]').waitFor();
  ok((await a.locator('#invitation-request-review-summary').textContent()).includes('希望人数：2名'),'Review modal shows selected real request details');
  ok(await a.locator('#invitation-request-review-submit').isDisabled(),'Review requires existing explicit confirmation');
  await a.keyboard.press('Escape');await closed(a,'review');ok(await review().evaluate(e=>e===document.activeElement),'Review dismissal restores reconstructed row trigger');
  await review().click();await a.locator('#invitation-request-review-comment').fill('Synthetic approval comment');await a.locator('#invitation-request-review-confirm').check();await a.locator('#invitation-request-review-submit').click();await closed(a,'review');
  await ready(m);ok((await api(m)).requests.find(r=>r.id===request.id)?.status==='Approved','Review modal preserves actual server approval');
  const invitation=(await api(m)).invitations.find(i=>i.requestId===request.id);ok(!!invitation&&invitation.limit===2&&invitation.uses===0,'Approval creates actual owner-scoped invitation');
  await m.getByRole('button',{name:'招待 '+invitation.id+' の本人リンクを取得',exact:true}).click();await m.locator('#invitation-owner-link-result:not([hidden])').waitFor();
  ok((await m.locator('#invitation-owner-link-value').inputValue()).includes('/join#invite='),'Owner obtains link only through explicit temporary result');
  ok(!(JSON.stringify(await api(a))).includes('#invite='),'Admin ledger does not expose secret invitation links');
  await ready(a);await a.getByRole('button',{name:'招待 '+invitation.id+' を失効対象に選ぶ',exact:true}).click();await a.locator('#invitation-revoke-dialog[open]').waitFor();
  await a.locator('#invitation-management-reason').fill('Synthetic revoke draft');await a.locator('#invitation-revoke-close').click();await closed(a,'revoke');
  ok(!(await api(a)).invitations.find(i=>i.id===invitation.id).disabled,'Closing revoke modal performs no write');
  await a.getByRole('button',{name:'招待 '+invitation.id+' を失効対象に選ぶ',exact:true}).click();await a.locator('#invitation-management-reason').fill('Synthetic immediate revoke');await a.locator('#invitation-management-confirm').check();await a.locator('#invitation-management-revoke').click();await closed(a,'revoke');
  await ready(a);ok((await api(a)).invitations.find(i=>i.id===invitation.id).disabled,'Confirmed revoke persists independently of modal close and reload');
  await ready(m);ok(await m.locator('#invitation-owner-link-value').inputValue()==='','Reload clears owner temporary secret');
  await open(a,'issue');await a.locator('#invitation-issue-purpose').fill('Unsent responsive UI purpose');
  for(const mode of['light','dark'])for(const width of[320,390,768,1440]){
   await a.setViewportSize({width,height:844});await a.emulateMedia({colorScheme:mode});
   // Use actual theme attributes/variables; OS preference alone need not override saved appearance.
   await a.evaluate(mode=>{document.documentElement.dataset.appearanceMode=mode;document.documentElement.style.setProperty('--appearance-surface',mode==='dark'?'#17241f':'#fffefb');document.documentElement.style.setProperty('--appearance-text',mode==='dark'?'#e0eee6':'#19332e');},mode);
   await modalFits(a,'issue',width+' '+mode);
   ok(await a.locator('.invitation-pair').first().evaluate(e=>{const c=[...e.children].map(x=>x.getBoundingClientRect());return c.length===2&&Math.abs(c[0].y-c[1].y)<2&&c[0].right<c[1].left;}),'Count/day stay paired '+width+' '+mode);
   ok(await a.locator('#invitation-issue-purpose').inputValue()==='Unsent responsive UI purpose','Responsive changes preserve issue draft '+width+' '+mode);
   await m.setViewportSize({width,height:844});ok(await m.evaluate(()=>document.documentElement.scrollWidth<=innerWidth+1),'Owner cards avoid page overflow '+width+' '+mode);
   ok(await m.locator('#invitation-management-requests-body tr').first().evaluate(e=>getComputedStyle(e).display==='grid'),'Requests use adopted card groups '+width+' '+mode);
  }
  await a.setViewportSize({width:844,height:390});await modalFits(a,'issue','short landscape');await a.locator('#invitation-issue-close').click();await closed(a,'issue');
  ok((await api(a)).invitations.every(i=>i.purpose!=='Unsent responsive UI purpose'),'Closing responsive draft does not issue a link');
  await a.setViewportSize({width:1280,height:900});await open(a,'issue');await a.locator('#invitation-issue-purpose').fill('Synthetic direct issue');await a.locator('#invitation-issue-confirm').check();await a.locator('#invitation-issue-submit').click();await a.locator('#invitation-issue-result:not([hidden])').waitFor();
  ok((await a.locator('#invitation-issue-link').inputValue()).includes('/join#invite='),'Direct issue retains confirmed temporary link result in modal');
  await a.locator('#invitation-issue-close').click();await closed(a,'issue');ok(await a.locator('#invitation-issue-link').inputValue()==='','Closing issue modal clears temporary secret');
  await open(a,'issue');await a.locator('#invitation-issue-purpose').fill('Synthetic unknown result');await a.locator('#invitation-issue-confirm').check();let writes=0;
  const issueRoute=/\/api\/core\/invitations\/issue(?:\?|$)/;await a.route(issueRoute,route=>{writes++;return route.abort('failed');});await a.locator('#invitation-issue-submit').click();try{await closed(a,'issue');}catch(error){console.error('Invitation unknown diagnostic',JSON.stringify({writes,status:await a.locator('#invitation-management-status').textContent(),error:await a.locator('#invitation-issue-error').textContent(),submitDisabled:await a.locator('#invitation-issue-submit').isDisabled(),openDisabled:await a.locator('#invitation-issue-open').isDisabled()}));throw error;}
  await a.waitForFunction(()=>document.getElementById('invitation-issue-open').disabled&&document.getElementById('invitation-management-status').textContent.includes('未確認'));
  ok(writes===1&&await a.locator('#invitation-issue-open').isDisabled(),'Unknown response closes modal and locks write launchers without retry');
  const result=a.locator('.operation-result-dialog[open]');if(await result.count())await result.getByRole('button',{name:'結果を確認して閉じる'}).click();
  await a.unroute(issueRoute);await a.locator('#invitation-management-refresh').click();await a.waitForFunction(()=>document.getElementById('invitation-management-refresh').disabled===false&&document.getElementById('invitation-management-status').textContent.includes('一覧は更新'));ok(writes===1&&await a.locator('#invitation-issue-open').isDisabled(),'Explicit read-only refresh preserves unknown issue lock and never repeats the write');
  ok(errors.length===0,'Invitation modal/card UI has no client runtime errors');
 }finally{await a.close();await m.close();}
};
