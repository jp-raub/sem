import assert from 'node:assert/strict';
import { readFile } from 'node:fs/promises';
import { spawnSync } from 'node:child_process';

const root = new URL('../', import.meta.url);
const read = path => readFile(new URL(path, root), 'utf8');
const [js, css, page, app, nav, docs, ja] = await Promise.all([
  read('src/Sem.Web/wwwroot/theme-appearance.js'), read('src/Sem.Web/wwwroot/theme-appearance.css'),
  read('src/Sem.Web/Components/Pages/Appearance.razor'), read('src/Sem.Web/Components/App.razor'),
  read('src/Sem.Web/Components/Layout/ServiceNavigation.razor'), read('deploy/THEME-APPEARANCE-UI.md'), read('deploy/THEME-APPEARANCE-UI.ja.md')
]);
const syntax = spawnSync(process.execPath, ['--check', new URL('src/Sem.Web/wwwroot/theme-appearance.js', root).pathname], { encoding: 'utf8' });
assert.equal(syntax.status, 0, syntax.stderr);
for (const id of ['appearance-status','appearance-editor','appearance-scope','appearance-theme','appearance-mode','appearance-apply','appearance-cancel','appearance-refresh','theme-catalog-list','theme-catalog-add','theme-catalog-name','theme-catalog-accent','theme-catalog-background','theme-catalog-surface','theme-catalog-text','theme-catalog-save','theme-catalog-cancel']) assert(page.includes(`id="${id}"`), `missing SSR control ${id}`);
for (const path of ['theme-appearance','theme-personal','theme-event','theme-site','theme-catalog-save','theme-catalog-delete']) assert(js.includes(`'${path}'`) || js.includes(`API+'${path}'`), `missing API route ${path}`);
for (const invariant of ["credentials:'same-origin'", "redirect:'error'", "cache:'no-store'", "'X-CSRF-TOKEN'", '25000', 'MAX_JSON = 4 * 1024 * 1024', 'MAX_CSRF = 64 * 1024', 'unknownOutcome=true', 'expectedActor', "new TextDecoder('utf-8',{fatal:true})"]) assert(js.includes(invariant), `missing transport guard ${invariant}`);
assert(!/localStorage|sessionStorage|\.innerHTML\s*=/.test(js), 'no browser preference authority or unsafe HTML');
assert(js.includes('getReader()') && js.includes('validCatalogEntry'), 'streamed response bound and mutation DTO validation missing');
assert(js.includes('label.textContent=t.name') && js.includes("HEX = /^#[0-9a-fA-F]{6}$/"), 'catalog rendering/color validation must be safe');
assert(js.includes('events') && js.includes("searchParams.get('eventId')"), 'event context detection missing');
assert(js.includes("modeQuery.addEventListener('change'") && js.includes("appearanceMode"), 'system mode and explicit mode support missing');
assert(css.includes('#15201c') && css.includes('#202d27') && css.includes('#e1ebe5'), 'approved dark palette missing');
assert(css.includes('forced-colors:active') && css.includes('prefers-reduced-motion:reduce') && css.includes('focus-visible') && css.includes('max-width:480px'), 'accessibility/responsive styles missing');
assert(app.includes('theme-appearance.css') && app.includes('theme-appearance.js') && nav.includes('href="/appearance"'), 'app boot or menu integration missing');
assert(docs.includes('localStorage') && docs.includes('exactly-once') && ja.includes('localStorage') && ja.includes('exactly-once'), 'privacy/outcome caveats missing from both docs');
for (const mode of ['0','1','2']) assert(page.includes(`value="${mode}"`), 'mode options must match numeric HTTP contract');
assert(js.includes("mode === null ? 0 : mode") && js.includes("t.id==='green'"), 'clearing selections previews catalog Forest/System rather than old resolved values');
assert(js.includes('li.append(edit); if (!t.builtIn)'), 'all themes editable by admin, only custom deletable');
console.log('Theme appearance frontend contract checks passed.');
