'use strict';
const assert=require('node:assert/strict');const{missingLibrary,probe}=require('./browser-runtime-probe.cjs');
(async()=>{
 assert(missingLibrary(Error('error while loading shared libraries: libnss3.so: cannot open shared object file: No such file or directory')));
 for(const message of ['browserType.launch: Timeout 20000ms exceeded','Executable does not exist','Permission denied','Target page, context or browser has been closed','sandbox initialization failed'])assert(!missingLibrary(Error(message)));
 let closed=0,pageClosed=0;
 const browser={newPage:async()=>({setContent:async value=>assert(value.includes('SEM runtime probe')),title:async()=>'SEM runtime probe',close:async()=>pageClosed++}),close:async()=>closed++};
 assert.equal(await probe({launch:async()=>browser}),0);assert.equal(closed,1);assert.equal(pageClosed,1);
 assert.equal(await probe({launch:async()=>{throw Error('error while loading shared libraries: libasound.so.2: cannot open shared object file');}}),78);
 assert.equal(await probe({launch:async()=>{throw Error('unrelated launch failure');}}),1);
 assert.equal(await probe({launch:async()=>({...browser,newPage:async()=>{throw Error('unrelated page failure');}})}),1);assert.equal(closed,2);
 const fs=require('node:fs');const workflow=fs.readFileSync('.github/workflows/main-integration.yml','utf8');assert.equal((workflow.match(/install chromium --only-shell/g)||[]).length,2);assert(!workflow.includes('install --with-deps'));assert.equal((workflow.match(/if \[\[ "\$status" != 78 \]\]; then exit "\$status"; fi/g)||[]).length,2);assert.equal((workflow.match(/install-deps chromium/g)||[]).length,2);
 await assert.rejects(probe({launch:async()=>({...browser,close:async()=>{throw Error('cleanup failed');}})}));
 console.log('ブラウザー起動の分類・実ページ・失敗時後始末の検査に成功');
})().catch(error=>{console.error(error);process.exitCode=1;});
