// Fixed service fields only. No form values, custom questions, or mutation API is read.
let dictionaryPromise;
let active;
let sequence = 0;
export async function loadDictionary() {
    dictionaryPromise ??= fetch('/i18n/field-help.ja.json', {
        credentials: 'omit', mode: 'same-origin', redirect: 'error', cache: 'default'
    }).then(async response => {
        if (!response.ok || !response.headers.get('content-type')?.toLowerCase().startsWith('application/json'))
            throw new Error('Help catalog unavailable');
        const text = await response.text();
        if (text.length > 65536) throw new Error('Help catalog too large');
        const result = JSON.parse(text);
        if (!result || Array.isArray(result) || typeof result !== 'object' ||
            Object.values(result).some(value => typeof value !== 'string' || value.length > 2000))
            throw new Error('Invalid help catalog');
        return result;
    }).catch(error => { dictionaryPromise = undefined; throw error; });
    return dictionaryPromise;
}
export function closeHelp(restoreFocus = true) {
    if (!active) return;
    const { trigger, panel } = active;
    active = undefined;
    trigger.setAttribute('aria-expanded', 'false');
    trigger.removeAttribute('aria-controls');
    panel.remove();
    if (restoreFocus && trigger.isConnected) trigger.focus({ preventScroll: true });
}
async function openHelp(trigger) {
    if (active?.trigger === trigger) { closeHelp(); return; }
    closeHelp(false);
    const panel = document.createElement('section');
    panel.id = `field-help-panel-${++sequence}`;
    panel.className = 'field-help-panel';
    panel.setAttribute('role', 'dialog');
    panel.setAttribute('aria-modal', 'false');
    panel.setAttribute('popover', 'manual');
    const heading = document.createElement('h2');
    heading.id = `${panel.id}-title`;
    heading.textContent = `${trigger.dataset.fieldLabel || '項目'}の説明`;
    panel.setAttribute('aria-labelledby', heading.id);
    const text = document.createElement('p');
    text.textContent = '説明を読み込んでいます…';
    const close = document.createElement('button');
    close.type = 'button'; close.textContent = '説明を閉じる';
    close.addEventListener('click', () => closeHelp());
    panel.append(heading, text, close);
    // A popover inside its modal dialog remains outside the dialog's inert subtree.
    const parent = trigger.closest('dialog[open], [role="dialog"][aria-modal="true"]') || document.body;
    parent.append(panel);
    active = { trigger, panel };
    trigger.setAttribute('aria-expanded', 'true');
    trigger.setAttribute('aria-controls', panel.id);
    if (typeof panel.showPopover === 'function') panel.showPopover();
    else panel.classList.add('field-help-fallback');
    close.focus({ preventScroll: true });
    try {
        const dictionary = await loadDictionary();
        if (active?.panel !== panel) return;
        const key = trigger.dataset.fieldHelp;
        text.textContent = Object.hasOwn(dictionary, key) ? dictionary[key] : 'この項目の説明は準備中です。';
    } catch {
        if (active?.panel === panel) text.textContent = '説明を取得できません。閉じて、もう一度お試しください。入力内容は変更していません。';
    }
}
if (typeof document !== 'undefined') {
    document.addEventListener('click', event => {
        const trigger = event.target.closest?.('[data-field-help]');
        if (trigger && !trigger.disabled) {
            event.preventDefault(); event.stopPropagation();
            void openHelp(trigger); return;
        }
        if (active && !active.panel.contains(event.target)) closeHelp(false);
    }, true);
    document.addEventListener('keydown', event => {
        if (active && event.key === 'Escape') {
            event.preventDefault(); event.stopImmediatePropagation(); closeHelp();
        }
    }, true);
    document.addEventListener('focusin', event => {
        if (active && event.target !== active.trigger && !active.panel.contains(event.target)) closeHelp(false);
    });
    // Remove stale overlays on Blazor navigation or when an editor is unmounted.
    new MutationObserver(() => {
        if (active && (!active.trigger.isConnected || !active.panel.isConnected ||
            active.trigger.closest('[hidden]') || active.trigger.closest('dialog:not([open])'))) closeHelp(false);
    }).observe(document.body, { childList: true, subtree: true, attributes: true, attributeFilter: ['open', 'hidden'] });
    document.addEventListener('enhancedload', () => closeHelp(false));
}
