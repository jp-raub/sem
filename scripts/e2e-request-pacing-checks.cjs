'use strict';
const assert = require('node:assert/strict');
const {createPacer, WEB_INTERVAL_MS, API_INTERVAL_MS} = require('./e2e-request-pacing.cjs');
(async () => {
 let checks = 0;
 for (const [interval, limit] of [[WEB_INTERVAL_MS,180],[API_INTERVAL_MS,60]]) {
  let clock = 0; const times = [];
  const pace = createPacer(interval,{now:()=>clock,sleep:async ms=>{clock+=ms;}});
  await Promise.all(Array.from({length:400},()=>pace({continue:async()=>times.push(clock)})));
  assert.equal(times.length,400); checks++;
  assert(times.slice(1).every((t,i)=>t-times[i]>=interval)); checks++;
  assert(times.every(t=>times.filter(s=>s>=t&&s<t+60000).length<limit)); checks++;
  clock+=120000;
  const idle=clock; await pace({continue:async()=>times.push(clock)});
  assert.equal(times.at(-1),idle); checks++;
  await assert.rejects(pace({continue:async()=>{throw new Error('closed route');}}),/closed route/); checks++;
  await pace({continue:async()=>times.push(clock)});
  assert(times.at(-1)>=idle+2*interval); checks++;
  let stalled=0;const releases=[];
  const slow=createPacer(interval,{now:()=>stalled,sleep:async ms=>{stalled+=ms+5000;}});
  await Promise.all(Array.from({length:4},()=>slow({continue:async()=>releases.push(stalled)})));
  assert(releases.slice(1).every((t,i)=>t-releases[i]>=interval)); checks++;
 }
 assert.throws(()=>createPacer(0),/Invalid/); checks++;
 console.log('Fixture request pacing checks passed: '+checks);
})().catch(error=>{console.error(error);process.exit(1);});
