# Acceptance workflow local verification

Base: `5d649f8d8d684271e7e17eec0596e074e471696c`.

The recorded local run and 400-file hash manifest below refer to the initial UI/retention implementation at `4c22dd3e26be13080ea1ee5b7f7410927d4ffff1`. Later profile-card and interaction-readiness additions require their own current-head CI; do not reuse the earlier success as proof for those additions.

- 76 independent .NET projects built/run serially: 76 passed, 0 failed. See dotnet-results.tsv.
- The 400 product source files were hashed before and after this run: no changes.
- Existing synthetic UI modules: 9/9 passed. New dialog lifecycle module also passed (10 modules total).
- Application build: 0 warnings, 0 errors.
- New populated retention suite: all 10 checks passed, including full-table values, passkey bytes, protected payload decryption, private file bytes, schema/model parity and restart/restore.
- Native browser acceptance is separately enforced by the acceptance-browser CI job. Earlier stopped attempts are not counted as success. Its per-check JSON is uploaded separately; it does not contain DB/mail/key/Cookie contents.

These are synthetic local results, not physical-device, external-delivery or DXP acceptance. CI results must be checked at the PR head before merge. No deployment has been performed by this change.
