module.exports=async({p,g,ok})=>{
 const random=()=>require('crypto').randomUUID();
 const api=(page,action,data,q='')=>page.evaluate(async({action,data,q})=>{
  const {token}=await(await fetch('/api/csrf')).json();const r=await fetch('/api/core/'+action+q,{method:data===undefined?'GET':'POST',headers:data===undefined?{}:{'Content-Type':'application/json','X-CSRF-TOKEN':token},body:data===undefined?undefined:JSON.stringify(data)});return{status:r.status,body:await r.json()};
 },{action,data,q});
 const create=await api(p,'event',{title:'Native business email',description:'',location:'',scheduledDate:new Date(Date.now()+86400000).toISOString(),opensAt:null,announcementMessage:'',phaseKind:'Hidden',managerIds:[],rowVersion:0});
 ok(create.status===200,'Business mail native event created');const q='?id='+create.body.id;
 let defs=(await api(p,'definitions',undefined,q)).body;const voice={id:random(),name:'Email Voice',category:'Sing'},guitar={id:random(),name:'Email Guitar',category:'Play'};
 defs=(await api(p,'definitions',{rowVersion:defs.rowVersion,definitions:[voice,guitar]},q)).body;
 let songs=(await api(p,'songs',undefined,q)).body;const rows=['Email Song A','Email Song B'].map(title=>({id:random(),title,artist:'',performanceSeconds:180,prepSeconds:0,definitionIds:[voice.id,guitar.id]}));songs=(await api(p,'songs',{rowVersion:songs.rowVersion,defaultPrepSeconds:0,songs:rows},q)).body;
 let phase=(await api(p,'phases',undefined,q)).body;const custom={id:random(),name:'Business mail open',maxPicks:-1,activateAt:null,deactivateAt:null};await api(p,'phases',{rowVersion:phase.rowVersion,manualOverride:true,activePhaseId:custom.id,customPhases:[custom]},q);
 const read=async(page,subject)=>{const r=await api(page,'participation',undefined,q+(subject?'&subject='+encodeURIComponent(subject):''));if(r.status!==200)throw Error('Business participant '+r.status);return r.body;};
 const draft=(plan,kind,slots,subjectId=null,reason='')=>({requestId:random(),rowVersion:plan.rowVersion,phaseId:plan.phaseId,kind,slots:slots.map(s=>({id:s.id,rowVersion:s.rowVersion})),answers:[],subjectId,reason,compatibilityException:false,participantVersion:plan.participantVersion});
 const mailId=id=>'<'+require('crypto').createHash('sha256').update('business-email:'+id).digest('hex').toUpperCase()+'@notifications.sem.invalid>';
 const mails=async()=>await(await fetch('http://127.0.0.1:18025')).json();
 async function received(id){for(let n=0;n<40;n++){const found=(await mails()).filter(x=>x.message_id===mailId(id));if(found.length)return found;await new Promise(r=>setTimeout(r,500));}throw Error('Business SMTP message not received');}
 let member=await read(g);const affected=member.ownUserId;const chosen=member.slots.filter(s=>s.songId===rows[0].id);const first=draft(member,'Register',chosen);const r=await api(g,'participation',first,q);
 ok(r.status===200&&r.body.succeeded===2,'Native registration commits before SMTP reception');const email=(await received(first.requestId));
 ok(email.length===1&&email[0].subject==='演奏パート登録のお知らせ'&&email[0].body.includes('Native business email')&&email[0].body.includes('Email Song A')&&email[0].body.includes('Email Voice')&&email[0].body.includes('Email Guitar'),'Actual SMTP receives one consolidated successful-slot email');
 ok(email[0].body.includes('+09:00')&&email[0].body.includes('日本時間')&&email[0].from.includes('fixture@example.test'),'Business SMTP preserves configured sender and absolute Japanese schedule');
 const replay=await api(g,'participation',first,q);ok(replay.status===200&&replay.body.replayed,'Native replay returns original saved registration result');
 let admin=await read(p);const own=draft(admin,'Register',[admin.slots.find(s=>s.id===chosen[0].id),admin.slots.find(s=>s.songId===rows[1].id&&s.category==='Sing')]);const partial=await api(p,'participation',own,q);
 ok(partial.status===200&&partial.body.succeeded===1,'Native partial-success operation confirms one new slot');const partialMail=(await received(own.requestId))[0];
 ok(partialMail.body.includes('Email Song B')&&!partialMail.body.includes('Email Song A'),'Actual partial-success mail excludes failed occupied song');
 member=await read(g);const noSuccess=draft(member,'Register',[member.slots.find(s=>s.id===chosen[0].id)]);ok((await api(g,'participation',noSuccess,q)).body.succeeded===0,'Native occupied-only operation has zero successes');
 const forged=draft(member,'Release',chosen,null,'forged');ok((await api(g,'participation',forged,q)).status===403,'Native member cannot trigger release notification');
 admin=await read(p,affected);const release=draft(admin,'Release',chosen.map(s=>admin.slots.find(x=>x.id===s.id)),affected,'Native performer request');
 ok((await api(p,'participation',release,q)).body.succeeded===2,'Native manager commits two-slot release');const releaseMail=(await received(release.requestId))[0];
 ok(releaseMail.subject==='演奏パート登録解除のお知らせ'&&releaseMail.body.includes('Native performer request')&&releaseMail.to===email[0].to,'Actual release email includes reason and goes to performer, not proxy actor');
 if(process.env.SEM_PHASE_RESTART_COMMAND){require('child_process').execSync(process.env.SEM_PHASE_RESTART_COMMAND,{stdio:'inherit'});await require('./storage-ready.cjs')();await p.goto('https://localhost:18443/');await p.waitForSelector('#issue');await g.goto('https://localhost:18443/');await g.waitForSelector('#request');}
 await new Promise(r=>setTimeout(r,3500));const final=await mails();
 ok(final.filter(x=>x.message_id===mailId(first.requestId)).length===1&&final.filter(x=>x.message_id===mailId(release.requestId)).length===1,'Replay and application restart do not resend sent business SMTP messages');
 ok(!final.some(x=>x.message_id===mailId(noSuccess.requestId)||x.message_id===mailId(forged.requestId)),'Failed and unauthorized operations never reach SMTP');
 member=await read(g);ok(chosen.every(s=>member.slots.find(x=>x.id===s.id).assignedUserId===null),'Restart retains released business state independently of SMTP status');
};
