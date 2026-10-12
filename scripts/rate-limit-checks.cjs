'use strict';
const fs=require('fs');
(async()=>{
 const base=process.env.SEM_RATE_TEST_URL||'http://localhost:20100';
 const headers={host:'localhost:18443','x-forwarded-proto':'https','x-forwarded-for':'127.0.0.1'};
 const checks=[];const ok=(value,label)=>{if(!value)throw Error(label);checks.push(label)};
 async function get(path){const r=await fetch(base+path,{headers});const text=await r.text();return {status:r.status,text}}
 for(let n=0;n<60;n++)ok((await get('/api/csrf')).status===200,'API permitted request '+(n+1));
 ok((await get('/api/csrf')).status===429,'API request 61 is limited');
 const page=await get('/workspace');ok(page.status===200,'API saturation does not block workspace HTML');
 const script=page.text.match(/src="([^"]*_framework\/blazor[^\"]+\.js)"/);
 ok(!!script && (await get('/'+script[1].replace(/^\//,''))).status===200,'API saturation does not block the real fingerprinted Blazor script');
 for(let n=0;n<178;n++)ok((await get('/manifest.webmanifest')).status===200,'Web permitted request '+(n+3));
 ok((await get('/manifest.webmanifest')).status===429,'Web request 181 is independently limited');
 ok((await get('/api/csrf')).status===429,'Web traffic never resets the exhausted API partition');
 fs.mkdirSync('verification',{recursive:true});fs.writeFileSync('verification/rate-limit-result.json',JSON.stringify({passed:checks.length,checks},null,2));
 console.log('Rate partition checks passed:',checks.length);
})().catch(e=>{console.error(e.message);process.exit(1)});
