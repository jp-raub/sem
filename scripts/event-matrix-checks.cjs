'use strict';

const fs = require('node:fs');
const path = require('node:path');

module.exports = async ({ p, g, ok }) => {
    const origin = 'https://localhost:18443';
    const api = (page, action, data, query = '') =>
        page.evaluate(async ({ action, data, query }) => {
            const { token } = await (await fetch('/api/csrf')).json();
            const response = await fetch('/api/core/' + action + query, {
                method: data === undefined ? 'GET' : 'POST',
                headers: data === undefined ? {} : {
                    'Content-Type': 'application/json',
                    'X-CSRF-TOKEN': token,
                },
                body: data === undefined ? undefined : JSON.stringify(data),
            });
            return { status: response.status, body: await response.json() };
        }, { action, data, query });
    const random = () => require('crypto').randomUUID();
    const requireOk = (response, label) => {
        if (response.status !== 200) {
            throw new Error(`${label}: HTTP ${response.status} ${JSON.stringify(response.body)}`);
        }
        return response.body;
    };
    const event = async title => {
        const created = requireOk(await api(p, 'event', {
            title,
            description: '',
            location: '',
            scheduledDate: new Date(Date.now() + 86400000).toISOString(),
            opensAt: null,
            announcementMessage: '',
            phaseKind: 'Hidden',
            managerIds: [],
            rowVersion: 0,
        }), 'Create matrix fixture event');
        return { id: created.id, query: '?id=' + created.id };
    };
    const timetableDraft = (plan, extra = {}) => ({
        rowVersion: plan.rowVersion,
        roundStarts: plan.roundStarts,
        clockFormat: plan.clockFormat,
        durationFormat: plan.durationFormat,
        songIcon: plan.songIcon,
        plannedEndAt: plan.plannedEndAt,
        parts: plan.parts,
        ...extra,
    });
    const setCustomPhase = async query => {
        const phases = requireOk(await api(p, 'phases', undefined, query), 'Read custom phase fixture');
        const active = {
            id: random(),
            name: 'Native matrix signup',
            maxPicks: 1,
            activateAt: null,
            deactivateAt: null,
        };
        requireOk(await api(p, 'phases', {
            rowVersion: phases.rowVersion,
            manualOverride: true,
            activePhaseId: active.id,
            customPhases: [active],
        }, query), 'Enable custom signup phase');
    };
    const readParticipation = async (page, query) =>
        requireOk(await api(page, 'participation', undefined, query), 'Read participation plan');
    const slotFor = (plan, songId, partName) => {
        const found = plan.slots.find(slot => slot.songId === songId && slot.partName === partName);
        if (!found) throw new Error(`Missing fixture slot for ${songId}/${partName}`);
        return found;
    };
    const duplicates = async page => page.locator('[id]').evaluateAll(elements => {
        const counts = new Map();
        for (const element of elements) counts.set(element.id, (counts.get(element.id) || 0) + 1);
        return [...counts].filter(([, count]) => count > 1).map(([id]) => id);
    });
    const participationSource = fs.readFileSync(path.resolve(
        __dirname, '..', 'src', 'Sem.Web', 'Components', 'Pages', 'Participation.razor'), 'utf8');
    ok(participationSource.includes('if(!Embedded||rows is null||rows.Count==0)return Plan.Slots;')
        && participationSource.includes('bool ShowFallbackTable=>!Embedded||MatrixRows is null||MatrixRows.Count==0||FallbackSlots.Count>0;'),
    'Null or empty timed rows retain the complete existing participation slot table');
    ok(participationSource.includes('return Plan.Slots.Where(slot=>!timedSongIds.Contains(slot.SongId)).ToArray();')
        && participationSource.includes('@foreach(var p in FallbackSlots)')
        && participationSource.includes('disabled="@SlotDisabled(p)"')
        && participationSource.includes('@onchange="a=>Toggle(p.Id,a.Value is true)"'),
    'Only slots for songs absent from timed rows use the existing table, toggle and disabled rule');

    const suffix = random().slice(0, 8);
    const integrated = await event('Native event matrix ' + suffix);
    let definitions = requireOk(await api(p, 'definitions', undefined, integrated.query), 'Read matrix part definitions');
    const voice = definitions.definitions[0];
    const guitar = { id: random(), name: 'Native matrix guitar', category: 'Play' };
    definitions = requireOk(await api(p, 'definitions', {
        rowVersion: definitions.rowVersion,
        definitions: [
            { id: voice.id, name: voice.name, category: voice.category },
            guitar,
        ],
    }, integrated.query), 'Add matrix part definition');

    const songs = [
        { id: random(), title: 'Native matrix song A', artist: 'Fixture artist', performanceSeconds: 180, prepSeconds: null, definitionIds: [voice.id, guitar.id] },
        { id: random(), title: 'Native matrix song B', artist: 'Fixture artist', performanceSeconds: 150, prepSeconds: null, definitionIds: [voice.id, guitar.id] },
    ];
    const songPlan = requireOk(await api(p, 'songs', undefined, integrated.query), 'Read matrix songs');
    requireOk(await api(p, 'songs', {
        rowVersion: songPlan.rowVersion,
        defaultPrepSeconds: 600,
        songs,
    }, integrated.query), 'Create matrix fixture songs');
    const questionPlan = requireOk(await api(p, 'questions', undefined, integrated.query), 'Read matrix questions');
    const question = { id: random(), text: 'Native matrix required answer', type: 'YesNo', required: true };
    requireOk(await api(p, 'questions', {
        rowVersion: questionPlan.rowVersion,
        questions: [question],
    }, integrated.query), 'Create matrix fixture question');
    await setCustomPhase(integrated.query);

    let timetable = requireOk(await api(p, 'timetable', undefined, integrated.query), 'Read matrix timetable');
    const originalTransition = {
        id: random(),
        kind: 'Transition',
        songId: null,
        name: 'Native matrix callback before',
        durationSeconds: 31,
        icon: null,
    };
    timetable = requireOk(await api(p, 'timetable', timetableDraft(timetable, {
        parts: [...timetable.parts, originalTransition],
    }), integrated.query), 'Add matrix progress row');

    const memberPlan = await readParticipation(g, integrated.query);
    const memberVoiceSlot = slotFor(memberPlan, songs[0].id, voice.name);
    const managerVoiceSlot = slotFor(memberPlan, songs[1].id, voice.name);
    const memberPage = await g.context().newPage();
    const adminPage = await p.context().newPage();
    const errors = [];
    for (const page of [memberPage, adminPage]) page.on('pageerror', error => errors.push(error.message));
    try {
        await memberPage.setViewportSize({ width: 390, height: 844 });
        await memberPage.goto(origin + '/events/' + integrated.id);
        await memberPage.locator('#participation-submit').waitFor();
        // Match standalone UI tests: SSR controls can appear before the interactive circuit.
        await memberPage.locator('[data-participation-interactive="true"]').waitFor();
        await memberPage.locator('.event-part-matrix').waitFor();
        await memberPage.locator(`[data-event-matrix-slot="${memberVoiceSlot.id}"]`).waitFor();
        ok(await memberPage.locator('.participation-entry-eyebrow').innerText() === 'YOUR ENTRY',
            'Integrated event displays the matrix and YOUR ENTRY panel');
        const scrolls = await memberPage.locator('.event-part-matrix-scroll').evaluate(element => ({
            client: element.clientWidth,
            scroll: element.scrollWidth,
        }));
        ok(scrolls.scroll > scrolls.client,
            `Narrow viewport keeps the matrix horizontally scrollable (${scrolls.client}/${scrolls.scroll})`);
        ok(await memberPage.locator('#timetable-refresh').count() === 1,
            'Embedded timetable exposes one refresh action for callback-state check');

        await memberPage.locator(`[data-event-matrix-slot="${memberVoiceSlot.id}"]`).evaluate(e=>e.scrollIntoView({block:"center",inline:"end"}));
        // Exercise the real keyboard path after horizontal panning. Do not retry a
        // toggle: a delayed first activation could otherwise select and then clear it.
        const firstCell = memberPage.locator(`[data-event-matrix-slot="${memberVoiceSlot.id}"]`);
        await firstCell.focus();
        await firstCell.press('Space');
        await memberPage.waitForFunction(() => document.querySelectorAll('.participation-selection-chip').length === 1)
            .catch(async error => {
                const state = await firstCell.evaluate(element => ({
                    disabled: element.matches(':disabled'),
                    pressed: element.getAttribute('aria-pressed'),
                    focused: document.activeElement === element,
                    interactive: element.closest('[data-participation-interactive]')?.getAttribute('data-participation-interactive'),
                    selections: document.querySelectorAll('.participation-selection-chip').length,
                }));
                throw new Error('Matrix keyboard selection state '+JSON.stringify(state)+'; '+error.message);
            });
        await memberPage.locator(`[data-participation-answer="${question.id}"]`).selectOption('YES');
        ok(await memberPage.locator('.participation-selection-chip').count() === 1,
            'Selecting a matrix cell updates the existing YOUR ENTRY selection chip');

        const refreshedName = 'Native matrix callback after';
        const latestTimetable = requireOk(await api(p, 'timetable', undefined, integrated.query), 'Read timetable before callback refresh');
        timetable = requireOk(await api(p, 'timetable', timetableDraft(latestTimetable, {
            parts: latestTimetable.parts.map(part =>
                part.id === originalTransition.id ? { ...part, name: refreshedName } : part),
        }), integrated.query), 'Update fixture progress row for callback refresh');
        await memberPage.locator('#timetable-refresh').click();
        await memberPage.waitForFunction(name =>
            [...document.querySelectorAll('.event-matrix-transition')].some(row => row.textContent.includes(name)),
        refreshedName);
        ok(await memberPage.locator('.participation-selection-chip').count() === 1
            && await memberPage.locator(`[data-participation-answer="${question.id}"]`).inputValue() === 'YES',
        'Timetable rows callback preserves unsaved participation selection and answer');

        await memberPage.locator('#participation-submit').click();
        const conflictText = 'ほかの画面で更新されています。最新状態を取得し、変更内容を確認してから保存してください。';
        await memberPage.waitForFunction(text => document.getElementById('participation-error').textContent.trim() === text,
            conflictText);
        ok(await memberPage.locator('.participation-selection-chip').count() === 1
            && await memberPage.locator(`[data-participation-answer="${question.id}"]`).inputValue() === 'YES',
        'Participation conflict keeps the unsaved selection and answer for explicit review');
        let afterConflict = await readParticipation(g, integrated.query);
        ok(afterConflict.ownPhaseSongCount === 0
            && afterConflict.slots.find(slot => slot.id === memberVoiceSlot.id)?.assignedUserId == null,
        'Stale participation RowVersion conflicts without registering the selected slot');

        await memberPage.locator('.operation-result-dialog').waitFor();
        ok((await memberPage.locator('.operation-result-dialog').innerText()).includes(conflictText),
            'Conflict dialog retains exact error and does not silently refresh the draft');
        await memberPage.locator('.operation-result-dialog button').click();
        await memberPage.locator('#participation-refresh').click();
        await memberPage.waitForFunction(() => document.getElementById('participation-error').textContent.trim() === '');
        ok(await memberPage.locator('.participation-selection-chip').count() === 0
            && await memberPage.locator(`[data-participation-answer="${question.id}"]`).inputValue() === '',
        'Explicit latest-state refresh replaces stale unsaved selection and answer with current data');
        await memberPage.locator(`[data-event-matrix-slot="${memberVoiceSlot.id}"]`).evaluate(e=>e.scrollIntoView({block:"center",inline:"end"}));
        await memberPage.locator(`[data-event-matrix-slot="${memberVoiceSlot.id}"]`).click();
        await memberPage.waitForFunction(() => document.querySelectorAll('.participation-selection-chip').length === 1);
        await memberPage.locator(`[data-participation-answer="${question.id}"]`).selectOption('YES');
        await memberPage.locator('#participation-submit').click();
        await memberPage.waitForFunction(() => document.getElementById('participation-message').textContent.includes('1 件成功'));
        const afterRetry = await readParticipation(g, integrated.query);
        ok(afterRetry.ownPhaseSongCount === 1
            && afterRetry.slots.find(slot => slot.id === memberVoiceSlot.id)?.assignedUserId === afterRetry.ownUserId
            && afterRetry.questions.find(item => item.id === question.id)?.ownValue === 'YES',
        'Reselected slot and answer submit successfully after explicit latest-state refresh');
        let managerPlan = await readParticipation(p, integrated.query);
        const assignedMemberSlot = managerPlan.slots.find(slot => slot.id === memberVoiceSlot.id);
        if (!assignedMemberSlot?.assignedName) throw new Error('Expected public assigned name in manager participation plan');
        await adminPage.setViewportSize({ width: 1200, height: 900 });
        await adminPage.goto(origin + '/events/' + integrated.id);
        await adminPage.locator('#participation-submit').waitFor();
        // SSR markup precedes the interactive Blazor circuit, as on the existing standalone checks.
        await adminPage.locator('[data-participation-interactive="true"]').waitFor();
        await adminPage.locator(`[data-event-matrix-slot="${memberVoiceSlot.id}"]`).waitFor();
        const otherText = await adminPage.locator(`[data-event-matrix-slot="${memberVoiceSlot.id}"]`).innerText();
        ok(otherText.includes('他人:') && otherText.includes(assignedMemberSlot.assignedName),
            'Assigned matrix cell visibly identifies another participant and shows their assigned name');

        const adminAnswer = adminPage.locator(`[data-participation-answer="${question.id}"]`);
        await adminAnswer.selectOption('NO');
        await adminPage.locator(`[data-event-matrix-slot="${managerVoiceSlot.id}"]`).evaluate(e=>e.scrollIntoView({block:"center",inline:"end"}));
        await adminPage.locator(`[data-event-matrix-slot="${managerVoiceSlot.id}"]`).click();
        await adminPage.waitForFunction(() => document.querySelectorAll('.participation-selection-chip').length === 1);
        ok(await adminAnswer.inputValue() === 'NO', 'Admin self-registration retains the selected answer before submit');
        await adminPage.locator('#participation-submit').click();
        try {
            await adminPage.waitForFunction(() => document.getElementById('participation-message').textContent.includes('1 件成功'));
        } catch (error) {
            const diagnostic = await adminPage.evaluate(() => ({
                message: document.getElementById('participation-message')?.textContent,
                error: document.getElementById('participation-error')?.textContent,
                selected: document.querySelectorAll('.participation-selection-chip').length,
                disabled: document.getElementById('participation-submit')?.disabled,
            }));
            throw new Error('Admin matrix submit did not complete: ' + JSON.stringify(diagnostic), { cause: error });
        }
        managerPlan = await readParticipation(p, integrated.query);
        const assignedManagerSlot = managerPlan.slots.find(slot => slot.id === managerVoiceSlot.id);
        if (!assignedManagerSlot?.assignedName) throw new Error('Expected own assigned name in manager participation plan');
        const ownText = await adminPage.locator(`[data-event-matrix-slot="${managerVoiceSlot.id}"]`).innerText();
        ok(ownText.includes('自分:') && ownText.includes(assignedManagerSlot.assignedName),
            'Assigned matrix cell visibly identifies self and shows the assigned name');

        const duplicateIds = await duplicates(memberPage);
        ok(duplicateIds.length === 0, 'Integrated event has no duplicate DOM ids: ' + duplicateIds.join(', '));
        ok(await memberPage.locator('title').count() === 1,
            'Embedded children do not add duplicate document titles');

        await adminPage.goto(origin + '/events/' + integrated.id + '/timetable');
        await adminPage.locator('#timetable-edit').waitFor();
        ok(new URL(adminPage.url()).pathname === `/events/${integrated.id}/timetable`
            && await adminPage.locator('.event-part-matrix').count() === 0,
        'Legacy dedicated timetable route remains available without the embedded matrix');
        const timetableDuplicateIds = await duplicates(adminPage);
        ok(timetableDuplicateIds.length === 0,
            'Legacy timetable route has no duplicate DOM ids: ' + timetableDuplicateIds.join(', '));
        await memberPage.goto(origin + '/events/' + integrated.id + '/participation');
        await memberPage.locator('#participation-submit').waitFor();
        ok(new URL(memberPage.url()).pathname === `/events/${integrated.id}/participation`
            && await memberPage.locator('.event-part-matrix').count() === 0
            && await memberPage.locator('[data-participation-select]').count() === memberPlan.slots.length,
        'Legacy dedicated participation route remains available with its full slot table');
        const participationDuplicateIds = await duplicates(memberPage);
        ok(participationDuplicateIds.length === 0,
            'Legacy participation route has no duplicate DOM ids: ' + participationDuplicateIds.join(', '));

        const progressOnly = await event('Native event matrix progress only ' + suffix);
        let emptyTimetable = requireOk(await api(p, 'timetable', undefined, progressOnly.query), 'Read progress-only timetable');
        const progress = {
            id: random(),
            kind: 'Transition',
            songId: null,
            name: 'Native progress without part columns',
            durationSeconds: 31,
            icon: null,
        };
        requireOk(await api(p, 'timetable', timetableDraft(emptyTimetable, {
            parts: [progress],
        }), progressOnly.query), 'Create progress-only timetable row');
        const phasePlan = requireOk(await api(p, 'phases', undefined, progressOnly.query), 'Read progress-only phase');
        const viewPhase = phasePlan.phases.find(phase => phase.kind === 'View');
        if (!viewPhase) throw new Error('View phase missing from existing phase fixture schema');
        requireOk(await api(p, 'phases', {
            rowVersion: phasePlan.rowVersion,
            manualOverride: true,
            activePhaseId: viewPhase.id,
            customPhases: [],
        }, progressOnly.query), 'Make progress-only event visible to fixture member');
        await memberPage.goto(origin + '/events/' + progressOnly.id);
        await memberPage.locator('.event-matrix-transition').waitFor();
        const progressHeaders = await memberPage.locator('.event-part-matrix thead th').allInnerTexts();
        ok(progressHeaders.slice(0, 3).join('|') === '曲・進行|開始・終了|所要時間'
            && progressHeaders.length === 3
            && await memberPage.locator('.event-matrix-transition').innerText().then(text => text.includes(progress.name))
            && await memberPage.locator('[data-event-matrix-slot]').count() === 0,
        'Progress row renders with the three base columns when no participation-part columns exist');
        const progressDuplicateIds = await duplicates(memberPage);
        ok(progressDuplicateIds.length === 0,
            'Progress-only event has no duplicate DOM ids: ' + progressDuplicateIds.join(', '));

        const emptySchedule = await event('Native event matrix no rows ' + suffix);
        const emptyPhasePlan = requireOk(await api(p, 'phases', undefined, emptySchedule.query), 'Read empty schedule phase');
        const emptyViewPhase = emptyPhasePlan.phases.find(phase => phase.kind === 'View');
        if (!emptyViewPhase) throw new Error('View phase missing from existing phase fixture schema');
        requireOk(await api(p, 'phases', {
            rowVersion: emptyPhasePlan.rowVersion,
            manualOverride: true,
            activePhaseId: emptyViewPhase.id,
            customPhases: [],
        }, emptySchedule.query), 'Make empty schedule visible to fixture member');
        await memberPage.goto(origin + '/events/' + emptySchedule.id);
        await memberPage.locator('#participation-submit').waitFor();
        ok(await memberPage.locator('.event-part-matrix').count() === 0
            && await memberPage.locator('.participation-table-scroll table').count() === 1
            && await memberPage.locator('[data-participation-slot]').count() === 0,
        'Zero calculated rows with zero slots falls back to the existing empty participation table');
        const emptyDuplicateIds = await duplicates(memberPage);
        ok(emptyDuplicateIds.length === 0,
            'Zero-row event has no duplicate DOM ids: ' + emptyDuplicateIds.join(', '));
        ok(errors.length === 0, 'Matrix and legacy route browser flows have no client runtime errors');
    } finally {
        await memberPage.close();
        await adminPage.close();
    }
};