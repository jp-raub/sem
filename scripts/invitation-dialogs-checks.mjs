import assert from 'node:assert/strict';
import {createInvitationDialogs} from '../src/Sem.Web/wwwroot/invitation-dialogs.js';
class Node extends EventTarget {
 constructor(){super();this.disabled=false;this.isConnected=true;this.open=false;this.opens=0;this.focuses=0;this.attrs=new Map();this.removes=0;}
 addEventListener(n,f){super.addEventListener(n,f);}removeEventListener(n,f){this.removes++;super.removeEventListener(n,f);}
 focus(){this.focuses++;document.activeElement=this;}setAttribute(k,v){this.attrs.set(k,v);}getAttribute(k){return this.attrs.get(k);}closest(){return null;}
 showModal(){this.open=true;this.opens++;}close(){this.open=false;}getBoundingClientRect(){return {left:10,top:10,right:90,bottom:90};}
 querySelector(){return this.input;}
 click(){if(!this.disabled)this.dispatchEvent(new Event('click'));}
}
const document={activeElement:null};const nodes=new Map();const root=new Node();root.ownerDocument=document;root.querySelector=s=>nodes.get(s)??null;root.querySelectorAll=()=>[...nodes.values()].filter(n=>n.getAttribute('aria-label'));
for(const kind of['issue','request','review','revoke']){
 const dialog=new Node();dialog.input=new Node();nodes.set('#invitation-'+kind+'-dialog',dialog);
 nodes.set(kind==='review'?'#invitation-request-review-cancel':'#invitation-'+kind+'-close',new Node());
 if(['issue','request'].includes(kind))nodes.set('#invitation-'+kind+'-open',new Node());
}
const refresh=new Node();nodes.set('#invitation-management-refresh',refresh);const get=s=>nodes.get(s);
let dismissed=[];const ui=createInvitationDialogs(root,k=>{dismissed.push(k);return true;});const member={actor:true,admin:false,locked:false,busy:false,review:false,revoke:false};
ui.sync(member);assert.equal(get('#invitation-issue-open').disabled,true);assert.equal(get('#invitation-request-open').disabled,false);
get('#invitation-request-open').focus();get('#invitation-request-open').click();const request=get('#invitation-request-dialog');assert.equal(request.open,true);assert.equal(request.input.focuses,1);
ui.sync(member);assert.equal(request.opens,1,'Sync does not reopen or refocus an existing dialog');
ui.sync({...member,busy:true,locked:true});assert.equal(get('#invitation-request-close').disabled,true);request.dispatchEvent(new Event('cancel',{cancelable:true}));assert.equal(request.open,true);assert.equal(dismissed.length,0,'Busy Escape cannot discard a pending operation');
ui.sync(member);request.dispatchEvent(new Event('cancel',{cancelable:true}));assert.equal(request.open,false);assert.equal(document.activeElement,get('#invitation-request-open'));assert.deepEqual(dismissed,['request']);
const trigger=new Node();trigger.setAttribute('aria-label','safe server-independent button label');document.activeElement=trigger;ui.capture();trigger.isConnected=false;const replacement=new Node();replacement.setAttribute('aria-label',trigger.getAttribute('aria-label'));nodes.set('#replacement',replacement);
const admin={...member,admin:true,review:true};ui.sync(admin);assert.equal(get('#invitation-review-dialog').open,true);ui.sync({...admin,review:false});assert.equal(document.activeElement,replacement,'Rebuilt row trigger receives restored focus');
ui.sync({...admin,review:false});get('#invitation-issue-open').click();assert.equal(get('#invitation-issue-dialog').open,true);ui.sync({...admin,review:false,locked:true});assert.equal(get('#invitation-issue-dialog').open,false);assert.equal(get('#invitation-issue-open').disabled,true,'Unknown/read error locks launchers and exposes inline refresh');
ui.sync({...member,actor:false});assert.equal(get('#invitation-request-open').disabled,true);assert.equal([...nodes.values()].some(n=>n.open),false);
ui.sync(member);get('#invitation-request-open').click();ui.closeAll();assert.equal(request.open,false);get('#invitation-request-open').click();ui.completed('request');assert.equal(request.open,false);
ui.dispose();assert.ok([...nodes.values()].reduce((n,x)=>n+x.removes,0)>=18);get('#invitation-request-open').click();assert.equal(request.open,false,'Disposal releases listeners');
assert.throws(()=>createInvitationDialogs({...root,querySelector:s=>s==='#invitation-issue-dialog'?new Node():null},()=>{}),TypeError);
console.log('Invitation dialog presentation checks passed: permissions, explicit open, focus, busy guard, unknown lock, row replacement, secret-scope close, disposal, incomplete markup rejection.');
