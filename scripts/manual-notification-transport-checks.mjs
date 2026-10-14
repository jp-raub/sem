import fs from 'node:fs/promises';
const source=await fs.readFile(new URL('../src/Sem.Web/wwwroot/manual-notification.js',import.meta.url),'utf8');
const {post}=await import('data:text/javascript;base64,'+Buffer.from(source).toString('base64'));
let passed=0;
const check=(v,s)=>{if(!v)throw Error(s);passed++;console.log('PASS '+s)};
const reject=async(fn,s)=>{let denied=false;try{await fn()}catch{denied=true}check(denied,s)};
const originalFetch=globalThis.fetch;
try {
 const calls=[];
 globalThis.fetch=async(url,options)=>{calls.push({url,options});return calls.length===1?new Response('{"token":"synthetic-csrf"}') : new Response('{"requestId":"synthetic-operation"}')};
 const payload={requestId:'synthetic-operation',subject:'<img>',body:'line\n2',confirmed:true};
 const accepted=await post('manual-notification-accept','event & id','actor id',payload);
 check(accepted.status===200&&accepted.value.requestId===payload.requestId,'success returns application result without interpreting payload');
 check(calls.length===2,'one CSRF read and exactly one explicit POST; no automatic retry');
 const sent=calls[1];
 check(sent.options.method==='POST'&&sent.options.cache==='no-store'&&sent.options.redirect==='error','POST uses no-store and refuses redirects');
 check(sent.options.headers['X-CSRF-TOKEN']==='synthetic-csrf'&&JSON.parse(sent.options.body).body===payload.body,'CSRF and frozen multiline plain text passed unchanged');
 check(sent.url.includes('id=event+%26+id')&&sent.url.includes('actor=actor+id')&&!sent.url.includes('subject'),'URL contains only encoded context, never draft');
 await reject(()=>post('arbitrary','e','a',{}),'only explicitly supported endpoint names allowed');
 for(const status of [400,403,409]) {
  let n=0;globalThis.fetch=async()=>++n===1?new Response('{"token":"csrf"}'):new Response('{"error":"application rejection"}',{status});
  const result=await post('manual-notification-preview','e','a',{});
  check(result.status===status&&result.value===null&&n===2,'explicit HTTP '+status+' classified without replay');
 }
 for(const status of [429,500,502]) {
  let n=0;globalThis.fetch=async()=>++n===1?new Response('{"token":"csrf"}'):new Response('{}',{status});
  await reject(()=>post('manual-notification-accept','e','a',payload),'HTTP '+status+' remains uncertain');check(n===2,'HTTP '+status+' never automatically retries');
 }
 let n=0;globalThis.fetch=async()=>{n++;throw Error('synthetic disconnect')};
 await reject(()=>post('manual-notification-accept','e','a',payload),'network failure remains uncertain');check(n===1,'network failure never automatically retries');
 n=0;globalThis.fetch=async()=>++n===1?new Response('{"token":"csrf"}'):new Response('not JSON');
 await reject(()=>post('manual-notification-accept','e','a',payload),'malformed success remains uncertain');
 n=0;globalThis.fetch=async()=>{n++;return new Response('{}')};
 await reject(()=>post('manual-notification-accept','e','a',payload),'missing CSRF token refuses submission');check(n===1,'missing CSRF never issues acceptance');
 check(!/localStorage|sessionStorage|console\.|setInterval/.test(source),'transport has no browser storage, logs or background loop');
 console.log(`Manual notification transport: ${passed} checks passed.`);
}finally{globalThis.fetch=originalFetch}
