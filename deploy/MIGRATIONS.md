# SQLite migration foundation

English reference; [Japanese version](MIGRATIONS.ja.md).

The baseline foundation changes **storage initialization**, not the formal business schema or authentication policy. The stacked [verified passkey-addition feature](PASSKEY-ADDITION.md) adds its own migration and authentication policy. It does not deploy itself to DXP. Review the feature PR and its exact CI commit before applying an image.

## Supported starting states

- An empty SQLite database is created by EF Core migration `20261012082319_ExternalTestBaseline`.
- An external-test database created by the fixed delivery (`78b9a6c`) using `EnsureCreated` is adopted **only if its complete schema matches** the external-test baseline. Tables, columns, indexes, constraints, views and triggers are compared through normalized SQLite schema DDL. Normalization only tolerates formatting whitespace; unusual but equivalent hand-written DDL may conservatively be rejected.
- A database already using this baseline must carry a known prefix of the application's migration history and match the schema produced by that prefix. Pending migrations are applied only after validation.

The original `00000000000000_CreateIdentitySchema` files were unused template scaffolding, not the external-test initialization path. Their design-time discovery attributes are disabled; they remain in source for traceability. A database carrying that old migration ID is **not silently relabeled** and is rejected for explicit review.

## Adoption and failure behavior

1. Refuse an unsupported provider or a model change with no migration.
2. In an immediate SQLite transaction, inspect migration history and compare the existing schema with a reference generated from the packaged migrations.
3. Require `integrity_check = ok` and no `foreign_key_check` violations.
4. For a matching legacy DB only, create the migration history table and insert the baseline history row atomically. Do not recreate business tables or rewrite user/passkey/invitation/notification rows.
5. Commit adoption and use EF Core `MigrateAsync` for normal schema updates. EF's SQLite migration lock is validated as infrastructure; it is not automatically deleted.

Unknown migration IDs, incomplete schemas, extra tables/views/triggers, missing/altered indexes and integrity violations fail startup instead of guessing. Do not use `EnsureCreated`, delete/recreate the DB, insert history manually, or disable checks to make an unknown DB pass. Preserve the backup and investigate. No multiple live application instances are supported by the external-test deployment. Stop the old instance before an update.

The baseline retains the **current simplified test model**. PostgreSQL, formal event/song/timetable models, extra constraints and other authentication lifecycle work remain separate. For passkey-addition changes, see PASSKEY-ADDITION.md.

## Candidate validation

```sh
dotnet build src/Sem.Web -c Release
dotnet run --project tests/Sem.StorageChecks -c Release
```

Storage checks use synthetic users, opaque passkey bytes, invitations, inbox/delivery/device records and events. They test empty initialization, adoption, repeat startup, copying/restoring a migrated DB, and byte-for-byte unchanged DB files on rejection. The legacy fixture is generated from the frozen baseline migration DDL (without migration history), reproducing the old deployment schema rather than the current evolving model.

The independent `SQLite migration foundation` workflow additionally builds the fixed legacy and candidate linux/amd64 images. It runs the existing HTTPS/CSRF/virtual-passkey/isolated-SMTP integration, swaps the running legacy application for the candidate against the same synthetic volume, and verifies retained protection-key sessions, invitations, inbox, existing administrator/member passkey logins and scheduled processing. This workflow does not push adjusted source back or merge the PR. Real Apple hardware and real SMTP/Push delivery are not proven by the virtual fixture.

Design-time `dotnet ef` uses a dedicated in-memory factory and Identity schema version 3. It does not start the application or touch a deployed DB. When changing the model, generate a new migration; never rewrite an already applied baseline.

## Before a future DXP update

- Keep a **fresh stopped backup of all `/data` (DB plus `/data/keys`)**, its checksum, protected `.env` and the previous image. Use BACKUP.md; never publish those private files.
- Verify the exact image/source commit, and first try the candidate on a separate copy of the current volume. The user's current DXP DB has not been accessed by this implementation.
- Preserve the original volume, public hostname/RP ID, VAPID keys and Data Protection application identity. Preserve the locally chosen SMTP authentication ID and display-name-inclusive SMTP_FROM; do not overwrite `.env` from the example.
- Cut over a single instance only after the copy passes schema/integrity checks and existing-passkey login, invitation state and notification tests.
- For the baseline-only change, business schema remains the same and the original volume is retained as rollback. Do not assume future schema migrations can be rolled back just by loading an older binary; use the retained volume/backup and account for post-cutover changes.

This document is a readiness checklist, **not authorization to update DXP**.