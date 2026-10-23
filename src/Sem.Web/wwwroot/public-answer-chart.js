// Position only; public, encoded answer data remains owned by Razor.
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
