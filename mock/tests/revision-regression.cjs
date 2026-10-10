const {chromium}=require('playwright');
(async()=>{
 const browser=await chromium.launch({executablePath:process.env.CHROMIUM_PATH||'/usr/local/bin/chromium',args:['--no-sandbox']});
 try {
 const page=await browser.newPage({viewport:{width:1440,height:1000}}),checks=[],errors=[];
 page.on('pageerror',e=>errors.push(e.message));page.on('dialog',d=>d.accept());
 const ok=(value,message)=>{if(!value)throw Error(message);checks.push(message);};
 const base=(process.env.MOCK_URL||'http://127.0.0.1:8765').replace(/\/$/,'');
 await page.goto(base);await page.locator('[data-demo="u1"]').click();await page.locator('a[href="#/events/e1"]').click();
 await page.locator('[data-slot="e1-0-1"]').click();await page.locator('[data-slot="e1-0-2"]').click();await page.locator('#submit-entry').click();
 ok((await page.locator('.quota').innerText()).startsWith('1'),'Same-song quota preserved');
 await page.locator('[data-slot="e1-1-0"]').click();await page.locator('[data-slot="e1-2-0"]').click();await page.locator('.side summary').click();await page.locator('#simulate-conflict').check();await page.locator('#submit-entry').click();
 const result=await page.locator('.result').innerText();ok(result.includes('競合')&&result.includes('登録完了'),'Partial-success registration preserved');
 await page.locator('[data-slot="e1-3-0"]').click();await page.locator('#submit-entry').click();ok((await page.locator('.result').innerText()).includes('登録上限'),'MaxPicks preserved');
 await page.locator('#persona').selectOption('u2');await page.goto(base+'/#/admin/users');ok((await page.locator('#app').innerText()).includes('全体管理者のみ'),'Event-admin service restriction preserved');
 await page.goto(base+'/#/events/e1');await page.locator('#edit-table').click();await page.locator('[data-slot="e1-0-0"]').click();await page.locator('#unassign').click();await page.locator('#modal-submit').click();ok(await page.locator('#dialog').isVisible(),'Unassignment still requires reason');
 await page.locator('[name="reason"]').fill('テスト');await page.locator('#modal-submit').click();ok(await page.evaluate(()=>JSON.parse(localStorage.getItem('session-room-mock-v1')).events[0].rows[1].parts[0].AssignedUserId==='u4'),'Unassignment remains staged until confirm');
 await page.locator('#commit-edit').click();ok(await page.evaluate(()=>JSON.parse(localStorage.getItem('session-room-mock-v1')).events[0].rows[1].parts[0].AssignedUserId===null),'Unassignment persists after confirm');
 await page.locator('#edit-table').click();await page.locator('#overview').click();await page.locator('#header-upload').setInputFiles({name:'demo.png',mimeType:'image/png',buffer:Buffer.from('iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAwMCAO+aN9sAAAAASUVORK5CYII=','base64')});await page.locator('.image-preview').waitFor();await page.locator('#modal-submit').click();ok(await page.locator('.header-image').count()===1,'Header image preview/display works');await page.locator('#commit-edit').click();await page.reload();ok(await page.locator('.header-image').count()===1,'Header image persists after reload');
 await page.locator('#edit-table').click();await page.locator('#overview').click();await page.locator('[name="Title"]').fill('stale edit');await page.locator('#modal-submit').click();await page.evaluate(()=>{const data=JSON.parse(localStorage.getItem('session-room-mock-v1'));data.externalUpdate=true;localStorage.setItem('session-room-mock-v1',JSON.stringify(data));});await page.locator('#commit-edit').click();ok((await page.locator('#toast').innerText()).includes('別のタブ'),'Stale draft overwrite rejected');await page.locator('#cancel-edit').click();
 await page.locator('#persona').selectOption('u3');await page.goto(base+'/#/events/e2');await page.locator('#edit-table').click();await page.locator('#import-open').click();await page.locator('summary').filter({hasText:'Google Spreadsheetから読み込む'}).click();
 let fetched=false;const host=['https:','','docs.google.com'].join('/');
 await page.route(host+'/spreadsheets/d/demo-sheet/export*',route=>{fetched=true;return route.fulfill({status:200,headers:{'access-control-allow-origin':'*'},contentType:'text/csv',body:'Kind,Title,Artist,Duration,Prep,Parts\nSong,シート曲,サンプル,2:03,0:00,Vo|Gt\n'});});
 await page.locator('#sheet-url').fill(host+'/spreadsheets/d/demo-sheet/edit#gid=123');await page.locator('#fetch-sheet').click();await page.waitForFunction(()=>document.querySelector('#import-text').value.includes('シート曲'));ok(fetched,'Public Sheets CSV endpoint conversion (mocked response)');await page.locator('#parse-import').click();await page.locator('#modal-submit').click();await page.locator('#commit-edit').click();ok((await page.locator('.board').innerText()).includes('シート曲'),'Fetched CSV validates and appends');
 await page.locator('#persona').selectOption('u1');ok(await page.locator('a[href="#/events/e2"]').count()===0,'Hidden imported event remains invisible to participant');ok(errors.length===0,'No browser runtime errors');
 console.log(JSON.stringify({passed:checks.length,checks,errors},null,2));
 } finally {await browser.close();}
})().catch(error=>{console.error(error);process.exit(1);});
