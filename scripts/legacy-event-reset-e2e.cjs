// CI only: no default command and no production data. Reuse native authenticated browsers.
module.exports = async ({ p, g, ok }) => {
  const snapshot = page => page.evaluate(async () => {
    const response = await fetch('/api/snapshot');
    if (!response.ok) throw Error('Snapshot rejected');
    return response.json();
  });
  const post = (page, action, body) => page.evaluate(async ({ action, body }) => {
    const { token } = await (await fetch('/api/csrf')).json();
    return (await fetch('/api/' + action, { method: 'POST',
      headers: { 'Content-Type': 'application/json', 'X-CSRF-TOKEN': token },
      body: JSON.stringify(body) })).status;
  }, { action, body });
  const initial = await snapshot(p);
  const manager = initial.people.find(u => u.displayName === '管理者');
  ok(!!manager, 'Reset acceptance identifies the synthetic event manager');
  ok(await post(p, 'create-event', { title: 'Disposable reset fixture', managerId: manager.id,
    starts: Math.floor(Date.now() / 1000) + 86400, capacity: 20 }) === 200,
    'Reset acceptance creates a synthetic old test event');
  const created = await snapshot(p);
  const event = created.events.find(e => e.title === 'Disposable reset fixture');
  ok(!!event && await post(p, 'publish-event', { id: event.id }) === 200 &&
    await post(g, 'join-event', { id: event.id }) === 200,
    'Reset acceptance includes a real participant and pending event reminder');
  const beforeAdmin = await snapshot(p), beforeGuest = await snapshot(g);
  require('child_process').execSync(process.env.SEM_LEGACY_RESET_COMMAND, { stdio: 'inherit' });
  let ready = false;
  for (let n = 0; n < 60; n++) {
    try { if ((await fetch('http://127.0.0.1:18080/health/ready')).ok) { ready = true; break; } } catch {}
    await new Promise(resolve => setTimeout(resolve, 1000));
  }
  ok(ready, 'Application restarts after explicit offline legacy reset');
  await p.goto('https://localhost:18443/'); await p.waitForSelector('#issue');
  await g.goto('https://localhost:18443/'); await g.waitForSelector('#request');
  const afterAdmin = await snapshot(p), afterGuest = await snapshot(g);
  ok(afterAdmin.events.length === 0 && afterGuest.events.length === 0,
    'Reset removes old events from both authenticated snapshots');
  ok(JSON.stringify(beforeAdmin.invites) === JSON.stringify(afterAdmin.invites) &&
    JSON.stringify(beforeAdmin.requests) === JSON.stringify(afterAdmin.requests) &&
    JSON.stringify(beforeAdmin.people) === JSON.stringify(afterAdmin.people),
    'Reset retains account identities, invitation uses and decisions');
  ok(JSON.stringify(beforeGuest.messages) === JSON.stringify(afterGuest.messages),
    'Reset retains the participant inbox and past event confirmation');
  ok(await post(g, 'join-event', { id: event.id }) === 400,
    'Deleted event cannot accept another participation');
  for (const [page, id, email] of [[p, 'issue', 'admin@example.test'], [g, 'request', 'member@example.test']]) {
    await page.locator('[data-action=logout]').click(); await page.waitForSelector('#login');
    await page.locator('#login [name=email]').fill(email); await page.locator('#login button').click();
    await page.waitForSelector('#' + id, { timeout: 20000 });
    ok(await page.locator('#' + id).isVisible(), 'Original native passkey authenticates after event reset: ' + email);
  }
  const messagesBefore = (await snapshot(g)).messages.filter(m => m.title === '予約通知テスト').length;
  ok(await post(g, 'schedule-test', { seconds: 10 }) === 200,
    'Reset does not disable scheduling unrelated notifications');
  await new Promise(resolve => setTimeout(resolve, 14000));
  ok((await snapshot(g)).messages.filter(m => m.title === '予約通知テスト').length > messagesBefore,
    'Notification worker executes a new unrelated reservation after reset');
};