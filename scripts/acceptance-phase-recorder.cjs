'use strict';
const fs = require('node:fs');
const path = require('node:path');
// Durations/status only: never serialize actions, errors, URLs, actors or data.
module.exports = ({output, now = () => performance.now(), report = message => { if (process.env.GITHUB_ACTIONS === 'true') console.log(message); }}) => {
 const phases = [];
 const flush = () => {
  fs.mkdirSync(path.dirname(output), {recursive:true});
  fs.writeFileSync(output, JSON.stringify({schema:1, phases}, null, 2));
 };
 const begin = label => {
  if (!/^[a-z0-9-]{1,80}$/.test(label)) throw new Error('Invalid phase label');
  const start = now(); const item = {phase:label, status:'running', seconds:null};
  phases.push(item); flush(); let finished = false;
  return status => {
   if (finished) throw new Error('Phase already finished');
   if (!['success','failure'].includes(status)) throw new Error('Invalid phase status');
   finished = true;
   item.status = status; item.seconds = Math.max(0, Math.round((now()-start))/1000);
   flush();
   report('::notice title=Acceptance phase timing::'+label+';seconds='+item.seconds+';status='+status);
  };
 };
 return {begin, run: async (label, action) => {
  const finish = begin(label);
  try { const result = await action(); finish('success'); return result; }
  catch (error) { finish('failure'); throw error; }
 }};
};
