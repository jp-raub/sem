'use strict';
// 採用mockの最終メッセージボックス。参照で既読保存を偽装しない。
window.SemMessageView=(()=>{
 const date=v=>v===null||typeof v==='string'&&Number.isFinite(Date.parse(v));
 const item=v=>v&&typeof v.id==='string'&&v.id.length>0&&v.id.length<=128&&typeof v.title==='string'&&v.title.length<=2000&&date(v.createdAt)&&typeof v.isRead==='boolean';
 const valid=v=>v&&Object.keys(v).sort().join(',')==='actorId,hasMore,messages,page,siteName'&&typeof v.siteName==='string'&&typeof v.actorId==='string'&&Number.isInteger(v.page)&&v.page>=0&&typeof v.hasMore==='boolean'&&Array.isArray(v.messages)&&v.messages.length<=100&&v.messages.every(m=>item(m)&&Object.keys(m).sort().join(',')==='createdAt,id,isRead,title')&&new Set(v.messages.map(m=>m.id)).size===v.messages.length;
 const validBody=v=>item(v)&&Object.keys(v).sort().join(',')==='actorId,body,createdAt,id,isRead,title'&&typeof v.actorId==='string'&&typeof v.body==='string'&&v.body.length<=65536;
 function render(v,{app,esc,dateText,open,load}){app.innerHTML=`<section class="panel message-box"><h1>メッセージボックス</h1>${v.messages.map(m=>`<article class="inbox-message"><span class="badge">${m.isRead?'既読':'未読'}</span><button class="message-title" data-message-open="${esc(m.id)}">${esc(m.title)}</button><small>${m.createdAt?esc(dateText(m.createdAt)):'日時不明'}</small></article>`).join('')||'<p>メッセージはまだありません。</p>'}<p class="tiny muted">本人宛の保存済み通知です。既読の保存はまだ接続していないため、開くだけでは既読になりません。Pushの許可・登録・送信は行いません。</p><div class="actions"><button id="messages-prev" ${v.page===0?'disabled':''}>前の100件</button><button id="messages-next" ${!v.hasMore?'disabled':''}>次の100件</button></div><p class="tiny muted" role="status">${v.page+1}ページ目・${v.messages.length}件を表示</p></section>`;app.querySelectorAll('[data-message-open]').forEach(b=>b.onclick=()=>open(b.dataset.messageOpen));app.querySelector('#messages-prev').onclick=()=>load(v.page-1);app.querySelector('#messages-next').onclick=()=>load(v.page+1);}
 return Object.freeze({valid,validBody,render});
})();
