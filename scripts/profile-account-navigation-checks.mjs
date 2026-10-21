// Synthetic lifecycle and URL-boundary checks. Actual browser navigation is checked in acceptance-navigation-checks.cjs.
import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
const source = readFileSync(new URL('../src/Sem.Web/wwwroot/profile-settings-navigation.js', import.meta.url), 'utf8');
const module = await import('data:text/javascript;base64,' + Buffer.from(source).toString('base64'));
let root = { isConnected: true }, replacements = [], handlers = new Map(), checks = 0;
globalThis.document = { getElementById: id => id === 'profile-settings' ? root : null };
globalThis.location = { pathname: '/profile/settings', hash: '', search: '?secret=not-forwarded', replace: value => replacements.push(value) };
globalThis.window = { addEventListener: (name, fn) => handlers.set(name, fn), removeEventListener: (name, fn) => { if (handlers.get(name) === fn) handlers.delete(name); } };
const check = (value, text) => { assert.ok(value, text); checks++; console.log('PASS ' + text); };
check(module.init() === false && handlers.size === 1, 'Normal profile navigation owns exactly one hash listener');
module.init(); check(handlers.size === 1, 'Reinitialization replaces rather than duplicates the listener');
for (const hash of ['#account-passkeys', '#profile-email-change']) {
 location.hash = hash; replacements = [];
 check(module.init() === true && replacements.length === 1 && replacements[0] === '/settings/account' + hash, 'Direct legacy link only forwards its exact known anchor: ' + hash);
 check(!replacements[0].includes('?'), 'Legacy redirect never forwards query material');
}
location.hash = ''; module.init(); location.hash = '#profile-email-change'; handlers.get('hashchange')();
check(replacements.at(-1) === '/settings/account#profile-email-change', 'Same-document hash change reaches the dedicated account page');
for (const hash of ['#profile-edit-heading', '#profile-qr-heading', '#unknown', '#https://other.invalid/', '#account-passkeys?next=outside', '#ACCOUNT-PASSKEYS']) {
 location.hash = hash; replacements = []; check(module.init() === false && replacements.length === 0, 'Unknown or profile anchor is never a destination: ' + hash);
}
location.hash = '#account-passkeys'; location.pathname = '/workspace'; replacements = [];
check(module.init() === false && replacements.length === 0, 'Compatibility never redirects another page');
location.pathname = '/profile/settings'; location.hash = ''; module.init(); root.isConnected = false; location.hash = '#account-passkeys'; handlers.get('hashchange')();
check(replacements.length === 0, 'Disconnected profile owner cannot redirect a later page');
module.cleanup(); check(handlers.size === 0, 'Cleanup removes the old page hash listener');
root = null; check(module.init() === false && handlers.size === 0, 'Missing profile root does not attach a listener');
delete globalThis.window; delete globalThis.document; delete globalThis.location;
console.log('Profile/account navigation checks passed: ' + checks);
