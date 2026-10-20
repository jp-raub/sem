'use strict';

// Runs only after the existing matrix fixture has finished all of its assertions.
// Reuses its disposable event; no production URLs, accounts or stored credentials.
module.exports = async ({ p, g, memberPage, adminPage, ok, api, requireOk, integrated, question, songs, guitar }) => {
    const random = () => require('crypto').randomUUID();
    const read = async (page, subject) => requireOk(await api(page, 'participation', undefined,
        integrated.query + (subject ? '&subject=' + encodeURIComponent(subject) : '')), 'Read summary fixture');
    const summary = page => page.locator('.entry-summary');
    const number = page => summary(page).locator('.entry-summary-number');
    const value = async page => (await number(page).innerText()).replace(/\s+/g, ' ').trim();
    const navigate = async page => {
        await page.goto('https://localhost:18443/events/' + integrated.id);
        await page.locator('[data-participation-interactive="true"]').waitFor();
        await summary(page).waitFor();
    };
    const setLimit = async maxPicks => {
        const plan = requireOk(await api(p, 'phases', undefined, integrated.query), 'Read summary phases');
        const active = plan.phases.find(x => x.id === member.phaseId && x.kind === 'Custom');
        if (!active) throw new Error('Summary fixture requires an existing custom phase');
        requireOk(await api(p, 'phases', {
            rowVersion: plan.rowVersion, manualOverride: true, activePhaseId: active.id,
            customPhases: plan.phases.filter(x => x.kind === 'Custom').map(x => ({
                id: x.id, name: x.name, maxPicks: x.id === active.id ? maxPicks : x.maxPicks,
                activateAt: x.activateAt, deactivateAt: x.deactivateAt,
            })),
        }, integrated.query), 'Set summary limit');
    };
    const waitNumber = async (page, text) => {
        await page.waitForFunction(text => document.querySelector('.entry-summary-number')?.textContent
            .replace(/\s+/g, ' ').trim() === text, text, { timeout: 25000 });
    };
    await navigate(memberPage);
    const member = await read(g);
    ok(member.ownPhaseSongCount === 1 && await value(memberPage) === '1 / 1 曲',
        'Summary shows persisted current-phase distinct songs after successful registration');
    ok((await summary(memberPage).innerText()).includes('曲数上限に達しています'),
        'Reached finite song limit is visible without disabling the existing answer action');
    ok(await summary(memberPage).locator('[role="meter"]').getAttribute('aria-valuenow') === '1',
        'Finite live summary exposes the saved count to assistive technology');

    await setLimit(-1);
    await navigate(memberPage);
    await navigate(adminPage);
    let plan = await read(g);
    const free = plan.slots.filter(x => x.assignedUserId === null).length;
    ok(await value(memberPage) === `${free} / ${plan.slots.length} 枠`,
        'Unlimited live summary matches event-wide free/total slots');
    const chosen = plan.slots.find(x => x.songId === songs[0].id && x.partName === guitar.name);
    if (!chosen || chosen.assignedUserId !== null) throw new Error('Expected free fixture instrument on member song');
    const cell = memberPage.locator(`[data-event-matrix-slot="${chosen.id}"]`);
    await cell.evaluate(e => e.scrollIntoView({ block: 'center', inline: 'end' }));
    await cell.focus(); await cell.press('Space');
    await memberPage.locator('.participation-selection-chip').waitFor();
    await memberPage.locator(`[data-participation-answer="${question.id}"]`).selectOption('NO');
    ok(await value(memberPage) === `${free} / ${plan.slots.length} 枠`,
        'Unsent selection and answer do not consume summary free slots');

    // The manager assigns the selected part to the same member: voice+instrument
    // on the same song must continue to count as one saved song.
    const manager = await read(p);
    requireOk(await api(p, 'participation', {
        requestId: random(), rowVersion: manager.rowVersion, participantVersion: member.participantVersion,
        phaseId: manager.phaseId, kind: 'Register', subjectId: member.ownUserId,
        slots: [{ id: chosen.id, rowVersion: chosen.rowVersion }], answers: [],
    }, integrated.query), 'Assign second part on the same song');
    await waitNumber(memberPage, `${free - 1} / ${plan.slots.length} 枠`);
    ok((await read(g)).ownPhaseSongCount === 1,
        'Voice plus instrument on the same song remains one authoritative registered song');
    ok(await memberPage.locator(`[data-participation-answer="${question.id}"]`).inputValue() === 'NO',
        'Polling updates summary without submitting or discarding the unsent answer');
    const notice = memberPage.locator('#participation-selection-notice');
    if (await notice.isVisible()) await memberPage.locator('#participation-selection-notice-close').click();

    // Select the member in the manager's real UI: no other person's saved answer
    // should be fetched or rendered just to display their count.
    await adminPage.locator('.participation-admin-tools > summary').click();
    await adminPage.locator('#participation-subject').selectOption(member.ownUserId);
    await adminPage.waitForFunction(() => document.querySelector('.entry-summary-subject')?.textContent.includes('選択中'));
    ok((await summary(adminPage).innerText()).includes('管理者が選択中の対象者')
        && await adminPage.locator(`[data-participation-answer="${question.id}"]`).inputValue() === '',
        'Admin subject summary switches subject without exposing the other person’s saved answer');

    // Releasing a part updates free/total through the existing automatic poll.
    const beforeRelease = await read(p);
    const occupied = beforeRelease.slots.find(x => x.songId === songs[1].id && x.partName === guitar.name);
    requireOk(await api(p, 'participation', {
        requestId: random(), rowVersion: beforeRelease.rowVersion, participantVersion: beforeRelease.participantVersion,
        phaseId: beforeRelease.phaseId, kind: 'Release', reason: 'Summary fixture release',
        slots: [{ id: occupied.id, rowVersion: occupied.rowVersion }], answers: [],
    }, integrated.query), 'Release other song for summary test');
    await waitNumber(memberPage, `${free} / ${plan.slots.length} 枠`);
    ok(await value(memberPage) === `${free} / ${plan.slots.length} 枠`,
        'Automatic poll updates event-wide free slots after a release');
    const target = await read(p, member.ownUserId);
    const second = target.slots.find(x => x.id === occupied.id);
    requireOk(await api(p, 'participation', {
        requestId: random(), rowVersion: target.rowVersion, participantVersion: target.participantVersion,
        phaseId: target.phaseId, kind: 'Register', subjectId: member.ownUserId,
        slots: [{ id: second.id, rowVersion: second.rowVersion }], answers: [],
    }, integrated.query), 'Assign second song under unlimited phase');
    ok((await read(g)).ownPhaseSongCount === 2, 'Second distinct song increases authoritative count to two');

    await setLimit(1);
    await navigate(memberPage);
    ok(await value(memberPage) === '2 / 1 曲'
        && await summary(memberPage).locator('[role="meter"]').getAttribute('aria-valuenow') === '1'
        && (await summary(memberPage).innerText()).includes('自動解除されません'),
        'Lowered limit preserves actual over-limit count and clamps only the visual/ARIA meter');
    await setLimit(0);
    await navigate(memberPage);
    ok(await value(memberPage) === '2 / 0 曲' && await summary(memberPage).locator('[role="meter"]').count() === 0
        && (await summary(memberPage).innerText()).includes('上限は0曲')
        && !(await summary(memberPage).innerText()).includes('パート登録受付中'),
        'Zero limit has explicit closed registration status and no invalid zero-maximum meter');
    ok(await memberPage.locator(`[data-participation-answer="${question.id}"]`).isEnabled()
        && await memberPage.locator('#participation-submit').isEnabled(),
        'Zero part limit does not disable the existing participant answer update');

    const phases = requireOk(await api(p, 'phases', undefined, integrated.query), 'Read hidden phase');
    const hidden = phases.phases.find(x => x.kind === 'Hidden');
    requireOk(await api(p, 'phases', {
        rowVersion: phases.rowVersion, manualOverride: true, activePhaseId: hidden.id,
        customPhases: phases.phases.filter(x => x.kind === 'Custom').map(x => ({
            id: x.id, name: x.name, maxPicks: x.maxPicks, activateAt: x.activateAt, deactivateAt: x.deactivateAt,
        })),
    }, integrated.query), 'Activate hidden summary fixture');
    await navigate(adminPage);
    ok((await summary(adminPage).innerText()).includes('非公開（管理者表示）')
        && !(await summary(adminPage).innerText()).includes('パート登録受付中'),
        'Manager hidden-phase summary does not imply a publicly open signup');
    ok((await api(g, 'participation', undefined, integrated.query)).status === 403,
        'Hidden event remains inaccessible to the ordinary member');

    // Later profile and retention checks reuse this event. Restore its original
    // assignments and phase instead of leaving it hidden or over the limit.
    await setLimit(-1);
    for (const original of member.slots) {
        const current = (await read(p)).slots.find(slot => slot.id === original.id);
        if (current.assignedUserId === original.assignedUserId) continue;
        if (current.assignedUserId !== null) {
            const owner = await read(p, current.assignedUserId);
            const slot = owner.slots.find(slot => slot.id === original.id);
            requireOk(await api(p, 'participation', {
                requestId: random(), rowVersion: owner.rowVersion, participantVersion: owner.participantVersion,
                phaseId: owner.phaseId, kind: 'Release', subjectId: current.assignedUserId,
                reason: 'Restore shared summary fixture', slots: [{ id: slot.id, rowVersion: slot.rowVersion }], answers: [],
            }, integrated.query), 'Restore fixture by releasing changed assignment');
        }
        if (original.assignedUserId !== null) {
            const owner = await read(p, original.assignedUserId);
            const slot = owner.slots.find(slot => slot.id === original.id);
            requireOk(await api(p, 'participation', {
                requestId: random(), rowVersion: owner.rowVersion, participantVersion: owner.participantVersion,
                phaseId: owner.phaseId, kind: 'Register', subjectId: original.assignedUserId,
                slots: [{ id: slot.id, rowVersion: slot.rowVersion }], answers: [],
            }, integrated.query), 'Restore original fixture owner');
        }
    }
    await setLimit(1);
    const restored = await read(g);
    ok(restored.phaseKind === member.phaseKind && restored.maxPicks === member.maxPicks
        && restored.ownPhaseSongCount === member.ownPhaseSongCount
        && restored.slots.every(slot => slot.assignedUserId === member.slots.find(original => original.id === slot.id).assignedUserId),
        'Summary checks restore the shared visible phase and original assignments for profile and retention checks');
};