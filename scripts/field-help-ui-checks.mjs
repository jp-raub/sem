import assert from 'node:assert/strict';
import fs from 'node:fs/promises';
const base = new URL('../src/Sem.Web/', import.meta.url);
const source = await fs.readFile(new URL('wwwroot/field-help.js', base), 'utf8');
const help = await import('data:text/javascript;base64,' + Buffer.from(source).toString('base64'));
const catalog = JSON.parse(await fs.readFile(new URL('wwwroot/i18n/field-help.ja.json', base), 'utf8'));
let fields = 0;
for (const file of await fs.readdir(new URL('Components/Pages/', base))) {
    if (!file.endsWith('.razor')) continue;
    const page = await fs.readFile(new URL('Components/Pages/' + file, base), 'utf8');
    for (const match of page.matchAll(/<FieldHelp HelpKey="([^"]+)" Label="([^"]+)"/g)) {
        assert.ok(Object.hasOwn(catalog, match[1]), `${file}: missing help ${match[1]}`);
        assert.ok(catalog[match[1]].length > 5 && match[2].length > 0);
        fields++;
    }
}
assert.ok(fields >= 50);
assert.equal(Object.keys(catalog).some(key => /mock|password/.test(key)), false);
const participation = await fs.readFile(new URL('Components/Pages/Participation.razor', base), 'utf8');
const questionStart = participation.indexOf('@foreach(var q in Plan.Questions');
assert.ok(questionStart >= 0);
const questions = participation.slice(questionStart, participation.indexOf('</section>', questionStart));
assert.equal(questions.includes('<FieldHelp'), false, 'No help on custom question answers');
const savedFetch = globalThis.fetch;
let calls = 0;
try {
    globalThis.fetch = async (url, options) => {
        calls++;
        assert.equal(url, '/i18n/field-help.ja.json');
        assert.equal(options.credentials, 'omit');
        assert.equal(options.mode, 'same-origin');
        assert.equal(options.redirect, 'error');
        return new Response('{', { headers: { 'Content-Type': 'application/json' } });
    };
    await assert.rejects(help.loadDictionary());
    globalThis.fetch = async () => { calls++; return new Response(JSON.stringify(catalog), { headers: { 'Content-Type': 'application/json' } }); };
    assert.deepEqual(await help.loadDictionary(), catalog);
    await help.loadDictionary();
    assert.equal(calls, 2, 'Only failed catalog retries; successful dictionary is cached');
} finally { globalThis.fetch = savedFetch; }
assert.equal(/innerHTML|localStorage|sessionStorage/.test(source), false);
assert.equal(/method\s*:\s*['"]POST/i.test(source), false);
console.log(`Field help synthetic checks passed: ${fields} explicit fields, catalog, no custom answers, safe text, retry/cache, no mutation requests.`);
