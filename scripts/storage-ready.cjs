module.exports=async()=>{
 for(let attempt=0;attempt<60;attempt++){
  try{
   const response=await fetch('http://127.0.0.1:18080/health/ready',{signal:AbortSignal.timeout(2000)});
   if(response.ok&&(await response.json()).status==='storage-ready')return;
  }catch{}
  await new Promise(resolve=>setTimeout(resolve,1000));
 }
 throw Error('Application did not report storage-ready after restart; browser acceptance was not attempted.');
};