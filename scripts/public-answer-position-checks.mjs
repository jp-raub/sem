import assert from 'node:assert/strict';
import {clampPosition} from '../src/Sem.Web/wwwroot/public-answer-chart.js';
let checks=0;
for(const [x,y,w,h,vw,vh] of [[100,100,120,40,800,600],[790,590,240,100,800,600],[-9,-9,240,100,390,844],[380,830,240,100,390,844]]) {
 const p=clampPosition(x,y,w,h,vw,vh);
 assert(p.x>=8 && p.y>=8 && p.x+w<=vw-8 && p.y+h<=vh-8);checks++;
}
assert.deepEqual(clampPosition(100,100,120,40,800,600),{x:112,y:112});checks++;
console.log(`Public answer popup geometry checks passed: ${checks}`);
