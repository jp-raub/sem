'use strict';
// Invoked only by an explicitly configured, disposable local/CI storage fixture.
// Never writes the captured JSON or authentication data to a shared result file.
module.exports = async ({p, g, ok}) => {
  const command = process.env.SEM_ACCEPTANCE_RETENTION_COMMAND;
  if (!command) throw new Error('An isolated retention fixture command is required');
  const capture = page => page.evaluate(async () => {
    const read = async path => {
      const response = await fetch(path);
      if (!response.ok) throw new Error('Retention read failed: '+response.status);
      return response.json();
    };
    const workspace = await read('/api/core/workspace');
    const events = [];
    for (const event of workspace.events) {
      const query = '?id='+encodeURIComponent(event.id);
      events.push({id:event.id,
        participation:await read('/api/core/participation'+query),
        timetable:await read('/api/core/timetable'+query),
        comments:await read('/api/core/comments'+query)});
    }
    return {workspace,events};
  });
  const before = [await capture(p),await capture(g)];
  ok(before[0].events.length>0 && before[0].events.some(e=>e.participation.slots.some(s=>s.assignedUserId)),
    'Retention fixture includes populated event assignments, not only an empty database');
  ok(before[0].events.some(e=>e.comments.comments.length>0),
    'Retention fixture includes posted comments before stopped backup');
  require('child_process').execSync(command,{stdio:'inherit',timeout:120000});
  const after = [await capture(p),await capture(g)];
  ok(JSON.stringify(before)===JSON.stringify(after),
    'Full-directory restore retains both live sessions, profiles, events, assignments, answers, comments and timetable settings');
};
