module.exports=async({p,g,ok})=>{
 const api=(page,action,q='',body)=>page.evaluate(async({action,q,body})=>{let opts;if(body!==undefined){const{token}=await(await fetch('/api/csrf')).json();opts={method:'POST',headers:{'Content-Type':'application/json','X-CSRF-TOKEN':token},body:JSON.stringify(body)};}const r=await fetch('/api/core/'+action+q,opts);return{status:r.status,cache:r.headers.get('cache-control'),body:await r.json()};},{action,q,body});
 const uuid=()=>require('node:crypto').randomUUID(),event=(await api(p,'workspace')).body.events.find(x=>x.title==='Profile-link native fixture'),q='?id='+event.id;
 let code=(await api(g,'event-qr',q)).body,read=(await api(p,'event-qr-read',q,{requestId:uuid(),token:code.token})).body;
 const tq=q+'&user='+encodeURIComponent(code.userId);let target=(await api(p,'reception-target',tq)).body;
 const edit=()=>({requestId:uuid(),userId:code.userId,readId:read.readId,token:code.token,tokenVersion:code.tokenVersion,eventVersion:target.eventVersion,participantVersion:target.participantVersion,receptionVersion:target.receptionVersion,receptionDone:true,note:'native QR confirmed'});
 ok((await api(g,'qr-reception',q,edit())).status===403,'Member cannot complete QR reception through native API');
 ok((await api(p,'qr-reception',q,{...edit(),receptionDone:false})).status===400,'Native QR reception needs explicit identity/action confirmation');
 ok((await api(p,'qr-reception',q,{...edit(),readId:uuid()})).status===409,'Native QR completion requires actual successful matching read');
 ok((await api(p,'qr-reception',q,{...edit(),unexpected:true})).status===400,'Native QR completion rejects unknown payload fields');
 const stale=edit();await api(g,'event-qr',q,{requestId:uuid(),userId:null,tokenVersion:code.tokenVersion,reissue:true});
 ok((await api(p,'qr-reception',q,stale)).status===409&&(await api(p,'reception-target',tq)).body.current===null,'Reissue between read and confirmation rejects stale QR without receipt');
 code=(await api(g,'event-qr',q)).body;read=(await api(p,'event-qr-read',q,{requestId:uuid(),token:code.token})).body;target=(await api(p,'reception-target',tq)).body;const saved=edit();
 ok((await api(p,'qr-reception',q,saved)).status===200,'Native current read and explicit version confirmation complete QR receipt');
 target=(await api(p,'reception-target',tq)).body;
 ok(target.current.method==='QR'&&target.current.readId===read.readId&&target.current.note===saved.note&&target.current.actorId&&target.current.at,'Native QR receipt returns linked read actor time and note');
 ok((await api(p,'qr-reception',q,saved)).body.replayed===true,'Native identical QR receipt retry creates no duplicate');
 ok((await api(p,'qr-reception',q,{...saved,note:'changed'})).status===400,'Native changed QR confirmation cannot reuse operation ID');
 ok((await api(p,'qr-reception',q,edit())).status===409,'Native active receipt prevents duplicate QR completion');
 const a=await p.context().newPage();try{await a.goto('https://localhost:18443/events/'+event.id+'/reception');await a.locator('[data-reception-select]').click();await a.locator('#reception-current').waitFor();ok((await a.locator('#reception-current').innerText()).includes('QR'),'Existing reception view labels QR receipt accurately');}finally{await a.close();}
 await api(p,'reception-cancel',q,{requestId:uuid(),userId:code.userId,receptionId:target.current.id,receptionVersion:target.receptionVersion,confirmed:true});
 ok((await api(p,'qr-reception',q,saved)).body.replayed===true&&(await api(p,'reception-target',tq)).body.current===null,'Native historical QR receipt retry does not resurrect cancelled receipt');
 const history=(await api(p,'event-qr-history',q)).body;ok(history.reads.some(x=>x.id===read.readId)&&!JSON.stringify(history).includes(code.token),'Cancelling QR reception retains original safe read history');
};
