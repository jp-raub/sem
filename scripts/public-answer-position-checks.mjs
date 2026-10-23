import assert from 'node:assert/strict';
import {clampPosition,bindDismiss,releaseDismiss} from '../src/Sem.Web/wwwroot/public-answer-chart.js';
let checks=0;
for(const [x,y,w,h,vw,vh] of [[100,100,120,40,800,600],[790,590,240,100,800,600],[-9,-9,240,100,390,844],[380,830,240,100,390,844]]) {
 const p=clampPosition(x,y,w,h,vw,vh);
 assert(p.x>=8 && p.y>=8 && p.x+w<=vw-8 && p.y+h<=vh-8);checks++;
}
assert.deepEqual(clampPosition(100,100,120,40,800,600),{x:112,y:112});checks++;
console.log(`Public answer popup geometry checks passed: ${checks}`);

const docEvents = new Map(), rootEvents = new Map();
globalThis.document = {body:{},addEventListener:(key,fn,capture)=>{assert.equal(capture,true);docEvents.set(key,fn);},removeEventListener:(key,fn)=>{if(docEvents.get(key)===fn)docEvents.delete(key);}};
let mutation,disconnected=0,calls=[];
globalThis.MutationObserver = class {constructor(fn){mutation=fn;}observe(){}disconnect(){disconnected++;}};
const trigger={},link={},background={},anotherGroup={},otherChart={};
const group={contains:target=>target===trigger||target===link};
let expanded=true;
const bar={closest:()=>group};
const root={isConnected:true,dataset:{popupGeneration:'7'},querySelector:()=>expanded?bar:null,addEventListener:(key,fn)=>rootEvents.set(key,fn),removeEventListener:(key,fn)=>{if(rootEvents.get(key)===fn)rootEvents.delete(key);}};
const reference={invokeMethodAsync:(name,generation)=>{calls.push([name,generation]);return Promise.resolve();}};
bindDismiss(root,reference);bindDismiss(root,reference);
assert.equal(docEvents.size,2);assert.equal(rootEvents.size,1);checks++;
for(const target of [trigger,link]) {docEvents.get('pointerdown')({target});docEvents.get('focusin')({target});rootEvents.get('focusout')({relatedTarget:target});}
assert.equal(calls.length,0);checks++;
for(const target of [background,anotherGroup,otherChart])docEvents.get('pointerdown')({target});
assert.deepEqual(calls,Array(3).fill(['Dismiss','7']));checks++;
root.dataset.popupGeneration='8';docEvents.get('focusin')({target:background});
rootEvents.get('focusout')({relatedTarget:null});assert.deepEqual(calls.slice(-2),[['Dismiss','8'],['Dismiss','8']]);checks++;
expanded=false;docEvents.get('pointerdown')({target:background});assert.equal(calls.length,5);checks++;
releaseDismiss(root);releaseDismiss(root);assert.equal(docEvents.size,0);assert.equal(rootEvents.size,0);assert.equal(disconnected,1);checks++;
root.isConnected=false;bindDismiss(root,reference);assert.equal(docEvents.size,0);checks++;
root.isConnected=true;bindDismiss(root,reference);root.isConnected=false;mutation();assert.equal(docEvents.size,0);assert.equal(disconnected,2);checks++;
delete globalThis.document;delete globalThis.MutationObserver;
console.log(`Public answer geometry and dismissal checks passed: ${checks}`);
