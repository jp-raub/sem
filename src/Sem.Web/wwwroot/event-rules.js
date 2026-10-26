'use strict';
// v3.1: slot availability and end-of-event-day comment cutoff (Asia/Tokyo).
(()=>{
const previous={home,eventPage,submitEntry};let deadlineTimer;
function slots(e){const ps=e.rows.filter(r=>r.Kind==='Song').flatMap(r=>r.parts||[]);return {free:ps.filter(p=>!p.AssignedUserId).length,total:ps.length};}
function endExclusive(e){const value=String(e.ScheduledDate||'');let date=value.slice(0,10);if(/[zZ]$|[+-]\d\d:\d\d$/.test(value)){const d=new Date(value);if(!Number.isFinite(d.getTime()))return NaN;const a=new Intl.DateTimeFormat('en-CA',{timeZone:'Asia/Tokyo',year:'numeric',month:'2-digit',day:'2-digit'}).formatToParts(d);date=['year','month','day'].map(k=>a.find(p=>p.type===k).value).join('-');}const m=date.match(/^(\d{4})-(\d{2})-(\d{2})$/);if(!m)return NaN;return Date.UTC(Number(m[1]),Number(m[2])-1,Number(m[3])+1)-9*3600000;}
function commentOpen(e,now=Date.now()){return admin(e)||Number.isFinite(endExclusive(e))&&now<endExclusive(e);}
home=function(){previous.home();const table=$('#app .panel table');if(!table)return;table.querySelector('thead tr').children[2].insertAdjacentHTML('afterend','<th>枠状況</th>');table.querySelectorAll('tbody tr').forEach(tr=>{const link=tr.querySelector('.event-link'),id=link?.getAttribute('href')?.slice('#/events/'.length),e=db.events.find(e=>e.Id===id);if(!e)return;const n=slots(e),td=document.createElement('td');td.className='slot-availability';td.textContent=`${n.free} / ${n.total}`;td.title='空き枠数 / 全体枠数（進行パートは対象外）';tr.children[2].after(td);});};
function applyCommentRules(e){if(location.hash.split('?')[0]!=='#/events/'+e.Id)return;const input=$('#comment-input');if(!input)return;const closed=!commentOpen(e);if(closed){input.disabled=true;input.value='';if(!selected.size)$('#submit-entry').disabled=true;}
 const notice=$('#comment-deadline');if(notice)notice.textContent=closed?'コメント受付は終了しました（実施日の23:59:59まで・日本時間）。パート申込みの可否は現在のフェーズに従います。':admin(e)?'管理者は実施日の終了後もコメント投稿できます。':'コメント受付はイベント実施日の23:59:59までです（日本時間）。';
 clearTimeout(deadlineTimer);const remaining=endExclusive(e)-Date.now();if(!admin(e)&&remaining>0)deadlineTimer=setTimeout(()=>applyCommentRules(e),Math.min(remaining+1,2147483647));
}
eventPage=function(e){previous.eventPage(e);const input=$('#comment-input');if(input)input.closest('label').insertAdjacentHTML('afterend','<p id="comment-deadline" class="tiny muted" role="status"></p>');applyCommentRules(e);};
submitEntry=function(e){const text=($('#comment-input')?.value||'').trim();if(text&&!commentOpen(e)){toast('コメント期限を過ぎています。コメントは投稿できません。');applyCommentRules(e);return;}previous.submitEntry(e);};
window.MockEventRules={slots,endExclusive,commentOpen};
render();
})();
