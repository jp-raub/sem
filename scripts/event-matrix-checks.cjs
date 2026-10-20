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
        ok((await memberPage.locator('.entry-summary-number').innerText()).replace(/\s+/g, ' ').trim() === '0 / 1 曲',
            'New participant summary starts with zero persisted songs');
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
        ok((await memberPage.locator('.entry-summary-number').innerText()).replace(/\s+/g, ' ').trim() === '0 / 1 曲',
            'Unsent finite selection does not increase persisted summary count');
        await require('./participation-layout-checks.cjs')({ page: memberPage, ok, answerId: question.id });

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

        // Latest-state refresh now preserves the draft and explicitly gates changed versions.
        await memberPage.locator('#participation-refresh').click();
        await memberPage.locator('#participation-review-confirm').waitFor();
        ok(await memberPage.locator('.participation-selection-chip').count() === 1
            && await memberPage.locator(`[data-participation-answer="${question.id}"]`).inputValue() === 'YES',
        'Manual latest-state refresh preserves the selected slot and unsent answer');
        ok(await memberPage.locator('#participation-submit').isDisabled()
            && (await memberPage.locator('#participation-refresh-notice').innerText()).includes('入力は保持'),
        'Structural version changes require explicit review before the retained draft can submit');
        const beforeReview = await readParticipation(g, integrated.query);
        ok(beforeReview.ownPhaseSongCount === 0
            && beforeReview.slots.find(slot => slot.id === memberVoiceSlot.id)?.assignedUserId == null,
        'Refreshing and reviewing a draft perform no registration write');
        await memberPage.locator('#participation-review-confirm').click();
        await memberPage.locator('#participation-submit').click();
        await memberPage.waitForFunction(() => document.getElementById('participation-message').textContent.includes('1 件成功'));
        const afterRetry = await readParticipation(g, integrated.query);
        ok(afterRetry.ownPhaseSongCount === 1
            && afterRetry.slots.find(slot => slot.id === memberVoiceSlot.id)?.assignedUserId === afterRetry.ownUserId
            && afterRetry.questions.find(item => item.id === question.id)?.ownValue === 'YES',
        'Retained slot and answer submit successfully only after explicit changed-version review');
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
        const otherButton = adminPage.locator(`[data-event-matrix-slot="${memberVoiceSlot.id}"]`);
        ok(otherText.trim() === assignedMemberSlot.assignedName,
            'Other assigned matrix cell displays the exact assigned name without an ownership prefix');
        ok(await otherButton.evaluate(e => e.classList.contains('event-matrix-occupied'))
            && (await otherButton.getAttribute('aria-label')).endsWith(' / ' + assignedMemberSlot.assignedName)
            && await otherButton.getAttribute('aria-description') === '他の参加者の登録枠',
            'Other slot keeps visual and accessible ownership context separately from the assigned name');

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
        const ownButton = adminPage.locator(`[data-event-matrix-slot="${managerVoiceSlot.id}"]`);
        ok(ownText.trim() === assignedManagerSlot.assignedName,
            'Own assigned matrix cell displays the exact assigned name without an ownership prefix');
        ok(await ownButton.evaluate(e => e.classList.contains('event-matrix-own'))
            && (await ownButton.getAttribute('aria-label')).endsWith(' / ' + assignedManagerSlot.assignedName)
            && await ownButton.getAttribute('aria-description') === 'ログイン中のアカウントの登録枠',
            'Own slot keeps visual and accessible ownership context separately from the assigned name');

        // Select two still-free instrument slots, then let another participant claim just one.
        const currentMemberPlan = await readParticipation(g, integrated.query);
        const instrumentA = slotFor(currentMemberPlan, songs[0].id, guitar.name);
        const instrumentB = slotFor(currentMemberPlan, songs[1].id, guitar.name);
        for (const slot of [instrumentA, instrumentB]) {
            const cell = memberPage.locator(`[data-event-matrix-slot="${slot.id}"]`);
            await cell.evaluate(e=>e.scrollIntoView({block:'center',inline:'end'}));
            await cell.click();
        }
        await memberPage.locator(`[data-participation-answer="${question.id}"]`).selectOption('NO');
        const managerBeforeClaim = await readParticipation(p, integrated.query);
        requireOk(await api(p, 'participation', {
            requestId: random(), rowVersion: managerBeforeClaim.rowVersion,
            participantVersion: managerBeforeClaim.participantVersion, phaseId: managerBeforeClaim.phaseId,
            kind: 'Register', slots: [{id: instrumentB.id, rowVersion: managerBeforeClaim.slots.find(s=>s.id===instrumentB.id).rowVersion}],
            answers: [{questionId: question.id, value: 'NO'}],
        }, integrated.query), 'Other participant claims one selected instrument slot');
        await memberPage.waitForFunction(id => {
            const cell = document.querySelector(`[data-event-matrix-slot="${id}"]`);
            return cell && cell.getAttribute('aria-pressed') === 'false' && cell.classList.contains('event-matrix-occupied');
        }, instrumentB.id, {timeout: 25000});
        ok(await memberPage.locator('.participation-selection-chip').count() === 1
            && await memberPage.locator(`[data-event-matrix-slot="${instrumentA.id}"]`).getAttribute('aria-pressed') === 'true'
            && await memberPage.locator(`[data-participation-answer="${question.id}"]`).inputValue() === 'NO',
        'Automatic polling removes only the occupied selection and retains other selection and unsent answer');
        ok((await memberPage.locator('#participation-refresh-notice').innerText()).includes('その枠だけ選択を解除'),
            'Automatic occupation update announces the removed selection');
        const occupationDialog = memberPage.locator('#participation-selection-notice');
        await occupationDialog.waitFor({state:'visible'});
        const noticeState = await occupationDialog.evaluate(e => ({
            modal:e.matches(':modal'), text:e.textContent, focused:e.contains(document.activeElement),
            width:e.getBoundingClientRect().width, viewport:innerWidth,
        }));
        ok(noticeState.modal && noticeState.focused && noticeState.text.includes(instrumentB.title)
            && noticeState.text.includes(instrumentB.partName),
            'Externally claimed selection opens a focused native modal naming the affected song and part');
        ok(noticeState.width <= noticeState.viewport && await occupationDialog.getAttribute('role') === 'alertdialog',
            'Occupation modal is accessible and fits the narrow viewport');
        const revisionWhileOpen = await memberPage.locator('#participation-refresh-notice').getAttribute('data-refresh-revision');
        await memberPage.waitForFunction(revision => document.querySelector('#participation-refresh-notice').dataset.refreshRevision !== revision,
            revisionWhileOpen, {timeout:25000});
        ok(await occupationDialog.isVisible() && await occupationDialog.count() === 1,
            'An unchanged automatic poll neither dismisses nor duplicates the pending modal');
        await memberPage.keyboard.press('Escape');
        await occupationDialog.waitFor({state:'detached'});
        ok(await memberPage.locator(`[data-participation-answer="${question.id}"]`).inputValue() === 'NO'
            && await memberPage.locator('.participation-selection-chip').count() === 1,
            'Escape acknowledges the modal without resetting other selections or the unsent answer');
        const revisionAfterClose = await memberPage.locator('#participation-refresh-notice').getAttribute('data-refresh-revision');
        await memberPage.waitForFunction(revision => document.querySelector('#participation-refresh-notice').dataset.refreshRevision !== revision,
            revisionAfterClose, {timeout:25000});
        ok(await occupationDialog.count() === 0, 'Acknowledged occupation modal does not reopen on an unchanged poll');

        // A later new loss must still notify. Assigned slots may remain enabled profile buttons.
        const beforeRelease = await readParticipation(p, integrated.query);
        requireOk(await api(p, 'participation', {
            requestId:random(), rowVersion:beforeRelease.rowVersion, participantVersion:beforeRelease.participantVersion,
            phaseId:beforeRelease.phaseId, kind:'Release', reason:'Modal repeat fixture',
            slots:[{id:instrumentB.id,rowVersion:beforeRelease.slots.find(s=>s.id===instrumentB.id).rowVersion}], answers:[],
        }, integrated.query), 'Release fixture slot before a later new selection');
        await memberPage.locator('#participation-refresh').click();
        const repeatedCell = memberPage.locator(`[data-event-matrix-slot="${instrumentB.id}"]`);
        await memberPage.waitForFunction(id=>!document.querySelector(`[data-event-matrix-slot="${id}"]`).disabled, instrumentB.id);
        await repeatedCell.evaluate(e=>e.scrollIntoView({block:'center',inline:'end'}));
        await repeatedCell.click();
        const beforeRepeatClaim = await readParticipation(p, integrated.query);
        requireOk(await api(p, 'participation', {
            requestId:random(), rowVersion:beforeRepeatClaim.rowVersion, participantVersion:beforeRepeatClaim.participantVersion,
            phaseId:beforeRepeatClaim.phaseId, kind:'Register',
            slots:[{id:instrumentB.id,rowVersion:beforeRepeatClaim.slots.find(s=>s.id===instrumentB.id).rowVersion}],
            answers:[{questionId:question.id,value:'NO'}],
        }, integrated.query), 'Claim a newly reselected fixture slot again');
        await occupationDialog.waitFor({state:'visible',timeout:25000});
        ok(await occupationDialog.evaluate(e=>e.matches(':modal')), 'A later new loss opens a new native modal');
        await memberPage.locator('#participation-selection-notice-close').click();
        await occupationDialog.waitFor({state:'detached'});
        const restoredFocus = await memberPage.evaluate(id => {
            const active = document.activeElement;
            return active?.isConnected && !active.matches(':disabled')
                && (active.getAttribute('data-event-matrix-slot') === id
                    || ['participation-refresh','participation-phase'].includes(active.id));
        }, instrumentB.id);
        ok(restoredFocus,
            'Closing a notice restores its still-enabled slot/profile control or a safe refresh fallback');
        ok(await memberPage.locator('.participation-selection-chip').count() === 1
            && await memberPage.locator(`[data-participation-answer="${question.id}"]`).inputValue() === 'NO',
            'Close button acknowledges only the notification and retains the remaining draft');
        const afterPoll = await readParticipation(g, integrated.query);
        ok(afterPoll.slots.find(slot=>slot.id===instrumentA.id).assignedUserId == null
            && afterPoll.questions.find(item=>item.id===question.id).ownValue === 'YES',
            'Automatic polling never submits the remaining selection or unsent answer');
        requireOk(await api(g, 'participation', {
            requestId: random(), rowVersion: afterPoll.rowVersion, participantVersion: afterPoll.participantVersion,
            phaseId: afterPoll.phaseId, kind: 'Answers', slots: [], answers: [{questionId: question.id, value: 'YES'}],
        }, integrated.query), 'Same account saves an answer in a second document');
        await memberPage.locator('#participation-refresh').click();
        await memberPage.locator('#participation-review-confirm').waitFor();
        await memberPage.locator('.participation-saved-answers summary').click();
        ok(await memberPage.locator('#participation-submit').isDisabled()
            && await memberPage.locator(`[data-participation-answer="${question.id}"]`).inputValue() === 'NO'
            && (await memberPage.locator('.participation-saved-answers').innerText()).includes('YES'),
            'Same-account external save preserves unsent answer and exposes saved-answer comparison before explicit review');
        ok((await readParticipation(g, integrated.query)).questions.find(item=>item.id===question.id).ownValue === 'YES',
            'External own-answer change is not silently overwritten by refresh');
        await memberPage.locator('#participation-review-confirm').click();
        const stillSelected = memberPage.locator(`[data-event-matrix-slot="${instrumentA.id}"]`);
        await stillSelected.evaluate(e=>e.scrollIntoView({block:'center',inline:'end'})); await stillSelected.click();

        const duplicateIds = await duplicates(memberPage);
        ok(duplicateIds.length === 0, 'Integrated event has no duplicate DOM ids: ' + duplicateIds.join(', '));
        ok(await memberPage.locator('title').count() === 1,
            'Embedded children do not add duplicate document titles');

        await adminPage.goto(origin + '/events/' + integrated.id + '/timetable');
        await adminPage.locator('#timetable-edit').waitFor();
        ok(new URL(adminPage.url()).pathname === `/events/${integrated.id}/timetable`
            && await adminPage.locator('.event-part-matrix').count() === 0,
        'Legacy dedicated timetable route remains available without the embedded matrix');
        ok(await adminPage.locator('.timetable-duration').evaluateAll(elements =>
            elements.length === 4 && elements.every(element => {
                const style = getComputedStyle(element);
                return style.textAlign === 'right' && style.fontVariantNumeric.includes('tabular-nums')
                    && style.whiteSpace === 'nowrap';
            })),
        'Dedicated timetable duration header and song/progress cells are right-aligned tabular numbers');
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
        ok((await memberPage.locator('.entry-summary').innerText()).includes('閲覧期間・パート登録受付外')
            && await memberPage.locator('.entry-summary [role="meter"]').count() === 0,
            'View-only zero-slot summary is closed and has no invalid 0/0 meter');
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
        await require('./entry-summary-checks.cjs')({
            p, g, memberPage, adminPage, ok, api, requireOk, integrated, question, songs, guitar,
        });
        await require('./participation-condition-checks.cjs')({
            p, g, memberPage, adminPage, ok, api, requireOk, event,
        });
        ok(errors.length === 0, 'Matrix and legacy route browser flows have no client runtime errors');
    } finally {
        await memberPage.close();
        await adminPage.close();
    }
};