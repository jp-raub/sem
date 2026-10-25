import assert from 'node:assert/strict';
import fs from 'node:fs/promises';
let source=await fs.readFile('src/Sem.Web/wwwroot/profile-editor.js','utf8');
source=source.replace("'./operation-result-presenter.js'",JSON.stringify(new URL('../src/Sem.Web/wwwroot/operation-result-presenter.js',import.meta.url).href));
const module=await import('data:text/javascript;base64,'+Buffer.from(source+'\nexport function fixtureOwner(root){ownerRoot=root;}').toString('base64'));
const saved=globalThis.document;let closed=0,globalLookups=0;
try{
 globalThis.document={getElementById(){globalLookups++;throw Error('新しい画面のDOMへ触れてはいけない');}};
 module.fixtureOwner({isConnected:false,querySelector(selector){assert(['#profile-edit-dialog','#profile-qr-dialog'].includes(selector));return{open:true,close(){closed++;}};}});
 module.cleanup();assert.equal(closed,2);assert.equal(globalLookups,0);
 module.cleanup();assert.equal(closed,2);assert.equal(globalLookups,0);
 console.log('プロフィールの破棄時は所有する旧画面の両モーダルだけを閉じ、後続画面へ触れない');
}finally{if(saved===undefined)delete globalThis.document;else globalThis.document=saved;}
