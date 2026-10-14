import assert from 'node:assert/strict';
import {parseCode,takeFragment} from '../src/Sem.Web/wwwroot/qr-reception.js';
const code='a'.repeat(64),origin='https://example.test',event='abc';let count=0;
function check(value,expected){assert.equal(parseCode(value,origin,event),expected);count++;}
check(code,code.toUpperCase());check(' '+origin+'/events/abc/reception#code='+code+' ',code.toUpperCase());
for(const value of [origin+'/events/other/reception#code='+code,'https://evil.invalid/events/abc/reception#code='+code,origin+'/events/abc/reception?token=x#code='+code,origin+'/events/abc/reception#code='+code+'&x=1','javascript:alert(1)',origin+'/events/abc/reception#code=bad','a'.repeat(63),origin.replace('https://','https://user@')+'/events/abc/reception#code='+code])check(value,null);
let cleared=false;globalThis.window={location:{hash:'#code='+code,pathname:'/events/abc/reception',search:''},history:{state:{safe:1},replaceState(s,t,path){assert.equal(path,'/events/abc/reception');cleared=true;}}};assert.equal(takeFragment(event),code.toUpperCase());assert.ok(cleared);count++;
window.location.pathname='/events/other/reception';window.history.replaceState=()=>{};assert.equal(takeFragment(event),null);count++;
console.log('QR input checks passed:',count);
