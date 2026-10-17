// Static source-level UI contract check. This is not a browser or visual test.
import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import { dirname, resolve, join } from 'node:path';
import { fileURLToPath } from 'node:url';
import { createHash } from 'node:crypto';

const root = resolve(dirname(fileURLToPath(import.meta.url)), '..');
const profile = readFileSync(join(root, 'src/Sem.Web/Components/Pages/ProfileSettings.razor'), 'utf8');
const ui = readFileSync(join(root, 'src/Sem.Web/wwwroot/account-passkeys.js'), 'utf8');
const workspaceUi = readFileSync(join(root, 'src/Sem.Web/wwwroot/sem.js'), 'utf8');
const api = readFileSync(join(root, 'src/Sem.Web/Runtime/AccountPasskeyEndpoints.cs'), 'utf8');
const routes = readFileSync(join(root, 'src/Sem.Web/Runtime/SemEndpoints.cs'), 'utf8');
const accountService = readFileSync(join(root, 'src/Sem.Web/Runtime/AccountPasskeyService.cs'), 'utf8');
const additionService = readFileSync(join(root, 'src/Sem.Web/Runtime/PasskeyAdditionService.cs'), 'utf8');
const recoveryService = readFileSync(join(root, 'src/Sem.Web/Runtime/PasskeyRecoveryService.cs'), 'utf8');
// Exact fixture from https://github.com/jp-raub/sem/blob/9cf8b031258fa1c9afe9668f54e61f002c4bce17/mock/ACCOUNT-MANAGEMENT.ja.md
const mockPath = join(root, 'tests', 'Fixtures', 'ui-mock-9cf8b031', 'ACCOUNT-MANAGEMENT.ja.md');
const mock = readFileSync(mockPath, 'utf8');
assert.equal(createHash('sha256').update(mock).digest('hex'), '2bc533b1187b0b0dd8176515172404ddcc24c9e979d1268f183db56a404cad38');
const requireText = (text, pattern, label) => assert.match(text, pattern, label);

requireText(mock, /アカウント一覧へ権限/, 'approved account mock remains the source reference');
requireText(mock, /認証メールは読み取り専用/, 'mock keeps the auth email read-only');
requireText(profile, /id="account-passkeys"[\s\S]*?アカウント管理：パスキー/, 'passkey management is integrated into profile settings');
for (const id of ['account-passkeys-email', 'account-passkeys-list', 'account-passkeys-add',
    'account-passkeys-add-flow', 'account-passkeys-email-code', 'account-passkeys-refresh'])
    requireText(profile, new RegExp(`id="${id}"`), `profile UI includes ${id}`);
for (const action of ['list', 'rename', 'delete-options', 'delete-verify', 'delete-confirm', 'delete-cancel',
    'add-begin', 'add-reauth', 'add-resend', 'add-confirm', 'add-create-options', 'add-complete', 'add-cancel'])
    requireText(ui, new RegExp(`request\\('${action}'`), `UI routes ${action} through the dedicated API`);
requireText(ui, /input\.maxLength = 200/, 'rename input enforces the server name bound');
requireText(ui, /deleteButton\.disabled = keys\.length <= 1/, 'UI disables deletion of the final key');
requireText(ui, /window\.confirm\([\s\S]*?5分[\s\S]*?最後のパスキー/, 'delete uses explicit confirmation and explains its expiry and last-key protection');
requireText(ui, /cache: 'no-store'[\s\S]*?redirect: 'error'/, 'requests avoid caching and reject redirects');
requireText(workspaceUi, /confirm-add-passkey-code/, 'legacy continuation UI uses the saved-email confirmation code');
requireText(workspaceUi, /passkey-add-confirm/, 'legacy continuation submits the code through the adapter');
requireText(routes, /passkeyAddition/, 'status preserves the session-bound addition status field');
requireText(api, /"passkey-options" when context\.User\.Identity\?\.IsAuthenticated == true => "add-create-options"/,
    'authenticated legacy options adapt to the gated new service');
requireText(api, /"passkey-complete" when context\.User\.Identity\?\.IsAuthenticated == true => "add-complete"/,
    'authenticated legacy completion adapts to the gated new service');
requireText(accountService, /ConfirmDeleteAsync[\s\S]*?BeginTransactionAsync\(IsolationLevel\.Serializable[\s\S]*?GetPasskeysAsync/,
    "delete's last-key count remains inside a serializable transaction");
requireText(additionService, /CompleteAsync[\s\S]*?BeginTransactionAsync\(IsolationLevel\.Serializable[\s\S]*?AddOrUpdatePasskeyAsync/,
    'addition completion remains serialized against deletion');
requireText(recoveryService, /CompletePasskeyRegistrationAsync[\s\S]*?BeginTransactionAsync\(IsolationLevel\.Serializable/,
    'recovery credential completion retains its transaction boundary');
assert.doesNotMatch(ui, /localStorage|sessionStorage/, 'credential and proof material is not persisted in browser storage');
assert.doesNotMatch(ui, /location\.(?:search|hash)|URLSearchParams/, 'account actions do not put secrets in navigation URLs');
console.log('Account passkey UI source checks passed. (Static only; no browser was launched.)');
