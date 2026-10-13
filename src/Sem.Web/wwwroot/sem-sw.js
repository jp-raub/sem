'use strict';
function target(raw){try{if(typeof raw!=='string'||!raw.startsWith('/')||raw.startsWith('//'))return '/?messages=1';const u=new URL(raw,self.location.origin);if(u.origin!==self.location.origin||u.username||u.password||u.hash)return '/?messages=1';if(u.pathname==='/'&&u.search==='?messages=1')return '/?messages=1';if(/^\/events\/[A-Za-z0-9-]+\/(timetable|progress|participation)$/.test(u.pathname))return u.pathname+u.search;}catch{}return '/?messages=1';}
self.addEventListener('push',event=>{let data={title:'SEM',body:'新しいメッセージがあります',url:'/?messages=1'};try{const parsed=event.data.json();if(parsed&&typeof parsed==='object')Object.assign(data,parsed);}catch{}
 const expired=Number.isFinite(data.expiresAt)&&Date.now()>=data.expiresAt;
 const title=typeof data.title==='string'?data.title.slice(0,80):'SEM';const body=expired?'期限を過ぎたお知らせです。最新情報はアプリで確認してください。':typeof data.body==='string'?data.body.slice(0,300):'新しいメッセージがあります';
 const tag=typeof data.id==='string'&&/^[A-F0-9]{64}$/.test(data.id)?data.id:undefined;
 event.waitUntil(self.registration.showNotification(title,{body,tag,renotify:false,data:{url:target(data.url)}}));
});
self.addEventListener('notificationclick',event=>{event.notification.close();const url=target(event.notification.data?.url);event.waitUntil((async()=>{const windows=await self.clients.matchAll({type:'window',includeUncontrolled:true});for(const window of windows)if(new URL(window.url).origin===self.location.origin){await window.navigate(url);return window.focus();}return self.clients.openWindow(url);})());});
