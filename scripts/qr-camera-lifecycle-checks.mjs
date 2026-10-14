import assert from 'node:assert/strict';
import {createController} from '../src/Sem.Web/wwwroot/qr-camera-controller.js';
let checks=0;const ok=(x)=>{assert.ok(x);checks++;};
function fixture(media,decode=()=>null){let tick,stops=[],codes=[];const video={srcObject:null,play:async()=>{}};const c=createController({media,video,nextFrame:f=>(tick=f,1),cancelFrame:()=>{tick=null},decode,validate:x=>x==='valid'?x:null,onCode:x=>codes.push(x),onStop:x=>stops.push(x)});return {c,video,codes,stops,tick:()=>tick?.()};}
function stream(){const t={stopped:false,stop(){this.stopped=true}};return {getTracks:()=>[t],t};}
let s=stream(),f=fixture({getUserMedia:async()=>s});f.c.stop();ok(!f.c.active);await f.c.start();ok(f.c.active&&f.video.srcObject===s);f.c.stop();ok(s.t.stopped&&f.video.srcObject===null);
let grant;f=fixture({getUserMedia:()=>new Promise(r=>grant=r)});let pending=f.c.start();f.c.stop();s=stream();grant(s);await pending;ok(s.t.stopped&&!f.c.active);
s=stream();let result='invalid';f=fixture({getUserMedia:async()=>s},()=>result);await f.c.start();await f.tick();ok(f.codes.length===0&&f.c.active);result='valid';await f.tick();ok(f.codes[0]==='valid'&&s.t.stopped&&!f.c.active);
f=fixture({getUserMedia:async()=>{throw Error()}});await f.c.start();ok(f.stops.at(-1)==='permission-or-device-error');s=stream();f=fixture({getUserMedia:async()=>s});f.video.play=async()=>{throw Error()};await f.c.start();ok(s.t.stopped&&f.stops.at(-1)==='play-error');let resolveDecode;s=stream();f=fixture({getUserMedia:async()=>s},()=>new Promise(r=>resolveDecode=r));await f.c.start();pending=f.tick();f.c.stop();resolveDecode('valid');await pending;ok(s.t.stopped&&f.codes.length===0);
s=stream();f=fixture({getUserMedia:async()=>s},()=>{throw Error()});await f.c.start();await f.tick();ok(s.t.stopped&&f.stops.at(-1)==='decode-error');
console.log(`Camera lifecycle: ${checks} passed`);
