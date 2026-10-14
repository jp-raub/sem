// Selection is the only persisted value. Server-supplied availability remains authoritative.
const kinds = new Set(['main','roster','checkin','progress','notifications']);
const scopeKey = (userId,eventId) => {
  if (typeof userId !== 'string' || !userId || userId.length > 128 || typeof eventId !== 'string' || !eventId || eventId.length > 128) return null;
  return 'sem.event-tab.v1:' + encodeURIComponent(userId) + ':' + encodeURIComponent(eventId);
};
const permitted = available => new Set(Array.isArray(available) ? available.filter(x=>typeof x==='string' && kinds.has(x)) : []);
export function readTabSelection(storage,userId,eventId,available) {
  const key=scopeKey(userId,eventId), allowed=permitted(available);
  if (!key || !allowed.has('main')) return 'main';
  try { const value=storage.getItem(key);return allowed.has(value) ? value : 'main'; } catch { return 'main'; }
}
export function saveTabSelection(storage,userId,eventId,available,value) {
  const key=scopeKey(userId,eventId);
  if (!key || typeof value !== 'string' || !permitted(available).has(value)) return false;
  try { storage.setItem(key,value);return true; } catch { return false; }
}
export function nextTabIndex(key,index,length) {
  if (!Number.isSafeInteger(length) || length<=0 || !Number.isSafeInteger(index) || index<0 || index>=length) return null;
  if (key==='ArrowRight') return (index+1)%length;
  if (key==='ArrowLeft') return (index+length-1)%length;
  if (key==='Home') return 0;
  if (key==='End') return length-1;
  return null;
}
