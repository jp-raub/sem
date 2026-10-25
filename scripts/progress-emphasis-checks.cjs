// Synthetic current-day events; no product clock override and no production data.
module.exports=async({p,g,ok})=>{
 const uuid=()=>require('crypto').randomUUID();
 const api=(action,data,q='')=>p.evaluate(async({action,data,q})=>{const{token}=await(await fetch('/api/csrf')).json();const r=await fetch('/api/core/'+action+q,{method:data===undefined?'GET':'POST',headers:data===undefined?{}:{'Content-Type':'application/json','X-CSRF-TOKEN':token},body:data===undefined?undefined:JSON.stringify(data)});return{status:r.status,body:await r.json()};},{action,data,q});
 const a=await p.context().newPage(),errors=[];a.on('pageerror',e=>errors.push(e.message));
 try{
  for(const [seconds,level]of[[10,'low'],[120,'medium'],[360,'high']]){
   const ev=await api('event',{title:'Delay '+level,description:'',location:'',scheduledDate:new Date(Date.now()-seconds*1000).toISOString(),opensAt:null,announcementMessage:'',phaseKind:'View',managerIds:[],rowVersion:0});if(ev.status!==200)throw Error('Create delay fixture '+ev.status);const id=ev.body.id,q='?id='+id;
   const plan=(await api('timetable',undefined,q)).body,part={id:uuid(),kind:'Transition',songId:null,name:'Long transition title '+level+' '.repeat(2)+'予実表示の長いタイトルを省略しても全文へアクセスできることを確認します',durationSeconds:600,icon:null};
   const saved=await api('timetable',{rowVersion:plan.rowVersion,roundStarts:false,clockFormat:plan.clockFormat,durationFormat:plan.durationFormat,songIcon:plan.songIcon,plannedEndAt:null,parts:[part]},q);if(saved.status!==200)throw Error('Prepare delay fixture '+saved.status);
   await a.goto('https://localhost:18443/events/'+id+'/progress');await a.locator('#progress-refresh[data-interactive=true]').waitFor();
   if(!await a.locator('#progress-comparison-region').isVisible())await a.locator('#progress-show-plan').click();
   const target=a.locator('[data-progress-point="'+part.id+':Start"]');
   ok(await target.getAttribute('data-delay-level')===level,'Live overdue unrecorded current point has '+level+' warning severity');
   ok((await target.locator('.record-delay').innerText()).includes('遅れ')&&await a.locator('#progress-current-alert').isVisible(),'Current '+level+' delay includes explicit readable warning, not only color');
   ok(await a.locator('[data-progress-point="'+part.id+':End"]').getAttribute('data-delay-level')==='none','Future on-plan point returns to normal while current delay stays visible '+level);
   ok(await a.locator('#progress-health').getAttribute('data-delay-level')===level,'Audience summary uses existing current-delay calculation '+level);
   ok(await a.locator('.comparison-part>strong').getAttribute('title')===part.name,'Truncated part title retains full tooltip '+level);
   for(const mode of['light','dark']){
    await a.evaluate(mode=>document.documentElement.dataset.appearanceMode=mode,mode);
    const color=await target.locator('.latest-plan').evaluate(e=>({bg:getComputedStyle(e).backgroundColor,fg:getComputedStyle(e).color}));
    const expected={light:{low:['rgb(255, 242, 214)','rgb(108, 67, 0)'],medium:['rgb(255, 214, 161)','rgb(106, 48, 0)'],high:['rgb(182, 72, 48)','rgb(255, 255, 255)']},dark:{low:['rgb(77, 59, 37)','rgb(255, 224, 160)'],medium:['rgb(117, 72, 39)','rgb(255, 223, 183)'],high:['rgb(155, 56, 42)','rgb(255, 255, 255)']}}[mode][level];
    ok(color.bg===expected[0]&&color.fg===expected[1],'Actual '+mode+' scoped warning colors match adopted contrast pair '+level);
   }
  }
  ok(errors.length===0,'Delay emphasis flows have no browser runtime errors');
 }finally{await a.close();}
};
