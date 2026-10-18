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
