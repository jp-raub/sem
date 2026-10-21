'use strict';
const assert=require('node:assert/strict'),fs=require('node:fs'),os=require('node:os'),path=require('node:path');
const create=require('./acceptance-phase-recorder.cjs');
(async()=>{
 const dir=fs.mkdtempSync(path.join(os.tmpdir(),'sem-phase-'));
 try {
  const output=path.join(dir,'phases.json');let t=0;
  const notices=[];const recorder=create({output,now:()=>t,report:message=>notices.push(message)});const read=()=>JSON.parse(fs.readFileSync(output));
  const end=recorder.begin('bootstrap-enrollment');
  assert.equal(read().phases[0].status,'running');
  t=1234;end('success');assert.equal(read().phases[0].seconds,1.234);
  assert.throws(()=>end('success'),/already/);
  assert.equal(await recorder.run('sample-checks',async()=>{t+=456;return 42;}),42);
  const error=new Error('PRIVATE-error-value');error.secret='PRIVATE-data-value';
  await assert.rejects(recorder.run('failure-checks',async()=>{t+=100;throw error;}),e=>e===error);
  assert.equal(read().phases[2].status,'failure');
  assert(!fs.readFileSync(output,'utf8').includes('PRIVATE'));
  assert.deepEqual(Object.keys(read()).sort(),['phases','schema']);
  assert(read().phases.every(p=>Object.keys(p).sort().join(',')==='phase,seconds,status'));
  assert.throws(()=>recorder.begin('https://private.invalid/?token=secret'),/Invalid/);
  await recorder.run('after-failure',async()=>{t+=1;});assert.equal(read().phases.at(-1).status,'success');
  assert(notices.every(n=>/^::notice title=Acceptance phase timing::[a-z0-9-]+;seconds=[0-9.]+;status=(success|failure)$/.test(n)));
  console.log('Acceptance phase recorder checks passed: 12');
 } finally {fs.rmSync(dir,{recursive:true,force:true});}
})().catch(error=>{console.error(error);process.exit(1);});
