'use strict';

// Independent disposable event: does not change the shared matrix fixture.
module.exports = async ({ p, g, memberPage, adminPage, ok, api, requireOk, event }) => {
    const random = () => require('crypto').randomUUID();
    const fixture = await event('Native atomic quota and release reset ' + random().slice(0, 8));
    const defs = requireOk(await api(p, 'definitions', undefined, fixture.query), 'Read quota definitions');
    const voice = defs.definitions[0];
    const songs = [0, 1, 2].map(index => ({
        id: random(), title: 'Atomic quota song ' + index, artist: 'Fixture',
        performanceSeconds: 60, prepSeconds: null, definitionIds: [voice.id],
    }));
    const songPlan = requireOk(await api(p, 'songs', undefined, fixture.query), 'Read quota songs');
    requireOk(await api(p, 'songs', {
        rowVersion: songPlan.rowVersion, defaultPrepSeconds: 600, songs,
    }, fixture.query), 'Create three quota songs');
    const question = { id: random(), text: 'Atomic quota answer', type: 'YesNo', required: true };
    const questions = requireOk(await api(p, 'questions', undefined, fixture.query), 'Read quota questions');
    requireOk(await api(p, 'questions', {
        rowVersion: questions.rowVersion, questions: [question],
    }, fixture.query), 'Create quota question');
    const phases = requireOk(await api(p, 'phases', undefined, fixture.query), 'Read quota phase');
    const phase = { id: random(), name: 'Two-song quota', maxPicks: 2, activateAt: null, deactivateAt: null };
    requireOk(await api(p, 'phases', {
        rowVersion: phases.rowVersion, manualOverride: true, activePhaseId: phase.id, customPhases: [phase],
    }, fixture.query), 'Enable two-song quota');
    const read = async () => requireOk(await api(g, 'participation', undefined, fixture.query), 'Read quota participation');
    const comments = async () => requireOk(await api(g, 'comments', undefined, fixture.query), 'Read quota comments');
    const slotButton = (page, slot) => page.locator(`[data-event-matrix-slot="${slot.id}"]`);
    await memberPage.goto('https://localhost:18443/events/' + fixture.id);
    await memberPage.locator('[data-participation-interactive="true"]').waitFor();
    const before = await read();
    const commentBefore = await comments();
    for (const slot of before.slots) await slotButton(memberPage, slot).click();
    await memberPage.locator(`[data-participation-answer="${question.id}"]`).selectOption('YES');
    await memberPage.locator('#participation-comment').fill('Atomic quota must not partially post');
    await memberPage.locator('#participation-submit').click();
    await memberPage.waitForFunction(() => document.querySelector('#participation-error')?.textContent.includes('曲数上限'));
    const rejected = await read();
    ok(JSON.stringify(rejected) === JSON.stringify(before) && JSON.stringify(await comments()) === JSON.stringify(commentBefore),
        'Actual three-song UI submit at limit two changes no slots, versions, saved answers or comments');
    ok(await memberPage.locator('.participation-selection-chip').count() === 3
        && await memberPage.locator(`[data-participation-answer="${question.id}"]`).inputValue() === 'YES'
        && await memberPage.locator('#participation-comment').inputValue() === 'Atomic quota must not partially post',
        'Quota error keeps all unsent choices, answer and comment available for correction');
    ok((await memberPage.locator('#participation-message').innerText()).trim() === '',
        'Quota rejection displays an error, not a partial-success message');
    await memberPage.locator('.operation-result-dialog button').click();
    await memberPage.locator('.operation-result-dialog').waitFor({ state: 'detached' });
    await memberPage.locator('.participation-selection-chip button').last().click();
    await memberPage.locator('#participation-comment').fill('');
    await memberPage.locator('#participation-submit').click();
    await memberPage.waitForFunction(() => document.querySelector('#participation-message')?.textContent.includes('2 件成功'));
    const saved = await read();
    ok(saved.ownPhaseSongCount === 2 && saved.slots.filter(slot => slot.assignedUserId === saved.ownUserId).length === 2
        && saved.questions[0].ownValue === 'YES',
        'Reducing the same draft to two songs registers both and saves its answer');

    await adminPage.goto('https://localhost:18443/events/' + fixture.id);
    await adminPage.locator('[data-participation-interactive="true"]').waitFor();
    await adminPage.locator('.participation-admin-tools > summary').click();
    await adminPage.locator('#participation-subject').selectOption(saved.ownUserId);
    await adminPage.waitForFunction(() => document.querySelector('.entry-summary-subject')?.textContent.includes('選択中'));
    const assigned = saved.slots.find(slot => slot.assignedUserId === saved.ownUserId);
    const mode = adminPage.locator('#participation-release-mode');
    const reason = adminPage.locator('#participation-reason');
    const chooseRelease = async () => {
        await mode.check();
        await slotButton(adminPage, assigned).click();
        await adminPage.waitForFunction(() => document.querySelector('#participation-submit')?.textContent.includes('登録を解除'));
    };
    // A known server-side validation failure must reset the visible draft too.
    await chooseRelease();
    await adminPage.locator('#participation-submit').click();
    await adminPage.waitForFunction(() => document.querySelector('#participation-error')?.textContent.includes('理由'));
    ok(!await mode.isChecked() && await reason.inputValue() === ''
        && (await read()).slots.find(slot => slot.id === assigned.id).assignedUserId === saved.ownUserId,
        'Rejected reasonless release resets visible mode/reason but leaves the assignment unchanged');
    await adminPage.locator('.operation-result-dialog button').click();
    await adminPage.locator('.operation-result-dialog').waitFor({ state: 'detached' });
    await chooseRelease();
    await reason.fill('Native explicit release reset reason');
    await adminPage.locator('#participation-submit').click();
    await adminPage.waitForFunction(() => !document.querySelector('#participation-release-mode')?.checked
        && document.querySelector('#participation-reason')?.value === '');
    await adminPage.waitForFunction(() => document.querySelector('#participation-message')?.textContent.includes('1 件成功'));
    const released = await read();
    ok(!await mode.isChecked() && await reason.inputValue() === ''
        && released.slots.find(slot => slot.id === assigned.id).assignedUserId === null
        && released.slots.filter(slot => slot.assignedUserId === saved.ownUserId).length === 1,
        'Successful real admin release clears mode/reason and releases only the captured selected slot');
    ok(released.questions[0].ownValue === 'YES',
        'Admin release reset does not overwrite the subject’s saved answer');
};