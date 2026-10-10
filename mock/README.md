# SEM — Session Room UI mock

English is the canonical documentation. [日本語](README.ja.md)

Dependency-free, browser-only prototype of the current account-based session-event design. This is not the production Blazor Server application. The existing root README and main branch are untouched.

## Try this branch on GitHub

Open the `ui/session-room-mock` branch in a GitHub Codespace (Code → Codespaces → Create codespace on this branch). Then run from the repository root:

```sh
python3 -m http.server 8000 --bind 0.0.0.0 --directory mock
```

Open port 8000 from the Ports panel and keep its visibility **Private**. No build or package install is required for the app. The same command works locally. GitHub source-file view does not execute HTML/JavaScript.

## Optional GitHub Pages (not deployed)

The workflow `.github/workflows/ui-mock-pages.yml` stages only index.html/styles.css/app.js, deploys under a dedicated path `ui-mock/`, and runs only by manual dispatch. It contains no push trigger. Review the PR and merge it into main before using the Actions UI to run this workflow. Set Settings → Pages → Source to GitHub Actions, then run Deploy UI mock to Pages. Its environment URL points at `/ui-mock/`.

Do not make a private repository public just for Pages. Private-repository Pages availability depends on the plan; the site itself may be public. Confirm visibility before deployment. This workflow publishes only the mock, not the production application, and would replace the current Pages site content if one is later added. Codespaces/local preview do not need Pages or public visibility.

## Demo accounts

| Persona | Email | Passcode | Scope |
|---|---|---|---|
| Participant | member@example.test | demo | Visible events, assign self, comment |
| Event administrator | event@example.test | demo | Autumn event only; no service administration |
| Global administrator | admin@example.test | demo | All events/users/invitations; privileged role protected |

Use the login shortcuts or header persona selector. Reusable invitation: SESSION-DEMO (valid through 2030). Route: `#/register?invite=SESSION-DEMO`. Without a query, the registration form remains visible. No public signup link appears on login.

## Implemented interactions

Sortable event list with hidden-event filtering; song/transition timetable with computed times and horizontal scrolling; absent cells disabled; duplicate part names as multiple numbered slots; batch registration; same-song multi-role counting as one; phase-independent quotas; -1 unlimited; partial-success results; comment-only submission (trimmed minimum 3 characters); admin-only comment deletion; mandatory-reason unassignment/deletion of populated rows; overview editing; phase CRUD/manual override/timestamp reevaluation; event-admin controls; timetable add/delete/reorder/start-time; global role/invitation controls; browser persistence/reset and mock audit log.

Use Demo validation options → Simulate conflict to assign the first selected candidate to another fictitious user before submission and check partial success.

## Limitations and safety

- Demo authentication/authorization is only UI simulation and intentionally bypassable through the persona selector. Never enter actual passwords, emails, tokens or participant data. Passcodes are not saved; all demo accounts re-login with demo.
- Data stays in localStorage; identity in sessionStorage. There is no DB/API/shared state, real multi-user locking, SignalR, real email/reset link, background phase scheduler, backup or PostgreSQL connection.
- Phase auto-switching is a button evaluated at the selected timestamp; reevaluation clears manual override. Production scheduler/override semantics and phase-attribution storage need implementation.
- Selection can exceed quota; submit applies the quota and reports rejected candidates individually. Selection-time guidance is a follow-up.
- Part-master maintenance and editing existing song metadata are not included. Row creation uses comma-separated names; production should select from PartDefinitions. Event/user deletion is intentionally absent.
- This JavaScript object graph is only a mock representation, not the final DB schema. Blazor/EF Core architecture remains unchanged.
- Use HTTP serving; file:// persistence can vary by browser.

## Verification

20 Chromium/Playwright checks passed for role visibility, same-song count, partial success, quota, comments, mandatory reasons, phase independence/unlimited, privileged account, invitation validation/auto-login, persistence, mobile overflow and runtime errors. See `qa/results.json` and `tests/browser.cjs`.

To rerun: serve mock/ on 127.0.0.1:8765, install Playwright in a separate tooling directory, then set `NODE_PATH` to that directory's node_modules and `CHROMIUM_PATH` to a Chromium binary. Run `node mock/tests/browser.cjs`. Tests use a fresh browser context and modify no remote data.
