import assert from 'node:assert/strict';
import {readFile} from 'node:fs/promises';
import '../src/Sem.Web/wwwroot/operation-result-presenter.js';
let source=await readFile('src/Sem.Web/wwwroot/profile-editor.js','utf8');
source=source.replace("'./operation-result-presenter.js'",JSON.stringify(new URL('../src/Sem.Web/wwwroot/operation-result-presenter.js',import.meta.url).href));
const {post}=await import('data:text/javascript;base64,'+Buffer.from(source+'\nexport {post};').toString('base64'));
const originalFetch=globalThis.fetch;
let writes=0;
const json=(value,status=200)=>new Response(JSON.stringify(value),{status,headers:{'Content-Type':'application/json'}});
async function failure(mode,expected){
 writes=0;globalThis.fetch=async(url,options={})=>{
  if(options.method==='GET'){if(mode==='csrf')throw Error('csrf unavailable');return json({token:'synthetic-csrf'});}
  writes++;if(mode==='network')throw Error('response lost');
  if(mode==='malformed')return new Response('broken',{headers:{'Content-Type':'application/json'}});
  return json({},Number(mode));
 };
 await assert.rejects(post('https://example.test/api/core/profile',{}),error=>error.resultKind===expected);
 assert.equal(writes,mode==='csrf'?0:1,'no automatic replay');
}
try{
 await failure('csrf','error');await failure('400','error');await failure('403','error');
 await failure('409','error');await failure('500','unknown');await failure('408','unknown');
 await failure('network','unknown');await failure('malformed','unknown');
 writes=0;globalThis.fetch=async(url,options={})=>{if(options.method==='GET')return json({token:'synthetic-csrf'});writes++;return json({ok:true});};
 assert.deepEqual(await (await post('https://example.test/api/core/profile',{})).json(),{ok:true});assert.equal(writes,1);
 assert.ok(source.includes("saved?'partial':error.resultKind||'error'"));
 console.log('Profile result boundary checks passed: 9 outcomes; pre-write, rejection, uncertain response, confirmed success, no replay');
}finally{globalThis.fetch=originalFetch;}
