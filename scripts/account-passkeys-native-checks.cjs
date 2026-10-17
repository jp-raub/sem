// Source-only native UI check. Uses the already-authenticated synthetic admin
// page, its existing page-scoped CDP WebAuthn authenticator, and only the local
// SMTP fixture. A successful run restores the account key set and original key
// name, and removes the test credential from the virtual authenticator. If the
// runner is interrupted after registration, the isolated fixture may need reset.
module.exports = async function ({ p, g, ok, cdp, adminAuth }) {
    if (!p || !ok || !cdp || !adminAuth)
        throw new Error('Existing admin page and its WebAuthn CDP authenticator are required.');

    const check = (condition, label) => {
        ok(condition, label);
        if (!condition) throw new Error(label);
    };
    const entryUrl = p.url();
    const origin = new URL(p.url()).origin;
    const settingsUrl = new URL('/profile/settings', origin).href;
    const confirmationSubject = 'SEM パスキー追加のメール確認';
    const fixtureEmail = 'admin@example.test';
    const waitMs = 250;
    let inspectionPage;
    let testCredentialId;
    let additionAuth;
    let serverCredentialRemoved = false;

    const readAccount = page => page.evaluate(async () => {
        const statusResponse = await fetch('/api/status', { cache: 'no-store', redirect: 'error' });
        const status = await statusResponse.json();
        const actor = status.user?.id;
        if (!actor) return { status: 401, accountMatches: false, fixtureAccount: false,
            confirmed: false, emailMatchesUi: false, hasReadonlyEmail: false,
            passkeys: [], pendingAddition: false };

        const response = await fetch('/api/account-passkeys/list', {
            method: 'GET',
            credentials: 'same-origin',
            mode: 'same-origin',
            cache: 'no-store',
            redirect: 'error',
            headers: { Accept: 'application/json', 'X-Expected-Actor': actor }
        });
        const data = await response.json();
        const emailNode = document.getElementById('account-passkeys-email');
        return {
            status: response.status,
            accountMatches: data.actorId === actor && data.email === status.user.email,
            fixtureAccount: status.user.email === 'admin@example.test',
            confirmed: data.emailConfirmed === true,
            emailMatchesUi: !!emailNode && emailNode.textContent.trim() === status.user.email,
            hasReadonlyEmail: !!emailNode && emailNode.tagName === 'STRONG' &&
                !emailNode.isContentEditable && !emailNode.querySelector('input,textarea,select'),
            passkeys: Array.isArray(data.passkeys)
                ? data.passkeys.map(key => ({ id: key.id, name: key.name })) : [],
            pendingAddition: data.addition !== null && data.addition !== undefined
        };
    });

    const waitForAccount = async (page, predicate, description, attempts = 80) => {
        for (let attempt = 0; attempt < attempts; attempt++) {
            const state = await readAccount(page);
            if (predicate(state)) return state;
            await page.waitForTimeout(waitMs);
        }
        const uiError = await page.locator('#account-passkeys-error').textContent().catch(() => '');
        throw new Error(description + (uiError ? ' UI error: ' + uiError.slice(0, 400) : ''));
    };

    const localConfirmationMails = async () => {
        const response = await fetch('http://127.0.0.1:18025/');
        if (!response.ok) throw new Error('Local SMTP fixture did not respond.');
        const messages = await response.json();
        if (!Array.isArray(messages)) throw new Error('Local SMTP fixture returned an invalid response.');
        return messages.filter(message =>
            typeof message.to === 'string' &&
            message.to.toLowerCase().includes(fixtureEmail) &&
            message.subject === confirmationSubject);
    };

    const waitForNewConfirmationMail = async previousCount => {
        for (let attempt = 0; attempt < 40; attempt++) {
            const messages = await localConfirmationMails();
            if (messages.length > previousCount) return messages[messages.length - 1];
            await new Promise(resolve => setTimeout(resolve, 500));
        }
        throw new Error('New confirmation mail did not arrive in the local SMTP fixture.');
    };

    const nativeCredentialCount = async (authenticatorId = adminAuth) => {
        const result = await cdp.send('WebAuthn.getCredentials', { authenticatorId });
        return result.credentials.length;
    };

    const resolveDeleteDialog = async (button, accept, expectedName) => {
        const dialogPromise = p.waitForEvent('dialog');
        const clickPromise = button.click();
        const dialog = await dialogPromise;
        const expectedDialog = dialog.type() === 'confirm' && dialog.message().includes(expectedName);
        if (accept) await dialog.accept();
        else await dialog.dismiss();
        await clickPromise;
        return expectedDialog;
    };

    try {
        // Use a fresh page for the read-only UI/API checks. No WebAuthn is
        // performed here; the native ceremonies stay on `p`, whose CDP target
        // already owns `adminAuth`.
        inspectionPage = await p.context().newPage();
        await inspectionPage.goto(settingsUrl, { waitUntil: 'domcontentloaded' });
        await inspectionPage.locator('#profile-settings').waitFor({ state: 'visible', timeout: 20_000 });
        await inspectionPage.waitForFunction(() =>
            document.querySelector('#account-passkeys-email')?.textContent.trim() !== '確認中です.' &&
            document.querySelector('#account-passkeys-list .account-passkey-item'));

        const initial = await readAccount(inspectionPage);
        check(initial.status === 200 && initial.accountMatches && initial.fixtureAccount &&
            initial.confirmed && initial.emailMatchesUi && initial.hasReadonlyEmail,
        'Account settings display the authenticated, confirmed email as read-only.');
        check(initial.passkeys.length === 1 && !initial.pendingAddition,
            'Fixture starts with one passkey and no pending addition.');
        const lastKeyDelete = inspectionPage
            .locator('#account-passkeys-list .account-passkey-item')
            .first()
            .getByRole('button', { name: '削除' });
        check(await lastKeyDelete.isDisabled(),
            'The only registered passkey cannot be deleted from the UI.');

        const rejectedEmailMutation = await inspectionPage.evaluate(async credentialId => {
            const csrfResponse = await fetch('/api/csrf', { cache: 'no-store', redirect: 'error' });
            const { token } = await csrfResponse.json();
            const status = await (await fetch('/api/status', { cache: 'no-store' })).json();
            const response = await fetch('/api/account-passkeys/rename', {
                method: 'POST',
                credentials: 'same-origin',
                mode: 'same-origin',
                cache: 'no-store',
                redirect: 'error',
                headers: {
                    Accept: 'application/json',
                    'Content-Type': 'application/json; charset=utf-8',
                    'X-CSRF-TOKEN': token
                },
                body: JSON.stringify({
                    expectedActor: status.user.id,
                    credentialId,
                    name: 'read-only probe',
                    email: 'not-an-update@example.invalid'
                })
            });
            return response.status;
        }, initial.passkeys[0].id);
        check(rejectedEmailMutation === 400,
            'The account-passkey API rejects attempts to update the saved email.');
        const afterReadonlyProbe = await readAccount(inspectionPage);
        check(afterReadonlyProbe.accountMatches && afterReadonlyProbe.passkeys.length === 1 &&
            afterReadonlyProbe.passkeys[0].id === initial.passkeys[0].id &&
            afterReadonlyProbe.passkeys[0].name === initial.passkeys[0].name,
        'A rejected email update leaves the account and passkey unchanged.');

        await inspectionPage.close();
        inspectionPage = undefined;

        const initialNativeCredentialCount = await nativeCredentialCount();
        check(initialNativeCredentialCount === initial.passkeys.length,
            'The existing page-scoped virtual authenticator matches the fixture passkey count.');

        await p.goto(settingsUrl, { waitUntil: 'domcontentloaded' });
        await p.locator('#profile-settings').waitFor({ state: 'visible', timeout: 20_000 });
        await waitForAccount(p, state => state.status === 200 &&
            state.passkeys.length === initial.passkeys.length && !state.pendingAddition,
        'Authenticated account settings did not become ready.');

        const previousMailCount = (await localConfirmationMails()).length;
        await p.locator('#account-passkeys-add').click();
        await p.locator('#account-passkeys-add-flow').waitFor({ state: 'visible', timeout: 30_000 });
        const confirmationMail = await waitForNewConfirmationMail(previousMailCount);
        if (typeof confirmationMail.body !== 'string')
            throw new Error('Local confirmation message has no readable body.');
        const codeMatch = confirmationMail.body.match(
            /確認コードを入力してください\.\s*([A-Fa-f0-9]{64})\s*追加操作全体/
        ) || confirmationMail.body.match(
            /確認コードを入力してください。\s*([A-Fa-f0-9]{64})\s*追加操作全体/
        );
        if (!codeMatch) throw new Error('Expected confirmation code was not found in the local message.');
        let confirmationCode = codeMatch[1];
        const codeInput = p.locator('#account-passkeys-email-code');
        await codeInput.fill(confirmationCode);
        confirmationCode = '';
        await p.locator('#account-passkeys-add-confirm').click();
        await p.locator('#account-passkeys-add-create').waitFor({ state: 'visible', timeout: 20_000 });
        await p.waitForFunction(() => {
            const input = document.getElementById('account-passkeys-email-code');
            return !input || input.value === '';
        });
        const currentUrl = new URL(p.url());
        check(currentUrl.search === '' && currentUrl.hash === '',
            'The email confirmation code is submitted without adding it to the URL.');

        // A second passkey is a different authenticator, not a duplicate on the
        // original authenticator (which is correctly listed in excludeCredentials).
        await cdp.send('WebAuthn.setAutomaticPresenceSimulation', {
            authenticatorId: adminAuth, enabled: false
        });
        ({ authenticatorId: additionAuth } = await cdp.send('WebAuthn.addVirtualAuthenticator', {
            options: { protocol: 'ctap2', transport: 'usb', hasResidentKey: true,
                hasUserVerification: true, isUserVerified: true,
                automaticPresenceSimulation: true }
        }));
        check(await nativeCredentialCount(additionAuth) === 0,
            'A distinct empty virtual authenticator is used for the additional passkey.');
        await p.locator('#account-passkeys-add-create').click();
        const added = await waitForAccount(p, state => state.status === 200 &&
            state.passkeys.length === initial.passkeys.length + 1 && !state.pendingAddition,
        'Native passkey addition did not settle with exactly one additional key.');
        check(added.passkeys.length === initial.passkeys.length + 1,
            'Native email-confirmed registration adds exactly one passkey.');
        await p.waitForFunction(() =>
            document.querySelector('#account-passkeys-add')?.disabled === false &&
            document.querySelectorAll('#account-passkeys-list .account-passkey-item').length === 2);
        const newKeyIndex = added.passkeys.findIndex(key =>
            !initial.passkeys.some(existing => existing.id === key.id));
        check(newKeyIndex >= 0, 'The newly added passkey is distinguishable from the original key.');

        const nativeAfterAddition = await nativeCredentialCount(additionAuth);
        check(nativeAfterAddition === 1 &&
            await nativeCredentialCount() === initialNativeCredentialCount,
            'The separate virtual authenticator received exactly one credential and the original is unchanged.');

        const testKeyName = `native-account-check-${Date.now().toString(36)}`;
        let newKeyRow = p.locator('#account-passkeys-list .account-passkey-item').nth(newKeyIndex);
        await newKeyRow.locator('input[type="text"]').fill(testKeyName);
        await newKeyRow.getByRole('button', { name: '名前を変更' }).click();
        const renamed = await waitForAccount(p, state => state.passkeys.some(key =>
            key.id === added.passkeys[newKeyIndex].id && key.name === testKeyName),
        'Native passkey rename did not persist.');
        check(renamed.passkeys.length === initial.passkeys.length + 1 &&
            renamed.passkeys.some(key => key.id === added.passkeys[newKeyIndex].id && key.name === testKeyName),
        'Renaming the newly added passkey persists through the account API.');

        await p.waitForFunction(name =>
            document.querySelector('#account-passkeys-add')?.disabled === false &&
            [...document.querySelectorAll('#account-passkeys-list .account-passkey-item strong')]
                .some(node => node.textContent === name), testKeyName);
        newKeyRow = p.locator('#account-passkeys-list .account-passkey-item').filter({ hasText: testKeyName });
        const cancelDialogExpected = await resolveDeleteDialog(
            newKeyRow.getByRole('button', { name: '削除' }), false, testKeyName);
        check(cancelDialogExpected, 'Deleting a key requires an explicit confirmation dialog.');
        await p.waitForFunction(() =>
            document.getElementById('account-passkeys-status')?.textContent.includes('削除を取り消しました'));
        const afterCancel = await readAccount(p);
        check(afterCancel.passkeys.length === initial.passkeys.length + 1 &&
            afterCancel.passkeys.some(key => key.id === added.passkeys[newKeyIndex].id),
        'Dismissing deletion consumes the proof and leaves both keys registered.');

        newKeyRow = p.locator('#account-passkeys-list .account-passkey-item').filter({ hasText: testKeyName });
        const acceptDialogExpected = await resolveDeleteDialog(
            newKeyRow.getByRole('button', { name: '削除' }), true, testKeyName);
        check(acceptDialogExpected, 'Accepting the explicit deletion dialog targets the renamed test key.');
        await p.waitForFunction(async () => {
            const status = await (await fetch('/api/status', { cache: 'no-store' })).json();
            const response = await fetch('/api/account-passkeys/list', {
                cache: 'no-store',
                headers: { 'X-Expected-Actor': status.user.id }
            });
            const data = await response.json();
            return response.ok && data.passkeys?.length === 1 && data.addition == null;
        }, null, { timeout: 20_000 });
        const finalState = await readAccount(p);
        check(finalState.passkeys.length === 1 &&
            finalState.passkeys[0].id === initial.passkeys[0].id &&
            finalState.passkeys[0].name === initial.passkeys[0].name &&
            !finalState.pendingAddition,
        'Accepted deletion leaves the original sole key and its original name unchanged.');
        check(await p.locator('#account-passkeys-list .account-passkey-item')
            .first().getByRole('button', { name: '削除' }).isDisabled(),
        'The remaining final key is again protected by the UI.');

        testCredentialId = added.passkeys[newKeyIndex].id;
        await cdp.send('WebAuthn.removeCredential', {
            authenticatorId: additionAuth,
            credentialId: Buffer.from(testCredentialId, 'base64url').toString('base64')
        });
        serverCredentialRemoved = true;
        testCredentialId = undefined;
        check(await nativeCredentialCount(additionAuth) === 0 &&
            await nativeCredentialCount() === initialNativeCredentialCount,
            'The temporary credential is removed and the original authenticator remains unchanged.');
        await p.goto(entryUrl, { waitUntil: 'domcontentloaded' });
        if (new URL(entryUrl).pathname === '/') await p.waitForSelector('#issue');
        check(new URL(p.url()).pathname === new URL(entryUrl).pathname,
            'Successful account-key checks return to the caller page with the original session.');
    } finally {
        if (additionAuth) await cdp.send('WebAuthn.removeVirtualAuthenticator', { authenticatorId: additionAuth }).catch(() => {});
        await cdp.send('WebAuthn.setAutomaticPresenceSimulation', { authenticatorId: adminAuth, enabled: true }).catch(() => {});
        if (inspectionPage) await inspectionPage.close().catch(() => {});
        // No cleanup request is attempted on a possibly stale/in-flight proof.
        // If interrupted after add completion, the synthetic account may retain
        // the new credential; restore the fixture before unrelated native tests.
        void g;
        void serverCredentialRemoved;
        void testCredentialId;
    }
};