import assert from 'node:assert/strict';
import fs from 'node:fs/promises';
const load=async name=>import('data:text/javascript;base64,'+Buffer.from(await fs.readFile(new URL('../src/Sem.Web/wwwroot/'+name,import.meta.url),'utf8')).toString('base64'));
const saved={document:globalThis.document,HTMLDialogElement:globalThis.HTMLDialogElement,getComputedStyle:globalThis.getComputedStyle};
try{
 let focuses=0,opens=0,closes=0,textareaFocus=0;
 const trigger={isConnected:true,focus:()=>focuses++};
 globalThis.document={activeElement:trigger};
 globalThis.getComputedStyle=el=>({visibility:el.hidden?'hidden':'visible'});
 const control=(options={})=>({tabIndex:0,disabled:false,hidden:false,...options,matches(){return this.disabled||this.hidden;},getClientRects(){return this.hidden?[]:[{}];},focus(){document.activeElement=this;}});
 class Dialog extends EventTarget{
  open=false;isConnected=true;closeDisabled=false;closeClicks=0;listenerAdds=0;listenerRemoves=0;onClose=()=>{};
  controls=[control(),control(),control({disabled:true}),control({hidden:true})];
  querySelectorAll(){return this.controls;}
  focus(){document.activeElement=this;}
  showModal(){this.open=true;opens++;}close(){this.open=false;closes++;}
  addEventListener(type,handler){assert.ok(['cancel','keydown'].includes(type));this.listenerAdds++;super.addEventListener(type,handler);}
  removeEventListener(type,handler){assert.ok(['cancel','keydown'].includes(type));this.listenerRemoves++;super.removeEventListener(type,handler);}
  querySelector(selector){return selector==='[data-dialog-close]'?{matches:()=>this.closeDisabled,click:()=>{this.closeClicks++;this.onClose();}}:{focus(){textareaFocus++;}};}
 }
 globalThis.HTMLDialogElement=Dialog;
 const comments=await load('comment-dialog.js');const dialog=new Dialog();
 comments.sync(dialog,true);assert.equal(opens,1);assert.equal(textareaFocus,1);
 comments.sync(dialog,true);assert.equal(opens,1);
 comments.sync(dialog,false);assert.equal(closes,1);assert.equal(focuses,1);
 comments.sync(dialog,true);comments.release(dialog);assert.equal(dialog.open,false);assert.equal(focuses,1);
 dialog.isConnected=false;comments.sync(dialog,true);assert.equal(opens,2);
 const cancelDialog=new Dialog();cancelDialog.onClose=()=>comments.sync(cancelDialog,false);
 comments.sync(cancelDialog,true);comments.sync(cancelDialog,true);
 assert.equal(cancelDialog.listenerAdds,2,'Rerenders must not register duplicate cancel listeners');
 const tab=(shiftKey=false)=>{const event=new Event('keydown',{cancelable:true});Object.assign(event,{key:'Tab',shiftKey});cancelDialog.dispatchEvent(event);return event;};
 document.activeElement=cancelDialog.controls[1];assert.equal(tab().defaultPrevented,true);assert.equal(document.activeElement,cancelDialog.controls[0],'Tab wraps at the last enabled visible control');
 assert.equal(tab(true).defaultPrevented,true);assert.equal(document.activeElement,cancelDialog.controls[1],'Shift+Tab wraps to the last enabled visible control');
 document.activeElement=trigger;assert.equal(tab().defaultPrevented,true);assert.equal(document.activeElement,cancelDialog.controls[0],'Focus outside the dialog is recovered');
 assert.equal(tab().defaultPrevented,false,'Native traversal between internal controls is preserved');
 const controls=cancelDialog.controls;cancelDialog.controls=[];assert.equal(tab().defaultPrevented,true);assert.equal(document.activeElement,cancelDialog,'An empty dialog retains focus');cancelDialog.controls=controls;
 let cancel=new Event('cancel',{cancelable:true});
 assert.equal(cancelDialog.dispatchEvent(cancel),false,'Native cancel is prevented before delegating the close action');
 assert.equal(cancelDialog.closeClicks,1);assert.equal(cancelDialog.open,false);
 comments.sync(cancelDialog,true);cancelDialog.closeDisabled=true;
 cancelDialog.dispatchEvent(new Event('cancel',{cancelable:true}));
 assert.equal(cancelDialog.closeClicks,1,'Busy or uncertain close actions must not be activated by Escape');
 assert.equal(cancelDialog.open,true,'Disabled close keeps native and server dialog state aligned');
 comments.release(cancelDialog);assert.equal(cancelDialog.listenerRemoves,2);
 document.activeElement=cancelDialog.controls[1];assert.equal(tab().defaultPrevented,false,'Disposal removes the Tab boundary listener');
 cancelDialog.closeDisabled=false;cancel=new Event('cancel',{cancelable:true});
 assert.equal(cancelDialog.dispatchEvent(cancel),true,'Disposal removes the native cancel listener');
 assert.equal(cancelDialog.closeClicks,1);
 const focusesBeforeDispose=focuses;
 let removed=0;const accountDialog={focus(){},addEventListener(){},removeEventListener(type){assert.equal(type,'keydown');removed++;}};
 globalThis.document={activeElement:trigger,querySelector:()=>accountDialog};
 const accounts=await load('admin-accounts.js');accounts.focusDialog();accounts.releaseDialog();accounts.restoreDialogFocus();
 assert.equal(removed,2);assert.equal(focuses,focusesBeforeDispose,'Disposal must not retain or restore a previous route focus target');
 console.log('Dialog lifecycle checks passed: native modal, no duplicate open, focus return, release, detached nodes, native Escape, disabled-close guard, listener cleanup, admin disposal.');
}finally{for(const [key,value]of Object.entries(saved)){if(value===undefined)delete globalThis[key];else globalThis[key]=value;}}
