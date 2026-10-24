import assert from 'node:assert/strict';
import fs from 'node:fs/promises';
const module = await import('data:text/javascript;base64,'+Buffer.from(await fs.readFile(new URL('../src/Sem.Web/wwwroot/draft-edit-dialog.js',import.meta.url))).toString('base64'));
let focuses=0, clicks=0, opens=0, closes=0;
const target={isConnected:true,focus(){focuses++;}};
globalThis.document={activeElement:target};
class Dialog extends EventTarget {
 open=false; isConnected=true; adds=0; removes=0;
 showModal(){this.open=true;opens++;} close(){this.open=false;closes++;}
 addEventListener(t,h){this.adds++;super.addEventListener(t,h);}
 removeEventListener(t,h){this.removes++;super.removeEventListener(t,h);}
 querySelector(s){return s==='[data-draft-cancel]'?{click(){clicks++;}}:target;}
 getBoundingClientRect(){return {left:20,right:200,top:20,bottom:200};}
}
globalThis.HTMLDialogElement=Dialog;
const d=new Dialog();module.sync(d,false);module.sync(d,false);
assert.equal(opens,1);assert.equal(d.adds,3);
assert.equal(d.dispatchEvent(new Event('cancel',{cancelable:true})),false);assert.equal(clicks,1);assert.equal(d.open,true);
function pointer(type,x,y){const e=new Event(type);e.clientX=x;e.clientY=y;d.dispatchEvent(e);}
pointer('pointerdown',0,0);pointer('pointerup',0,0);assert.equal(clicks,2);
pointer('pointerdown',100,100);pointer('pointerup',0,0);assert.equal(clicks,2,'Dragging from fields to outside must not cancel');
pointer('pointerdown',100,100);pointer('pointerup',100,100);assert.equal(clicks,2,'Inside dialog clicks must not cancel');
module.sync(d,true);module.sync(d,false);const before=focuses;module.release(d);
assert.equal(d.removes,3);assert.equal(d.open,false);assert.equal(focuses,before+1);assert.equal(closes,1);
module.release(d);assert.equal(d.removes,3);
d.dispatchEvent(new Event('cancel',{cancelable:true}));assert.equal(clicks,2);
const detached=new Dialog();detached.isConnected=false;module.sync(detached,false);assert.equal(opens,1);
console.log('Draft edit dialog checks passed: 16');
