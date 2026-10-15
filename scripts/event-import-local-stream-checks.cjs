'use strict';

const assert = require('node:assert/strict');

(async () => {
    const {
        attachComposer, detachComposer, lockComposer, readCsvInputStream,
        setNavigationWarning, snapshotPasteStream, syncTextarea
    } = await import('../src/Sem.Web/wwwroot/event-import.js');
    let checks = 0;
    const check = (condition, message) => { assert.ok(condition, message); checks++; };

    class Control {
        constructor() { this.listeners = new Map(); this.value = ''; this.files = []; this.disabled = false; this.readOnly = false; }
        addEventListener(type, fn) { this.listeners.set(type, fn); }
        removeEventListener(type, fn) { if (this.listeners.get(type) === fn) this.listeners.delete(type); }
        fire(type) { this.listeners.get(type)?.({ type, target: this }); }
    }
    const docListeners = new Map();
    const windowListeners = new Map();
    const add = store => (type, listener, capture = false) => store.set(`${type}:${capture}`, listener);
    const remove = store => (type, listener, capture = false) => {
        const key = `${type}:${capture}`;
        if (store.get(key) === listener) store.delete(key);
    };
    const originalDocument = global.document;
    const originalWindow = global.window;
    global.document = { addEventListener: add(docListeners), removeEventListener: remove(docListeners) };
    global.window = {
        location: { href: 'https://app.example/events/1/event-import', origin: 'https://app.example' },
        addEventListener: add(windowListeners), removeEventListener: remove(windowListeners)
    };

    const textarea = new Control();
    const fileInput = new Control();
    const calls = [];
    const dotnet = { invokeMethodAsync: async (...args) => { calls.push(args); } };
    const owner = 'local-stream-check';
    try {
        check(attachComposer(owner, textarea, fileInput, dotnet, 7) === 0, 'composer attaches with an initial revision');

        for (const size of [64 * 1024, 1048576]) {
            const paste = 'x'.repeat(size);
            textarea.value = paste;
            textarea.fire('input');
            await Promise.resolve();
            const callback = calls.at(-1);
            check(callback.length === 5 && callback[0] === 'OnPasteChanged'
                && callback.slice(1).every(value => typeof value === 'number' || typeof value === 'boolean'),
                `${size}-byte input sends compact metadata only`);
            check(!callback.some(value => typeof value === 'string' && value === paste),
                `${size}-byte input text never enters the .NET callback`);
            const bytes = await snapshotPasteStream(owner);
            check(bytes instanceof Uint8Array && bytes.byteLength === size,
                `${size}-byte paste is returned as bounded stream bytes`);
            check(new TextDecoder('utf-8', { fatal: true }).decode(bytes) === paste,
                `${size}-byte paste retains exact UTF-8 content`);
            lockComposer(owner, false);
        }

        for (const size of [64 * 1024, 1048576]) {
            const content = new Uint8Array(size).fill(120);
            const file = { size, async arrayBuffer() { return content.buffer.slice(0); } };
            fileInput.files = [file];
            fileInput.value = 'chosen.csv';
            const streamBytes = await readCsvInputStream(fileInput);
            check(streamBytes instanceof Uint8Array && streamBytes.byteLength === size,
                `${size}-byte local file returns bounded stream bytes`);
            check(streamBytes[0] === 120 && streamBytes.at(-1) === 120,
                `${size}-byte local file retains exact boundary bytes`);
            check(fileInput.value === '', 'file selector value is cleared after reading');
        }

        textarea.value = 'x'.repeat(1048577);
        textarea.fire('input');
        await Promise.resolve();
        check(calls.at(-1)[4] === false, 'paste over 1 MiB is reported invalid through metadata');
        await assert.rejects(snapshotPasteStream(owner), /invalid or exceeds 1 MiB/);
        checks++;
        lockComposer(owner, false);
        check(!textarea.readOnly, 'failed oversized paste can be unlocked for correction');

        textarea.value = '\ud800';
        textarea.fire('input');
        await Promise.resolve();
        check(calls.at(-1)[4] === false, 'unpaired surrogate is reported invalid before TextEncoder replacement');
        await assert.rejects(snapshotPasteStream(owner), /invalid or exceeds 1 MiB/);
        checks++;
        lockComposer(owner, false);

        const oversized = { size: 1048577, async arrayBuffer() { throw new Error('must reject before reading'); } };
        fileInput.files = [oversized];
        await assert.rejects(readCsvInputStream(fileInput), /1 MiB/);
        checks++;

        const invalidUtf8 = { size: 2, async arrayBuffer() { return Uint8Array.from([0xc3, 0x28]).buffer; } };
        fileInput.files = [invalidUtf8];
        await assert.rejects(readCsvInputStream(fileInput), /encoded data/);
        checks++;
        check(fileInput.value === '', 'invalid UTF-8 file selection is cleared');

        const priorCalls = calls.length;
        const syncRevision = syncTextarea(owner, 'A,B\n1,2');
        check(calls.length === priorCalls, 'programmatic textarea sync does not dispatch input metadata');
        check(textarea.value === 'A,B\n1,2', 'programmatic sync updates the visible textarea');
        check(docListeners.has('click:true') && windowListeners.has('beforeunload:false'),
            'nonempty browser input enables owner-scoped navigation warnings');
        const lastRevision = calls.at(-1)[2];
        textarea.value = '';
        textarea.fire('input');
        await Promise.resolve();
        check(syncRevision > lastRevision && calls.at(-1)[2] > syncRevision && calls.at(-1)[3] === false,
            'edits acknowledge increasing revisions and empty-input metadata');
        const beforeDetach = calls.at(-1)[2];
        detachComposer(owner);
        const replacementTextarea = new Control();
        const replacementFile = new Control();
        check(attachComposer(owner, replacementTextarea, replacementFile, dotnet, 7) === beforeDetach,
            'recreated textarea retains the owner revision baseline');
        replacementTextarea.value = 'recovered CSV';
        replacementTextarea.fire('input');
        await Promise.resolve();
        check(calls.at(-1)[2] > beforeDetach && calls.at(-1)[3] === true,
            'callbacks from recreated textarea advance beyond stale callbacks');
        detachComposer(owner);
        detachComposer(owner, true);
        setNavigationWarning(owner, false);
        check(textarea.listeners.size === 0 && fileInput.listeners.size === 0,
            'composer listeners are removed on detach');
        check(!docListeners.has('click:true') && !windowListeners.has('beforeunload:false'),
            'navigation guards are removed after the last owner clears');
    } finally {
        detachComposer(owner);
        setNavigationWarning(owner, false);
        if (originalDocument === undefined) delete global.document; else global.document = originalDocument;
        if (originalWindow === undefined) delete global.window; else global.window = originalWindow;
    }

    console.log(`event-import-local-stream-checks: ${checks} assertions passed (synthetic browser controls only)`);
})().catch(error => {
    console.error(error);
    process.exitCode = 1;
});