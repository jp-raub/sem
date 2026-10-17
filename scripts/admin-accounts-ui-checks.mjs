import assert from 'node:assert/strict';
import fs from 'node:fs/promises';
const source=await fs.readFile(new URL('../src/Sem.Web/wwwroot/admin-accounts.js',import.meta.url),'utf8');
const account=await import('data:text/javascript;base64,'+Buffer.from(source).toString('base64'));
const savedFetch=globalThis.fetch,savedLocation=globalThis.location;
const calls=[];let replies=[];
globalThis.location={origin:'https://admin.example.test'};
globalThis.fetch=async(url,options)=>{calls.push({url:String(url),options});assert.ok(replies.length,'Unexpected automatic request/retry');const next=replies.shift();if(next instanceof Error)throw next;return next;};
const json=(body,status=200)=>new Response(JSON.stringify(body),{status,headers:{'Content-Type':'application/json'}});
const reset=(...responses)=>{calls.length=0;replies=responses;};
try{
 reset(json({actorId:'admin',accounts:[]}));assert.deepEqual(await account.load(),{actorId:'admin',accounts:[]});
 assert.equal(calls.length,1);assert.equal(calls[0].options.method,'GET');
 const changes=[{userId:'member',expectedConcurrencyStamp:'stamp',isAdmin:true}];
 reset(json({token:'synthetic-csrf'}),json({saved:true}));assert.deepEqual(await account.save('admin',changes),{saved:true});
 assert.equal(calls.length,2);const sent=calls[1];assert.equal(new URL(sent.url).searchParams.get('expectedActor'),'admin');assert.equal(sent.options.method,'POST');assert.equal(sent.options.headers['X-CSRF-TOKEN'],'synthetic-csrf');assert.deepEqual(JSON.parse(sent.options.body),{changes});
 for(const call of calls){assert.equal(call.options.credentials,'same-origin');assert.equal(call.options.mode,'same-origin');assert.equal(call.options.cache,'no-store');assert.equal(call.options.redirect,'error');assert.equal(call.options.referrerPolicy,'same-origin');assert.ok(!call.url.includes('synthetic-csrf'));}
 reset(json({token:''}));await assert.rejects(account.save('admin',changes));assert.equal(calls.length,1);
 reset(json({token:'synthetic-csrf'}));await assert.rejects(account.save('admin',[{userId:'x'.repeat(70_000)}]),RangeError);assert.equal(calls.length,1);
 reset(json({token:'synthetic-csrf'}),new Error('response lost after commit'));await assert.rejects(account.save('admin',changes));assert.equal(calls.length,2);assert.equal(calls.filter(c=>c.options.method==='POST').length,1);
 reset(json({token:'synthetic-csrf'}),json({error:'conflict'},409));await assert.rejects(account.save('admin',changes),/conflict/);assert.equal(calls.length,2);
 reset(new Response(new Uint8Array([0xff]),{status:200}));await assert.rejects(account.load());assert.equal(calls.length,1);
 reset(new Response(' '.repeat(5*1024*1024+1),{status:200}));await assert.rejects(account.load(),RangeError);assert.equal(calls.length,1);
 reset(json({accounts:[]}));await assert.rejects(account.load());assert.equal(calls.length,1);
 console.log('Admin accounts UI synthetic checks passed: load, CSRF, same-origin, bounded body/response, no replay, conflicts, strict UTF-8.');
}finally{globalThis.fetch=savedFetch;if(savedLocation===undefined)delete globalThis.location;else globalThis.location=savedLocation;}
