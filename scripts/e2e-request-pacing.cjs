'use strict';
// Fixture-only pacing. Product limiters remain unchanged (web 180/min, API 60/min).
// Serialize actual dispatches, not reserved timestamps: a delayed event loop must
// never release queued requests in a catch-up burst. Shared across fixture users.
function createPacer(interval, {now = Date.now, sleep = ms => new Promise(resolve => setTimeout(resolve, ms))} = {}) {
 if (!Number.isFinite(interval) || interval <= 0) throw new Error('Invalid pacing interval');
 let previous = -Infinity, tail = Promise.resolve();
 return route => {
  const request = tail.then(async () => {
   const delay = previous + interval - now();
   if (delay > 0) await sleep(delay);
   previous = now();
   await route.continue();
  });
  tail = request.catch(() => {});
  return request;
 };
}
const WEB_INTERVAL_MS = 375; // <=161 dispatches per rolling minute, below 180.
const API_INTERVAL_MS = 1100; // <=55 dispatches per rolling minute, below 60.
module.exports = {createPacer, WEB_INTERVAL_MS, API_INTERVAL_MS};
