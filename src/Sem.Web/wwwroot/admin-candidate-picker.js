// Focus/dismiss only. Selection and persistence remain in the existing Razor owner.
const bindings=new WeakMap();
export function focusSearch(root){root?.querySelector('input')?.focus({preventScroll:true});}
export function release(root){const b=bindings.get(root);if(!b)return;document.removeEventListener('pointerdown',b.outside,true);document.removeEventListener('focusin',b.focus,true);root.removeEventListener('keydown',b.key);b.observer.disconnect();delete root.dataset.adminPickerInteractive;bindings.delete(root);}
export function bind(root,reference){
 release(root);if(!root?.isConnected)return;
 const dismiss=()=>{if(root.querySelector('input')?.getAttribute('aria-expanded')==='true')reference.invokeMethodAsync('Dismiss').catch(()=>{});};
 const outside=e=>{if(!root.contains(e.target))dismiss();};
 const focus=e=>{if(!root.contains(e.target))dismiss();};
 const key=e=>{
  const input=root.querySelector('input'),buttons=[...root.querySelectorAll('[data-admin-picker-candidate]')];
  if(e.key==='Enter'&&e.target===input){e.preventDefault();buttons[0]?.focus();return;}
  if(e.key==='Escape'){e.preventDefault();e.stopPropagation();focusSearch(root);dismiss();return;}
  if(!['ArrowDown','ArrowUp'].includes(e.key)||!buttons.length)return;
  const index=buttons.indexOf(document.activeElement);e.preventDefault();
  if(index<0){(e.key==='ArrowDown'?buttons[0]:buttons.at(-1)).focus();return;}
  const next=index+(e.key==='ArrowDown'?1:-1);if(next<0||next>=buttons.length)input.focus();else buttons[next].focus();
 };
 const observer=new MutationObserver(()=>{if(!root.isConnected)release(root);});observer.observe(document.body,{childList:true,subtree:true});
 document.addEventListener('pointerdown',outside,true);document.addEventListener('focusin',focus,true);root.addEventListener('keydown',key);bindings.set(root,{outside,focus,key,observer});root.dataset.adminPickerInteractive="true";
}
