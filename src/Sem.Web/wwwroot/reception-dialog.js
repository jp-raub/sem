import { sync as syncDialog, release as releaseDialog } from './comment-dialog.js';
const returnUsers = new WeakMap();
export function sync(dialog, open, userId) {
  if (!(dialog instanceof HTMLDialogElement) || !dialog.isConnected) return;
  if (open && userId) returnUsers.set(dialog, userId);
  const closing = !open && dialog.open;
  syncDialog(dialog, open);
  if (closing) {
    // 受付後の一覧再取得で元のボタンが置換されても、同じ対象へ戻す。
    // セレクターへ値を連結せず、接続済みの現在の画面内だけを照合する。
    const owner = dialog.closest('#event-tab-panel');
    const id = returnUsers.get(dialog);
    const button = [...(owner?.querySelectorAll('[data-reception-select]') || [])]
      .find(el => el.dataset.receptionSelect === id && !el.matches(':disabled'));
    (button || owner?.querySelector('#reception-search'))?.focus();
    returnUsers.delete(dialog);
  }
}
export function release(dialog) {
  returnUsers.delete(dialog);
  releaseDialog(dialog);
}
