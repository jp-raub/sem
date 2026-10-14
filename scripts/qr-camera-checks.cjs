module.exports=async({p,g,ok})=>{
 const api=(page,action,q='')=>page.evaluate(async({action,q})=>{const r=await fetch('/api/core/'+action+q);return{status:r.status,body:await r.json()};},{action,q});
 const event=(await api(p,'workspace')).body.events.find(x=>x.title==='Profile-link native fixture'),q='?id='+event.id,code=(await api(g,'event-qr',q)).body,url='https://localhost:18443/events/'+event.id+'/reception';
 const a=await p.context().newPage(),errors=[];a.on('pageerror',e=>errors.push(e.message));try{
  await a.goto(url);await a.locator('#qr-camera-start').waitFor();
  await a.evaluate(()=>{window.__qrCalls=0;navigator.mediaDevices.getUserMedia=async()=>{window.__qrCalls++;throw Error('synthetic denial')};});
  let release,seen;const gate=new Promise(r=>release=r),entered=new Promise(r=>seen=r);await a.route('**/qr-camera.js',async route=>{seen();await gate;await route.continue();});
  await a.locator('#qr-camera-start').click();await Promise.race([entered,new Promise((_,r)=>setTimeout(()=>r(Error('Module import was not requested')),10000))]);await a.locator('#qr-camera-stop').click();release();await a.waitForTimeout(1500);
  ok(await a.evaluate(()=>window.__qrCalls===0)&&await a.locator('#qr-camera-start').isEnabled(),'Stop while camera module loads prevents later permission request');await a.unroute('**/qr-camera.js');
  await a.locator('#qr-camera-start').click();await a.waitForFunction(()=>document.querySelector('#qr-reader-error').textContent.includes('カメラ'));ok(await a.locator('#qr-camera-start').isEnabled(),'Synthetic camera denial restores controls and paste fallback');
  await a.evaluate(async({id,code})=>{
   const response=await fetch('/api/core/event-qr-image?id='+encodeURIComponent(id)+'&version='+code.tokenVersion+'&user='+encodeURIComponent(code.userId));if(!response.ok)throw Error('Synthetic image fixture unavailable');
   const image=await createImageBitmap(await response.blob()),canvas=document.createElement('canvas');canvas.width=image.width;canvas.height=image.height;canvas.getContext('2d').drawImage(image,0,0);
   window.__qrCanvas=canvas;window.__qrImage=image;
   navigator.mediaDevices.getUserMedia=async()=>{const stream=canvas.captureStream(15),track=stream.getVideoTracks()[0];window.__qrSyntheticTrack=track;
    const timer=setInterval(()=>{if(track.readyState==='ended'){clearInterval(timer);return;}canvas.getContext('2d').drawImage(image,0,0);track.requestFrame?.();},80);track.requestFrame?.();return stream;};
  },{id:event.id,code});
  const before=(await api(p,'event-qr-history',q)).body.reads.length;await a.locator('#qr-camera-start').click();
  try{await a.waitForFunction(token=>document.querySelector('#qr-reader-input').value===token,code.token,{timeout:20000});}catch(e){console.log('Camera fixture diagnostics',await a.evaluate(()=>{const v=document.querySelector('#qr-camera-video');return {readyState:v.readyState,paused:v.paused,width:v.videoWidth,height:v.videoHeight,hasStream:!!v.srcObject,track:window.__qrSyntheticTrack?.readyState,decoder:typeof window.jsQR,hidden:document.hidden,hasInput:!!document.querySelector('#qr-reader-input').value,error:document.querySelector('#qr-reader-error').textContent};}));throw e;}
  ok(await a.evaluate(()=>window.__qrSyntheticTrack.readyState==='ended'&&document.querySelector('#qr-camera-video').srcObject===null),'Real local jsQR decodes synthetic camera frames and stops acquired track');
  ok((await api(p,'event-qr-history',q)).body.reads.length===before&&!(await a.locator('body').innerText()).includes(code.token),'Decoded camera code stays private without automatic read or receipt');
  await a.locator('#qr-reader-read').click();await a.locator('#qr-reader-target').waitFor();ok((await api(p,'event-qr-history',q)).body.reads.length===before+1&&await a.locator('#qr-reader-complete').isDisabled(),'Camera result requires explicit read and separate reception confirmation');
  await a.evaluate(()=>{navigator.mediaDevices.getUserMedia=()=>new Promise(r=>window.__qrLateGrant=()=>{const s=window.__qrCanvas.captureStream(15);window.__qrLateTrack=s.getVideoTracks()[0];r(s);});});await a.locator('#qr-camera-start').click();await a.waitForFunction(()=>typeof window.__qrLateGrant==='function');await a.locator('#qr-camera-stop').click();await a.evaluate(()=>window.__qrLateGrant());await a.waitForFunction(()=>window.__qrLateTrack?.readyState==='ended');
  ok(await a.locator('#qr-camera-start').isEnabled()&&(await api(p,'event-qr-history',q)).body.reads.length===before+1,'Permission granted after stop is discarded with track stopped and no write');
  ok(errors.length===0,'Camera lifecycle and explicit QR UI have no runtime errors');
 }finally{await a.close();}
};
