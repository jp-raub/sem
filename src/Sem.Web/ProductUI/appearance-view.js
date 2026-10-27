'use strict';
// 固定mockのenhancements／event-features最終色計算。本人の解決済み4色だけを受け取る。
window.SemAppearanceView=(()=>{
 const root=document.documentElement,media=matchMedia('(prefers-color-scheme: dark)'),variables=['--green','--hero','--paper','--surface','--ink','--muted','--line','--soft','--accent-soft','--lime'];let saved=null;
 const hex=v=>typeof v==='string'&&v.length===7&&/^#[0-9a-f]{6}$/i.test(v);
 const valid=v=>v&&Object.keys(v).sort().join(',')==='actorId,colors,eventId,mode,palette'&&typeof v.actorId==='string'&&(v.eventId===null||typeof v.eventId==='string')&&['green','blue','purple','amber'].includes(v.palette)&&['system','light','dark'].includes(v.mode)&&(v.colors===null||v.colors&&Object.keys(v.colors).sort().join(',')==='accent,background,surface,text'&&Object.values(v.colors).every(hex));
 const mix=(value,white)=>'#'+value.slice(1).match(/../g).map(h=>{const n=parseInt(h,16);return Math.round(n+(255-n)*white).toString(16).padStart(2,'0');}).join('');
 function paint(){variables.forEach(v=>root.style.removeProperty(v));root.dataset.palette=saved?.palette||'green';const mode=saved?.mode||'system';root.dataset.mode=mode==='system'?(media.matches?'dark':'light'):mode;const t=saved?.colors;if(!t)return;const values=root.dataset.mode==='dark'?{'--green':mix(t.accent,.5),'--hero':'#223039','--accent-soft':'#2b3c43','--lime':'#344853'}:{'--green':t.accent,'--hero':t.accent,'--paper':t.background,'--surface':t.surface,'--ink':t.text,'--muted':mix(t.text,.3),'--line':mix(t.text,.82),'--soft':t.background,'--accent-soft':mix(t.accent,.9),'--lime':mix(t.accent,.8)};for(const[k,v]of Object.entries(values))root.style.setProperty(k,v);}
 function reset(){saved=null;paint();}function apply(v,actorId,eventId){if(!valid(v)||v.actorId!==actorId||v.eventId!==eventId){reset();throw Error('appearance');}saved={...v,colors:v.colors?{...v.colors}:null};paint();}
 media.addEventListener('change',paint);reset();return Object.freeze({valid,apply,reset});
})();
