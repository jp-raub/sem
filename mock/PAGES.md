# Public UI mock on GitHub Pages

[日本語](PAGES.ja.md)

Public exposure of the fictitious UI mock was approved on 2026-10-10. Repository visibility must not be changed. No real data, credentials, or production backend are deployed.

## One-time repository setup

1. Open repository Settings → Pages.
2. Under Build and deployment → Source, select GitHub Actions. If Pages is unavailable for the current private repository/plan, do not change repository visibility without additional approval.
3. If an environment deployment restriction blocks the preview branch, open Settings → Environments → github-pages and allow `ui/session-room-mock` as a deployment branch. Keep other protection rules intact. Alternatively deploy after the reviewed PR is merged into main.
4. Open Actions → Deploy UI mock to Pages. If the initial run failed before setup, open that run and choose Re-run all jobs. A new commit to the preview branch also starts a run.

The workflow now runs on mock changes pushed to `ui/session-room-mock` and `main`. Merge is not required to run the push trigger. Manual dispatch in the GitHub UI generally becomes available once the workflow is on the default branch.

The normal project-site URL is `https://jp-raub.github.io/sem/ui-mock/`. This is an expected address, not proof of successful deployment; use the actual github-pages environment URL after a successful run. Custom domains may change the address.

This workflow replaces the repository's entire Pages artifact with the mock under `ui-mock/`. Coordinate with any future production Pages site before reusing it. Future mock changes auto-update the site only when the workflow completes successfully.

## Viewing

Use the participant/event-admin/global-admin shortcuts. Only fictitious account data should be entered. Edits are local to each visitor's browser; there is no real authentication, shared DB, real mail or locking.

## UI revision request

Requested on 2026-10-10, not yet implemented: themes/dark mode/site and event defaults, header image, hidden-by-default admin controls, distinctive edit-mode styling, staged edits with confirm/cancel and unsaved-change warnings, second-resolution durations (`m:ss` / `h:mm:ss`, songs >=1 second), separate song/transition add buttons, drag-and-drop, CSV/accessible Google Sheets import, location URL, Markdown/HTML description and safe preview. Publishing the current mock does not imply these revisions are complete.
