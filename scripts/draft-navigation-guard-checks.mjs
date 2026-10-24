import assert from 'node:assert/strict';
import fs from 'node:fs/promises';
globalThis.location = new URL('https://localhost/events/test/phases');
const windowEvents=new EventTarget();globalThis.addEventListener=windowEvents.addEventListener.bind(windowEvents);globalThis.removeEventListener=windowEvents.removeEventListener.bind(windowEvents);
globalThis.document = new EventTarget(); globalThis.navigation = new EventTarget();
const module = await import('data:text/javascript;base64,'+Buffer.from(await fs.readFile(new URL('../src/Sem.Web/wwwroot/draft-navigation-guard.js',import.meta.url))).toString('base64'));
let blocked=0;const reference={invokeMethodAsync(name){assert.equal(name,'BlockNavigation');blocked++;return Promise.resolve();}};
function click(href,props={}) {
 const event=new Event('click',{cancelable:true});Object.assign(event,{button:0,...props});
 const link={href,target:props.linkTarget||'',hasAttribute:n=>n==='download'&&props.download};
 Object.defineProperty(event,'target',{value:{closest:()=>link}});document.dispatchEvent(event);return event.defaultPrevented;
}
module.sync('x',false,reference);assert.equal(click('/workspace'),false);
module.sync('x',true,reference);assert.equal(click('/workspace'),true);assert.equal(blocked,1);
assert.equal(click('/workspace',{ctrlKey:true}),false);assert.equal(click('/workspace',{linkTarget:'_blank'}),false);
assert.equal(click('/workspace',{download:true}),false);assert.equal(click('https://other.test/'),false);
assert.equal(click(location.href+'#section'),false);
const back=new Event('navigate',{cancelable:true});Object.assign(back,{navigationType:'traverse',destination:{url:'https://localhost/workspace'}});navigation.dispatchEvent(back);assert.equal(back.defaultPrevented,true);assert.equal(blocked,2);
const unload=new Event('beforeunload',{cancelable:true});windowEvents.dispatchEvent(unload);assert.equal(unload.defaultPrevented,true);
module.sync('x',false,reference);assert.equal(click('/workspace'),false);
module.sync('x',true,reference);module.release('x');module.release('x');assert.equal(click('/workspace'),false);
console.log('Draft navigation guard checks passed: 13');
