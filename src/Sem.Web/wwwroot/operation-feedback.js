// Presentation only. Never initiates a save, retry, refresh, or credential operation.
let toast, modal, timer;
function closeModal(restore = true) {
    if (!modal) return;
    const current = modal; modal = undefined;
    if (current.node.open) current.node.close();
    current.node.remove();
    if (restore && current.focus?.isConnected && !current.focus.disabled) current.focus.focus({ preventScroll: true });
}
function closeToast() { clearTimeout(timer); toast?.node.remove(); toast = undefined; }
export function release(owner) {
    if (modal?.owner === owner) closeModal(false);
    if (toast?.owner === owner) closeToast();
}
export function notify(owner, kind, text) {
    if (!owner?.isConnected || typeof text !== 'string' || !text.trim()) return;
    const value = text.slice(0, 6000);
    if (kind === 'success') {
        closeToast();
        const node = document.createElement('aside');
        node.className = 'operation-toast';
        node.setAttribute('role', 'status'); node.setAttribute('aria-live', 'polite');
        const message = document.createElement('span'); message.textContent = value;
        const dismiss = document.createElement('button'); dismiss.type = 'button';
        dismiss.textContent = '閉じる'; dismiss.setAttribute('aria-label', '完了通知を閉じる');
        dismiss.addEventListener('click', closeToast);
        node.append(message, dismiss); document.body.append(node);
        node.addEventListener('click', closeToast);
        toast = { node, owner }; timer = setTimeout(closeToast, 3000);
        return;
    }
    const focus = modal?.focus || document.activeElement;
    closeToast(); closeModal(false);
    const node = document.createElement('dialog'); node.className = 'operation-result-dialog';
    node.setAttribute('aria-modal', 'true'); node.setAttribute('aria-labelledby', 'operation-result-title');
    const title = document.createElement('h2'); title.id = 'operation-result-title';
    title.textContent = kind === 'unknown' ? '結果が未確認です' : kind === 'partial' ? '一部の操作が完了しませんでした' : '操作を完了できませんでした';
    const message = document.createElement('p'); message.textContent = value;
    const guidance = document.createElement('p');
    guidance.textContent = kind === 'unknown' ? '保存済みの可能性があります。この案内を閉じ、元の画面の結果確認・同じ操作IDでの再試行手順に従ってください。自動再送は行っていません。' : kind === 'partial' ? '成功した操作は取り消されません。元の画面の項目別結果を確認してください。自動再送は行いません。' : '元の画面に詳しい結果と入力を残しています。内容を確認してから操作してください。';
    const close = document.createElement('button'); close.type = 'button'; close.textContent = '結果を確認して閉じる';
    close.addEventListener('click', () => closeModal());
    node.addEventListener('cancel', event => { event.preventDefault(); closeModal(); });
    node.append(title, message, guidance, close);
    document.body.append(node); modal = { owner, node, focus };
    if (typeof node.showModal === 'function') node.showModal(); else node.setAttribute('open', '');
    close.focus({ preventScroll: true });
}
if (typeof document !== 'undefined') {
    new MutationObserver(() => {
        if (modal && !modal.owner.isConnected) closeModal(false);
        if (toast && !toast.owner.isConnected) closeToast();
    }).observe(document.body, { childList: true, subtree: true });
}
