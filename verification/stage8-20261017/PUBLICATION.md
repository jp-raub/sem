# Stage8 publication record

This branch preserves the stage8 integrated source tested on 2026-10-17. The 877 archived source files match source-archive-manifest.json. Additional files in this directory record historical verification, not a fresh CI execution.

- Base: 2e9fe2bfd7567939e24cf3435ec84c5e4033cc22
- Source archive SHA-256: c862d5a670bc29dcc331ee60e4098b3d2c679413ae196cf275c9505a60abb816
- Evidence archive SHA-256: cd6c414b70f43785324b602b8ee8b3022462cd7a0177b82436e67607c9655fbf
- Local .NET suites: 75/75; synthetic UI scripts: 9/9; fresh native browser checks: 570, client errors: 0.
- Frozen source files checked after both native and regression runs: 815, changed: 0.

## Boundaries

Recovery included both exact reconstructions and repaired candidates; this is not a claim of byte-identical recovery of every earlier worktree. Mock parity remains incomplete. Docker, DXP, real mail/Push delivery, physical authenticators, and production data preservation acceptance are not proven by local fixtures. Existing non-null duplicate Invitation.RequestId groups must be zero before migration; no automatic repair or data deletion is authorized.

No database, cookie, mail, key, certificate, credential, proof, raw application log, or authentication fingerprint is committed. Synthetic screenshots remain in the separate shared evidence archive. Per-file log hashes are references to private historical logs only.

## Ongoing source-of-truth policy

Commit source, tests, migration definitions and non-secret verification summaries to GitHub at each coherent milestone. Use a branch and PR for unfinished work; keep main integration and actual deployment separate. Record commit/PR and verification stage in the Notion operational dashboard. Session archives are supplemental backups, not the sole source of truth. Never commit runtime databases, credentials, raw mail or authentication material.
