import fs from 'node:fs/promises';
const source=await fs.readFile(new URL('../src/Sem.Web/wwwroot/manual-push-status.js',import.meta.url),'utf8');
const {status}=await import('data:text/javascript;base64,'+Buffer.from(source).toString('base64'));
let passed=0;const check=(v,s)=>{if(!v)throw Error(s);passed++;console.log('PASS '+s)},reject=async(fn,s)=>{let threw=false;try{await fn()}catch{threw=true}check(threw,s)};
const original=globalThis.fetch,id='12345678-1234-1234-1234-123456789abc';
try{
 const calls=[];globalThis.fetch=async(url,options)=>{calls.push({url,options});return new Response('{"requestId":"'+id+'"}')};
 const value=await status('event & id','actor & id',id);check(value.status===200&&value.value.requestId===id,'read returns snapshot without implying delivery');check(calls.length===1,'one explicit GET only, no CSRF or submit request');
 const q=new URL('https://localhost'+calls[0].url);check(q.pathname==='/api/core/manual-notification-push-status'&&q.searchParams.get('id')==='event & id'&&q.searchParams.get('actor')==='actor & id'&&q.searchParams.get('requestId')===id,'event actor and accepted request are encoded separately');check(calls[0].options.method==='GET'&&!calls[0].options.body&&!calls[0].options.headers,'read never sends notification content or mutation body');check(calls[0].options.cache==='no-store'&&calls[0].options.redirect==='error'&&calls[0].options.signal instanceof AbortSignal,'private read refuses caches/redirects and has cancellation');
 for(const code of [400,403]){let n=0;globalThis.fetch=async()=>{n++;return new Response('{}',{status:code})};const x=await status('e','a',id);check(x.status===code&&x.value===null&&n===1,'explicit read rejection '+code+' exposes no previous snapshot and never retries');}
 for(const code of [429,500,502]){let n=0;globalThis.fetch=async()=>{n++;return new Response('{}',{status:code})};await reject(()=>status('e','a',id),'HTTP '+code+' cannot invent status');check(n===1,'HTTP '+code+' is not automatically polled or retried');}
 let n=0;globalThis.fetch=async()=>{n++;throw Error('disconnect')};await reject(()=>status('e','a',id),'lost read does not invent provider outcome');check(n===1,'network failure never invokes mutation or retry');
 globalThis.fetch=async()=>new Response('invalid');await reject(()=>status('e','a',id),'malformed success cannot produce snapshot');
 const invalid=[['','a',id],['e','',id],['e','a','wrong'],['e','a',id.toUpperCase()],[null,'a',id],['e',null,id],['e','a',null]];
 n=0;globalThis.fetch=async()=>{n++;return new Response('{}')};for(const args of invalid)await reject(()=>status(...args),'invalid context rejected before read '+JSON.stringify(args));check(n===0,'invalid contexts cause no network request');check(!/localStorage|sessionStorage|console\.|setInterval/.test(source),'reader has no browser storage logging or background timer');
 console.log(`Manual Push status transport: ${passed} checks passed.`);
}finally{globalThis.fetch=original}
