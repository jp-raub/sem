// Pure validation: never fetch, navigate, write a read or transmit a code here.
export function parseCode(value, origin, eventId) {
 const text = (value || '').trim();
 if (/^[a-fA-F0-9]{64}$/.test(text)) return text.toUpperCase();
 try {
  const url = new URL(text);
  if (url.origin !== origin || url.username || url.password || url.search || url.pathname !== '/events/' + encodeURIComponent(eventId) + '/reception') return null;
  const match = /^#code=([a-fA-F0-9]{64})$/.exec(url.hash);
  return match ? match[1].toUpperCase() : null;
 } catch { return null; }
}
export function takeFragment(eventId) {
 const fragment = window.location.hash;
 if (!fragment) return null;
 const path = window.location.pathname;
 // Immediately remove sensitive fragment. No automatic scan/write.
 window.history.replaceState(window.history.state, '', path + window.location.search);
 if (path !== '/events/' + encodeURIComponent(eventId) + '/reception' || window.location.search) return null;
 const match = /^#code=([a-fA-F0-9]{64})$/.exec(fragment);
 return match ? match[1].toUpperCase() : null;
}
export function parseCurrent(value, eventId) { return parseCode(value, window.location.origin, eventId); }
