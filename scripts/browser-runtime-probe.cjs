'use strict';
// 読取専用の空ページで実際のブラウザー起動を確認する。製品や保存データは使わない。
const missingLibrary=error=>/error while loading shared libraries: [A-Za-z0-9_.+-]+: cannot open shared object file/i.test(String(error?.message||''));
async function probe(chromium){
 let browser;
 try{browser=await chromium.launch({timeout:20000});const page=await browser.newPage();await page.setContent('<title>SEM runtime probe</title>');if(await page.title()!=='SEM runtime probe')throw Error('PROBE_CONTENT');await page.close();return 0;}
 catch(error){return missingLibrary(error)?78:1;}
 finally{if(browser)await browser.close();}
}
module.exports={missingLibrary,probe};
if(require.main===module)probe(require('playwright').chromium).then(code=>{console.log(code===0?'ブラウザー実起動を確認済み。OS依存の再導入は不要です。':code===78?'共有ライブラリ不足を確認。公式のOS依存導入が必要です。':'ブラウザー起動検証が失敗。依存導入の推測・失敗無視はしません。');process.exitCode=code;},()=>{console.error('ブラウザー検証の後始末に失敗しました。');process.exitCode=1;});
