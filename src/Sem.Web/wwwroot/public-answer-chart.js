// Position and dismissal only; public encoded data and expanded state remain owned by Razor.
export function clampPosition(x, y, width, height, viewportWidth, viewportHeight) {
    return { x: Math.max(8, Math.min(x + 12, viewportWidth - width - 8)),
        y: Math.max(8, Math.min(y + 12, viewportHeight - height - 8)) };
}
function position(event) {
    const bar = event.target.closest?.('.public-answer-bar');
    if (!bar || bar.disabled) return;
    const group = bar.closest('.public-answer-group');
    const bounds = bar.getBoundingClientRect();
    const keyboard = event.type === 'focusin' || (event.type === 'click' && event.detail === 0);
    const x = !keyboard && Number.isFinite(event.clientX) ? event.clientX : bounds.left;
    const y = !keyboard && Number.isFinite(event.clientY) ? event.clientY : bounds.bottom;
    const panel = group.querySelector(bar.getAttribute('aria-expanded') === 'true'
        ? '.public-answer-respondents' : '.public-answer-tooltip');
    const rect = panel.getBoundingClientRect();
    const point = clampPosition(x, y, rect.width || 240, rect.height || 100, innerWidth, innerHeight);
    group.style.setProperty('--popup-x', `${point.x}px`);
    group.style.setProperty('--popup-y', `${point.y}px`);
}
if (typeof document !== 'undefined') {
    document.addEventListener('pointermove', event => { if (event.pointerType === 'mouse') position(event); }, {passive:true});
    document.addEventListener('pointerdown', position, {passive:true});
    document.addEventListener('focusin', position);
    document.addEventListener('click', position);
}

// Each chart owns its subscription. Never mutate hidden/ARIA or synthesize a toggle.
const dismissalBindings = new WeakMap();
export function releaseDismiss(root) {
    const binding = dismissalBindings.get(root);
    if (!binding) return;
    document.removeEventListener('pointerdown', binding.outside, true);
    document.removeEventListener('focusin', binding.outside, true);
    root.removeEventListener('focusout', binding.blur);
    binding.observer.disconnect();
    dismissalBindings.delete(root);
}
export function bindDismiss(root, reference) {
    if (!root?.isConnected || dismissalBindings.has(root)) return;
    const dismissOutside = target => {
        const bar = root.querySelector('.public-answer-bar[aria-expanded="true"]');
        const group = bar?.closest('.public-answer-group');
        // Keep pointer/focus on the trigger and all panel links usable. A different
        // group, another chart, a non-focusable background, or null focus dismisses.
        if (!group || (target && group.contains(target))) return;
        reference.invokeMethodAsync('Dismiss', root.dataset.popupGeneration).catch(() => {});
    };
    const outside = event => dismissOutside(event.target);
    const blur = event => dismissOutside(event.relatedTarget);
    const observer = new MutationObserver(() => { if (!root.isConnected) releaseDismiss(root); });
    observer.observe(document.body, {childList:true, subtree:true});
    document.addEventListener('pointerdown', outside, true);
    document.addEventListener('focusin', outside, true);
    root.addEventListener('focusout', blur);
    dismissalBindings.set(root, {outside, blur, observer});
}
