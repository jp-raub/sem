module.exports = async ({p,g,ok}) => {
 // Previous native-auth checks share the same IP rate-limit window. Do not weaken production limits.
 await new Promise(r=>setTimeout(r,61000));
 const api = (page, action, data, query='') => page.evaluate(async ({action,data,query}) => {
  const token = (await (await fetch('/api/csrf')).json()).token;
  const response = await fetch('/api/core/'+action+query, { method:data===undefined?'GET':'POST',
   headers:data===undefined?{}:{'Content-Type':'application/json','X-CSRF-TOKEN':token}, body:data===undefined?undefined:JSON.stringify(data) });
  return {status:response.status, body:await response.json()};
 }, {action,data,query});
 const a = (await api(p,'workspace')).body, m = (await api(g,'workspace')).body;
 ok(a.isAdmin && !m.isAdmin && a.profile.userId!==m.profile.userId,'Core uses existing administrator/member identities');
 ok((await api(g,'service',{name:'Denied',description:'',rowVersion:0})).status===403,'Ordinary member cannot edit global service settings');
 const svc = {name:'SEM Core Acceptance',description:'Shared description',rowVersion:a.service.rowVersion};
 ok((await api(p,'service',svc)).status===200 && (await api(g,'workspace')).body.service.name===svc.name,'Service save is shared without resetting bootstrap or authentication');
 ok((await api(p,'service',{...svc,name:'Stale service'})).status===409,'Stale service save is rejected');
 const profile = {displayName:'参加者更新',reading:'ライオン',callName:'ライオン',social:[{service:'X',url:'https://example.test/member'}],rowVersion:m.profile.rowVersion};
 ok((await api(g,'profile',profile)).status===200,'Member edits only own basic profile');
 const publishedProfile = (await api(p,'profile',undefined,'?id='+m.profile.userId)).body;
 ok(publishedProfile.reading==='らいおん' && publishedProfile.callName==='ライオン' && !('email' in publishedProfile),'Member-readable profile normalizes reading and excludes authentication email');
 ok((await api(g,'profile',{...profile,userId:a.profile.userId,rowVersion:1})).status===400 && (await api(p,'workspace')).body.profile.displayName===a.profile.displayName,'Forged profile target is rejected rather than editing another user');
 ok((await api(g,'profile',{...profile,social:[{service:'bad',url:'javascript:alert(1)'}],rowVersion:1})).status===400,'Non-HTTP profile social link is rejected');
 const event = {title:'Core multi-manager event',description:'Overview',location:'Venue',scheduledDate:'2026-10-20T13:00:00+09:00',opensAt:'2026-10-20T13:00:00+09:00',announcementMessage:'Notice',phaseKind:'Hidden',managerIds:[a.profile.userId,m.profile.userId],rowVersion:0};
 ok((await api(g,'event',event)).status===403,'Event creation remains global-admin only');
 const created = await api(p,'event',event);ok(created.status===200,'Formal event creation supports two managers and opening equal to start');
 const other = await api(p,'event',{...event,title:'Other hidden event',managerIds:[]});
 const mv = (await api(g,'workspace')).body;
 ok(mv.events.length===1 && mv.events[0].id===created.body.id && mv.events[0].canManage && mv.events[0].scheduledDate==='2026-10-20T04:00:00.000Z','Manager sees only assigned hidden event and canonical UTC dates');
 ok((await api(g,'event',{...event,rowVersion:1},'?id='+other.body.id)).status===403,'Event manager cannot edit a different event');
 ok((await api(g,'event',{...event,phaseKind:'Custom',rowVersion:1},'?id='+created.body.id)).status===400,'Unimplemented enrollment/custom phase is not exposed as working');
 const ambiguous = {...event,scheduledDate:'2026-10-20T13:00:00',opensAt:null,rowVersion:1};
 ok((await api(p,'event',ambiguous,'?id='+created.body.id)).status===400,'API rejects date-times without an explicit timezone');
 const upper = {...ambiguous};delete upper.scheduledDate;upper.ScheduledDate='2026-10-20T13:00:00';
 ok((await api(p,'event',upper,'?id='+created.body.id)).status===400,'Timezone validation cannot be bypassed by property-name casing');
 ok((await api(g,'event',{...event,title:'Manager updated overview',rowVersion:1},'?id='+created.body.id)).status===200,'Assigned manager can save assigned event without global permission');
 ok((await api(p,'event',{...event,rowVersion:1},'?id='+created.body.id)).status===409,'Event and manager draft share optimistic conflict detection');
 const csrfMissing = await g.evaluate(async()=> (await fetch('/api/core/profile',{method:'POST',headers:{'Content-Type':'application/json'},body:JSON.stringify({displayName:'No CSRF',reading:'',callName:'',social:[],rowVersion:1})})).status);
 ok(csrfMissing===400,'Core HTTP mutation requires antiforgery token');
 const w = await p.context().newPage();const browserErrors=[];w.on('pageerror',e=>browserErrors.push(e.message)); w.on('console',m=>{if(m.type()==='error')console.error('Blazor browser:',m.text().replace(/\?[^\s]+/g,'?REDACTED').slice(0,300))});
 try {
  const response = await w.goto('https://localhost:18443/workspace');ok(response.headers()['cache-control']?.includes('no-store'),'Private workspace HTML is not cached');await w.waitForSelector('#service-edit');await w.waitForTimeout(4000);
  for(let n=0;n<8 && !await w.locator('#service-save').count();n++){await w.locator('#service-edit').click();await w.waitForTimeout(350)}
  await w.waitForSelector('#service-save');await w.locator('#service-name').fill('Unsaved preview');
  await w.waitForFunction(()=>document.querySelector('#core-title').textContent==='Unsaved preview');
  ok((await api(g,'workspace')).body.service.name===svc.name,'Blazor service preview is a local draft, not a shared write');
  await w.locator('#core-service button[type=button]').click();
  await w.waitForSelector('#service-edit');ok((await api(p,'workspace')).body.service.name===svc.name,'Blazor cancel discards service draft without persistence');
  await w.locator('#profile-edit').click();await w.waitForSelector('#profile-save');await w.locator('#profile-name').fill('管理者更新');
  await w.locator('#profile-reading').fill('カンリシャ');await w.locator('#profile-save').click();
  await w.waitForSelector('#profile-edit');ok((await api(p,'workspace')).body.profile.reading==='かんりしゃ','Interactive Blazor profile save uses the same validated service');
  await w.locator('#event-new').click();await w.waitForSelector('#event-core-save');
  await w.locator('#event-title').fill('Blazor native event');await w.locator('#event-starts').fill('2026-10-22T13:00');
  await w.locator('#event-opens').fill('2026-10-22T13:00');await w.locator('#event-core-save').click();
  await w.waitForSelector('#event-core-form',{state:'detached'});
  ok((await api(p,'workspace')).body.events.some(e=>e.title==='Blazor native event'),'Interactive Blazor creates and persists a formal event');
  await w.locator('#profile-edit').click();await w.waitForSelector('#profile-save');await w.locator('#profile-name').fill('After logout must not save');
  // Allow the UI asset/navigation burst to leave the shared IP rate-limit window.
  await w.waitForTimeout(61000);
  const logoutResponse = p.waitForResponse(r=>new URL(r.url()).pathname==='/api/logout');
  await p.locator('[data-action=logout]').click();
  const logout = await logoutResponse;ok(logout.status()===200,'Native logout succeeds with a still-connected Blazor circuit');
  await p.waitForSelector('#login');
  await w.locator('#profile-save').click();await w.waitForFunction(()=>document.querySelector('#core-error').textContent.includes('再ログイン'));
  ok(await w.locator('#core-account').count()===0,'Still-connected Blazor circuit denies logged-out session and clears displayed account data');
  ok((await api(p,'workspace')).status===403,'Logged-out HTTP workspace is denied');
  await p.locator('#login [name=email]').fill('admin@example.test');await p.locator('#login button').click();await p.waitForSelector('#issue');
  ok((await api(p,'workspace')).body.profile.displayName==='管理者更新','Denied circuit write did not change persisted profile and native key still logs in');
  ok(browserErrors.length===0,'No client runtime errors in the Blazor foundation');
 } finally { await w.close(); }
 const current = (await api(p,'workspace')).body.events.find(e=>e.id===created.body.id);
 ok((await api(p,'event',{...event,managerIds:[],phaseKind:'View',rowVersion:current.rowVersion},'?id='+current.id)).status===200,'Global admin can remove all event managers and allow viewing');
 const guestAfter = (await api(g,'workspace')).body.events.find(e=>e.id===current.id);
 ok(guestAfter && !guestAfter.canManage && !(await api(g,'workspace')).body.isAdmin,'Public viewing and event assignment never grant global or residual manager rights');
 ok((await api(g,'event',{...event,rowVersion:guestAfter.rowVersion},'?id='+current.id)).status===403,'Removed event manager cannot write through the old authenticated browser');
};
