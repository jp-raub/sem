import assert from 'node:assert/strict';
import {readFile} from 'node:fs/promises';
import {createResultPresenter} from '../src/Sem.Web/wwwroot/operation-result-presenter.js';
const owner={isConnected:true}, calls=[];let current=true,cleared=0;
const presenter=createResultPresenter(owner,()=>current,(...args)=>calls.push(args),target=>{assert.equal(target,owner);cleared++;});
for(const kind of ['success','error','unknown'])presenter.show(kind,'結果 <img>');
assert.deepEqual(calls.map(x=>x[1]),['success','error','unknown']);
assert.ok(calls.every(x=>x[0]===owner&&x[2]==='結果 <img>'));
current=false;presenter.show('success','old actor');assert.equal(calls.length,3);
current=true;owner.isConnected=false;presenter.show('success','unmounted');assert.equal(calls.length,3);
presenter.clear();assert.equal(cleared,1);
owner.isConnected=true;assert.doesNotThrow(()=>createResultPresenter(owner,()=>true,()=>{throw Error('UI failed');},()=>{throw Error('cleanup failed');}).show('error','inline remains'));
assert.doesNotThrow(()=>createResultPresenter(owner,()=>true,()=>{},()=>{throw Error('cleanup failed');}).clear());
const source=await readFile('src/Sem.Web/wwwroot/operation-result-presenter.js','utf8');
assert.doesNotMatch(source,/\bfetch\s*\(|setTimeout\s*\(|\.click\s*\(/);
for(const name of ['account-passkeys','invitation-management','profile-editor']){
 const code=await readFile(`src/Sem.Web/wwwroot/${name}.js`,'utf8');
 assert.match(code,/import \{ createResultPresenter \} from '\.\/operation-result-presenter.js'/);
 assert.match(code,/resultPresenter.*clear\(/);
 assert.match(code,/resultPresenter.*show\('success'/);
}
const invitation=await readFile('src/Sem.Web/wwwroot/invitation-management.js','utf8');
for(const field of ['issueErrorMessage','requestErrorMessage','reviewErrorMessage','errorMessage']) assert.ok(invitation.includes(`state.unknown?.message || state.${field} ||`));
console.log('Operation result presenter checks passed: terminal states, stale owner suppression, cleanup, inline fallback, no writes, module hooks');
