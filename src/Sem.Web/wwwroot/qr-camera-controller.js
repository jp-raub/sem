export function createController({media,video,nextFrame,cancelFrame,decode,validate,onCode,onStop}) {
 let generation=0,stream=null,frame=null,active=false;
 const stopTracks=s=>s?.getTracks().forEach(t=>t.stop());
 function stop(reason='stopped'){generation++;active=false;if(frame!==null)cancelFrame(frame);frame=null;stopTracks(stream);stream=null;video.srcObject=null;onStop(reason);}
 async function start(){stop('replaced');const own=++generation;let acquired;
  try{acquired=await media.getUserMedia({audio:false,video:{facingMode:{ideal:'environment'}}});}catch{if(own===generation)stop('permission-or-device-error');return;}
  if(own!==generation){stopTracks(acquired);return;}stream=acquired;video.srcObject=stream;
  try{await video.play();}catch{if(own===generation)stop('play-error');return;}
  if(own!==generation)return;active=true;
  async function tick(){if(own!==generation||!active)return;let result;
   try{result=await decode(video);}catch{if(own===generation)stop('decode-error');return;}
   if(own!==generation||!active)return;const code=typeof result==='string'?validate(result):null;
   if(code){stop('code-ready');onCode(code);return;}frame=nextFrame(tick);
  }frame=nextFrame(tick);
 }
 return {start,stop,get active(){return active;}};
}
