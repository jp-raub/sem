import fs from 'node:fs';
import assert from 'node:assert/strict';
let nextTimer = 0, calls = 0, completed = 0, pendingResolve;
const timers = new Map(), listeners = new Set();
globalThis.document = {
    visibilityState: 'visible',
    addEventListener: (name, fn) => { assert.equal(name, 'visibilitychange'); listeners.add(fn); },
    removeEventListener: (name, fn) => listeners.delete(fn),
};
globalThis.setTimeout = (fn, delay) => { assert.equal(delay, 7500); timers.set(++nextTimer, fn); return nextTimer; };
globalThis.clearTimeout = id => timers.delete(id);
const { start, stop } = await import('data:text/javascript;base64,' + Buffer.from(fs.readFileSync('src/Sem.Web/wwwroot/participation-refresh.js')).toString('base64'));
function check(value, name) { assert.ok(value, name); completed++; console.log('PASS ' + name); }
async function tick() { const [id, fn] = timers.entries().next().value; timers.delete(id); return fn(); }
const sink = { invokeMethodAsync: async name => { assert.equal(name, 'RefreshParticipation'); calls++; await new Promise(r => pendingResolve = r); } };
const loop = start(sink);
check(timers.size === 1 && listeners.size === 1 && calls === 0, 'Start waits 7.5 seconds without an immediate duplicate read');
const inFlight = tick();
check(calls === 1 && timers.size === 0, 'Only one read is active and no interval accumulates requests');
for (const fn of listeners) fn();
check(timers.size === 0, 'Visibility event cannot overlap an in-flight read');
document.visibilityState = 'hidden';
for (const fn of listeners) fn();
pendingResolve(); await inFlight;
check(timers.size === 0, 'Completion while hidden schedules no background read');
document.visibilityState = 'visible'; for (const fn of listeners) fn();
check(timers.size === 1, 'Visible return resumes a single scheduled read');
const second = tick(); stop(loop); pendingResolve(); await second;
check(timers.size === 0 && listeners.size === 0, 'Disposal during an active read removes listener and prevents rescheduling');
stop(loop); check(timers.size === 0, 'Repeated disposal is harmless');
document.visibilityState = 'hidden'; const hidden = start(sink);
check(timers.size === 0, 'Initially hidden document starts no timer');
stop(hidden);
document.visibilityState = 'visible'; let failures=0;
const broken = start({invokeMethodAsync:async()=>{failures++; throw new Error('Disconnected');}});
await tick(); check(failures === 1 && timers.size === 1, 'Read disconnection schedules only another read, not a write or immediate retry');
stop(broken);
const page = fs.readFileSync('src/Sem.Web/Components/Pages/Participation.Refresh.cs','utf8');
check(page.includes('Pending is not null || Uncertain') && page.includes('Pending is null && !Uncertain'), 'Start and response-adoption guards both protect immutable pending operations');
check(page.includes('generation == LoadGeneration') && page.includes('subject == Subject') && page.includes('BindingFor(live) != binding'), 'Response adoption checks generation, subject and live identity');
check(!page.includes('SubmitParticipationAsync') && !page.includes('Pending =') && !page.includes('Pending='), 'Refresh path never submits or replaces a pending operation');
console.log(`Participation refresh scheduler checks passed: ${completed}`);

// Exercise the exact modal module with the existing native-dialog lifecycle code.
const lifecycleUrl = 'data:text/javascript;base64,' + Buffer.from(fs.readFileSync('src/Sem.Web/wwwroot/comment-dialog.js')).toString('base64');
const noticeSource = fs.readFileSync('src/Sem.Web/wwwroot/participation-notice.js','utf8').replace("'./comment-dialog.js'", JSON.stringify(lifecycleUrl));
const notice = await import('data:text/javascript;base64,' + Buffer.from(noticeSource).toString('base64'));
const body = {isConnected:true};
const target = {isConnected:true,disabled:false,matches(){return this.disabled;},focus(){if(!this.disabled)document.activeElement=this;}};
const refreshTarget = {...target}, phaseTarget = {...target};
globalThis.document = {body,activeElement:target};
class NativeDialog extends EventTarget {
    open=false; isConnected=true; opens=0; closes=0; adds=0; removes=0;
    addedNames=[];removedNames=[];
    button={isConnected:true,matches:()=>false,focus:()=>{document.activeElement=this.button;},click:()=>notice.sync(this,false)};
    showModal(){this.open=true;this.opens++;}
    close(){this.open=false;this.closes++;}
    querySelector(selector){return selector==='[data-dialog-close]'?this.button:null;}
    closest(){return {querySelector:selector=>selector==='#participation-refresh'?refreshTarget:phaseTarget};}
    contains(element){return element===this.button;}
    addEventListener(name,fn){this.adds++;this.addedNames.push(name);super.addEventListener(name,fn);}
    removeEventListener(name,fn){this.removes++;this.removedNames.push(name);super.removeEventListener(name,fn);}
}
globalThis.HTMLDialogElement=NativeDialog;
const dialog=new NativeDialog();
notice.sync(dialog,true);notice.sync(dialog,true);
check(dialog.open && dialog.opens===1 && dialog.adds===2 && document.activeElement===dialog.button,
    'Native modal opens once, focuses acknowledgement, and does not duplicate on rerender');
check(JSON.stringify(dialog.addedNames)===JSON.stringify(['cancel','keydown']), 'Shared modal registers exactly the native cancel and Tab boundary listeners');
const cancel=new Event('cancel',{cancelable:true});dialog.dispatchEvent(cancel);
check(cancel.defaultPrevented && !dialog.open && document.activeElement===target,
    'Escape follows the acknowledgement action and restores the connected input');
notice.sync(dialog,true);target.disabled=true;notice.sync(dialog,false);
check(document.activeElement===refreshTarget, 'Disabled lost-slot trigger falls back to the refresh control');
target.disabled=false;document.activeElement=target;notice.sync(dialog,true);target.disabled=true;refreshTarget.disabled=true;notice.sync(dialog,false);
check(document.activeElement===phaseTarget, 'Busy refresh control falls back to the participation heading');
refreshTarget.disabled=false;target.disabled=false;document.activeElement=target;notice.sync(dialog,true);
notice.release(dialog);
check(!dialog.open && dialog.removes===2, 'Disposal closes the modal and releases its native Escape and Tab listeners');
check(JSON.stringify(dialog.removedNames)===JSON.stringify(dialog.addedNames), 'Disposal removes exactly the listeners registered by the shared modal');
dialog.isConnected=false;const before=dialog.opens;notice.sync(dialog,true);
check(dialog.opens===before, 'Detached event or authentication context cannot reopen an old modal');
const markup=fs.readFileSync('src/Sem.Web/Components/Shared/ParticipationSelectionNotice.razor','utf8');
check(markup.includes('role="alertdialog"') && markup.includes('aria-modal="true"') && markup.includes('data-dialog-close')
    && !markup.includes('SubmitParticipationAsync'), 'Modal markup announces a labelled alert and performs no registration');
console.log(`Participation refresh and modal checks passed: ${completed}`);
