import {createController} from './qr-camera-controller.js';
import {parseCode} from './qr-reception.js';
let current=null;
export function stopCamera(owner,reason='stopped'){if(current?.owner===owner){const c=current;current=null;c.controller.stop(reason);c.cleanup();}}
export async function startCamera(owner,videoId,eventId,sink,attempt){
 if(current)stopCamera(current.owner,'replaced');
 if(!navigator.mediaDevices?.getUserMedia){await sink.invokeMethodAsync('CameraStopped',attempt,'unavailable');return;}
 const video=document.getElementById(videoId);if(!video)return;
 const canvas=document.createElement('canvas'),ctx=canvas.getContext('2d',{willReadFrequently:true});let last=0;
 const notify=(method,value)=>sink.invokeMethodAsync(method,attempt,value).catch(()=>{});
 const controller=createController({media:navigator.mediaDevices,video,nextFrame:requestAnimationFrame,cancelFrame:cancelAnimationFrame,
  decode:async()=>{await import('./vendor/jsqr-1.4.0.js');const now=performance.now();if(now-last<250||!video.videoWidth||video.readyState<2)return null;last=now;
   const scale=Math.min(1,640/video.videoWidth,640/video.videoHeight);canvas.width=Math.max(1,Math.round(video.videoWidth*scale));canvas.height=Math.max(1,Math.round(video.videoHeight*scale));
   ctx.drawImage(video,0,0,canvas.width,canvas.height);const image=ctx.getImageData(0,0,canvas.width,canvas.height);
   return window.jsQR(image.data,image.width,image.height,{inversionAttempts:'attemptBoth'})?.data??null;},
  validate:x=>parseCode(x,window.location.origin,eventId),onCode:x=>notify('CameraCodeReady',x),onStop:reason=>{canvas.width=canvas.height=0;notify('CameraStopped',reason);}});
 const hidden=()=>{if(document.hidden)stopCamera(owner,'hidden');},leave=()=>stopCamera(owner,'pagehide');
 document.addEventListener('visibilitychange',hidden);window.addEventListener('pagehide',leave);
 const cleanup=()=>{document.removeEventListener('visibilitychange',hidden);window.removeEventListener('pagehide',leave);canvas.width=canvas.height=0;};
 current={owner,controller,cleanup};await controller.start();
}
