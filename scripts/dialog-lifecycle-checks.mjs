import assert from 'node:assert/strict';
import fs from 'node:fs/promises';
const load=async name=>import('data:text/javascript;base64,'+Buffer.from(await fs.readFile(new URL('../src/Sem.Web/wwwroot/'+name,import.meta.url),'utf8')).toString('base64'));
const saved={document:globalThis.document,HTMLDialogElement:globalThis.HTMLDialogElement};
try{
 let focuses=0,opens=0,closes=0,textareaFocus=0;
 const trigger={isConnected:true,focus:()=>focuses++};
 globalThis.document={activeElement:trigger};
 class Dialog{open=false;isConnected=true;showModal(){this.open=true;opens++;}close(){this.open=false;closes++;}querySelector(){return{focus(){textareaFocus++;}};}}
 globalThis.HTMLDialogElement=Dialog;
 const comments=await load('comment-dialog.js');const dialog=new Dialog();
 comments.sync(dialog,true);assert.equal(opens,1);assert.equal(textareaFocus,1);
 comments.sync(dialog,true);assert.equal(opens,1);
 comments.sync(dialog,false);assert.equal(closes,1);assert.equal(focuses,1);
 comments.sync(dialog,true);comments.release(dialog);assert.equal(dialog.open,false);assert.equal(focuses,1);
 dialog.isConnected=false;comments.sync(dialog,true);assert.equal(opens,2);
 let removed=0;const accountDialog={focus(){},addEventListener(){},removeEventListener(type){assert.equal(type,'keydown');removed++;}};
 globalThis.document={activeElement:trigger,querySelector:()=>accountDialog};
 const accounts=await load('admin-accounts.js');accounts.focusDialog();accounts.releaseDialog();accounts.restoreDialogFocus();
 assert.equal(removed,2);assert.equal(focuses,1,'Disposal must not retain or restore a previous route focus target');
 console.log('Dialog lifecycle checks passed: native modal, no duplicate open, focus return, release, detached nodes, admin disposal.');
}finally{for(const [key,value]of Object.entries(saved)){if(value===undefined)delete globalThis[key];else globalThis[key]=value;}}
