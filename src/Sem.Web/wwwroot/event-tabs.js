import {readTabSelection,saveTabSelection,nextTabIndex} from './event-tabs-state.mjs';
const bindings=new Map();
export function unbindTabs(id,owner){const previous=bindings.get(id);if(previous&&(owner===undefined||previous.owner===owner)){previous.root.removeEventListener('keydown',previous.key);bindings.delete(id);}}
export function bindTabs(id,sink,userId,eventId,available,selected,restore,owner="default"){
 unbindTabs(id);const root=document.getElementById(id);if(!root)throw new Error("Event tab root unavailable");
 // Defensive client filter never grants server permissions. No event data or drafts are persisted.
 const buttons=[...root.querySelectorAll('[role=tab][data-event-tab]')].filter(b=>available.includes(b.dataset.eventTab));
 const key=e=>{if(e.altKey||e.ctrlKey||e.metaKey||e.shiftKey)return;const index=buttons.indexOf(e.target),next=nextTabIndex(e.key,index,buttons.length);if(next===null)return;e.preventDefault();const b=buttons[next];b.focus();b.scrollIntoView({block:'nearest',inline:'nearest'});sink.invokeMethodAsync('SelectFromKeyboard',b.dataset.eventTab).catch(()=>{});};
 const active=buttons.find(b=>b.dataset.eventTab===selected);if(active){active.focus({preventScroll:true});active.scrollIntoView({block:'nearest',inline:'nearest'});}else if(buttons[0])buttons[0].tabIndex=0;
 root.addEventListener('keydown',key);bindings.set(id,{root,key,owner});
 let storage;try{storage=window.sessionStorage;}catch{return null;}
 const target=restore?readTabSelection(storage,userId,eventId,available):selected;
 if(target===selected)saveTabSelection(storage,userId,eventId,available,selected);
 // Do not store a requested tab here: a blocked or unsuccessful route leaves the current selection untouched.
 return target;
}
