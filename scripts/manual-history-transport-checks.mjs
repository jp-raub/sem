import fs from 'node:fs/promises';
const source=await fs.readFile(new URL('../src/Sem.Web/wwwroot/manual-notification-history.js',import.meta.url),'utf8');
const {history}=await import('data:text/javascript;base64,'+Buffer.from(source).toString('base64'));
let passed=0;const check=(v,s)=>{if(!v)throw Error(s);passed++;console.log('PASS '+s)},reject=async(fn,s)=>{let threw=false;try{await fn()}catch{threw=true}check(threw,s)};
const original=globalThis.fetch,id='12345678-1234-1234-1234-123456789abc';
try{
 const calls=[];globalThis.fetch=async(url,options)=>{calls.push({url,options});return new Response('{"eventId":"event"}')};
 const value=await history('event & id','actor & id');check(value.status===200&&value.value.eventId==='event','read returns one snapshot');check(calls.length===1,'one GET with no CSRF/write');
 let q=new URL('https://localhost'+calls[0].url);check(q.pathname==='/api/core/manual-notification-history'&&q.searchParams.get('id')==='event & id'&&q.searchParams.get('actor')==='actor & id'&&!q.searchParams.has('beforeAt')&&!q.searchParams.has('beforeId'),'latest encodes current event actor and omits both cursor fields');check(calls[0].options.method==='GET'&&!calls[0].options.body&&!calls[0].options.headers,'never sends content or mutation body');check(calls[0].options.cache==='no-store'&&calls[0].options.redirect==='error'&&calls[0].options.signal instanceof AbortSignal,'private read refuses caches/redirects and is abortable');
 await history('e','a',{acceptedAt:1700000000,requestId:id});q=new URL('https://localhost'+calls[1].url);check(q.searchParams.get('beforeAt')==='1700000000'&&q.searchParams.get('beforeId')===id,'continuation encodes exact joint cursor');
 for(const code of [400,403]){let n=0;globalThis.fetch=async()=>{n++;return new Response('{}',{status:code})};const x=await history('e','a');check(x.status===code&&x.value===null&&n===1,'read rejection '+code+' returns no cached view and never retries');}
 for(const code of [429,500,502]){let n=0;globalThis.fetch=async()=>{n++;return new Response('{}',{status:code})};await reject(()=>history('e','a'),'HTTP '+code+' cannot invent history');check(n===1,'HTTP '+code+' never retries');}
 let n=0;globalThis.fetch=async()=>{n++;throw Error('disconnect')};await reject(()=>history('e','a'),'lost read does not invent history');check(n===1,'lost read makes no automatic retry or mutation');
 globalThis.fetch=async()=>new Response('invalid');await reject(()=>history('e','a'),'malformed success refused');
 const invalid=[['','a'],['e',''],[null,'a'],['e',null],['e','a',{acceptedAt:-1,requestId:id}],['e','a',{acceptedAt:1.2,requestId:id}],['e','a',{acceptedAt:253402300800,requestId:id}],['e','a',{acceptedAt:1,requestId:id.toUpperCase()}],['e','a',{acceptedAt:1,requestId:'bad'}],['e','a',{requestId:id}],['e','a',{}]];
 n=0;globalThis.fetch=async()=>{n++;return new Response('{}')};for(const args of invalid)await reject(()=>history(...args),'invalid context refused '+JSON.stringify(args));check(n===0,'invalid context creates no request');check(!/localStorage|sessionStorage|console\.|setInterval/.test(source),'no storage logging or polling');
 console.log(`Manual history transport: ${passed} checks passed.`);
}finally{globalThis.fetch=original}
