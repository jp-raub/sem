import assert from 'node:assert/strict';
import fs from 'node:fs/promises';
let source=await fs.readFile('src/Sem.Web/wwwroot/reception-dialog.js','utf8');
source=source.replace("'./comment-dialog.js'",JSON.stringify(new URL('../src/Sem.Web/wwwroot/comment-dialog.js',import.meta.url).href));
const module=await import('data:text/javascript;base64,'+Buffer.from(source).toString('base64'));
const saved={HTMLDialogElement:globalThis.HTMLDialogElement,document:globalThis.document};
let focus=0,clicks=0,forbidden=0;const oldButton={isConnected:false,focus(){throw Error('置換前のボタンへ戻してはいけない')}};
const current={dataset:{receptionSelect:'current-user'},matches(){return false},focus(){focus++}};
class Dialog extends EventTarget{
 open=false;isConnected=true;disabled=false;
 showModal(){this.open=true}close(){this.open=false}
 querySelector(s){if(s==='[data-dialog-close]')return{matches:()=>this.disabled,click(){clicks++}};return null}
 closest(){return{querySelectorAll(){return[current]},querySelector(){throw Error('一致する対象者がいるため検索欄への代替は不要')}}}
}
try{
 globalThis.HTMLDialogElement=Dialog;globalThis.document={activeElement:oldButton,querySelector(){forbidden++;throw Error('後続画面の文書全体を検索してはいけない')}};
 const dialog=new Dialog();module.sync(dialog,true,'current-user');assert(dialog.open);
 dialog.disabled=true;dialog.dispatchEvent(new Event('cancel',{cancelable:true}));assert.equal(clicks,0);
 dialog.disabled=false;dialog.dispatchEvent(new Event('cancel',{cancelable:true}));assert.equal(clicks,1);
 module.sync(dialog,false);assert.equal(focus,1);assert(!dialog.open);assert.equal(forbidden,0);
 module.sync(dialog,true,'current-user');module.release(dialog);assert(!dialog.open);
 dialog.dispatchEvent(new Event('cancel',{cancelable:true}));assert.equal(clicks,1);
 module.release(dialog);assert.equal(forbidden,0);
 console.log('受付モーダル：閉じる禁止・一覧置換後の対象復帰・旧所有DOMだけの解放を確認');
}finally{for(const [k,v]of Object.entries(saved)){if(v===undefined)delete globalThis[k];else globalThis[k]=v;}}
const razor=await fs.readFile('src/Sem.Web/Components/Pages/Reception.razor','utf8');
assert(razor.includes('bool CanClose => !Busy&&Pending is null&&PendingCancel is null&&!QrLocked;'));
for(const action of ['RequestClose','CloseTarget'])assert(new RegExp(`void ${action}\\(\\)\\{if\\(!CanClose\\)return;`).test(razor));
assert(razor.includes('Pending??=new(')&&razor.includes('PendingCancel??=new('));
assert(razor.includes('Authentication.AuthenticationStateChanged-=AuthenticationChanged'));
console.log('受付の実ハンドラー：処理中／結果不明の取消禁止・同一操作ID・認証変更時の後始末を維持');

assert(razor.includes("InvokeVoidAsync(\"sync\",Dialog,ConfirmationOpen,ReturnUserId)"));
assert(!razor.includes("Busy&&ReturnUserId is not null"));
console.log("一覧の再検索は処理中でも確認モーダルを開かず、明示した対象選択だけで開く");
