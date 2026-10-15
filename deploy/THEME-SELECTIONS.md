# Theme selections — persistence and service layer

English source of truth. This category adds storage and the core service contract only; it does not add HTTP endpoints, UI, CSS, or a deployment.

## Behavior

- Appearance has three optional persisted layers: the current account, one authorized `SessionEvent`, and the site. Palette and mode inherit independently in personal → event → site order. The existing `ThemeResolver` supplies the persisted catalog’s `green` palette and `System` mode when nothing is selected.
- A null `ThemeId` or `Mode` means inherit. An explicit `System` mode is a value and stops mode inheritance. Writes accept catalog IDs only; arbitrary color values are not stored.
- Reads use the current authenticated actor. They expose that actor’s selection, the authorized event selection only when an event context is requested, the site selection, the validated catalog, resolved appearance, row versions, and capabilities for the current context. Event reads and writes use the existing current-phase visibility/authorization helper. Event writes require an event manager; site writes and catalog deletion require a current global administrator.
- Theme deletion is limited to unused custom themes. The service checks all three selection scopes; restricted catalog foreign keys protect the check against races. Built-in deletion is rejected by both the service and a database trigger, and the existing immutable `BuiltIn` trigger remains in force.

## Migration and storage

Migration `20261015081000_AddThemeSelectionPreferences` adds `UserThemePreferences`, `EventThemePreferences`, and `SiteThemePreferences`. Each table has a nullable catalog reference, nullable mode constrained to `0`, `1`, or `2`, and a nonnegative concurrency `RowVersion`. New rows use expected version `0` and start at version `1`; each accepted update increments the version. Foreign keys use `RESTRICT`.

The migration does not insert a preference row, choose a theme for an account/event/site, or backfill existing data. The catalog remains authoritative and is not reseeded by this migration. Identity V3/passkey metadata is retained.

## Deployment notes

No deployment was performed as part of this change. When this persistence category is included in a later approved release:

1. Follow the release’s existing stopped-instance database-and-keys backup procedure before applying schema changes.
2. Apply the migration through the normal application migration path, then verify migration history and that all three selection tables remain empty until an authorized service write occurs.
3. Do not roll back with EF `Down` after any selection has been stored; restore a compatible backup or deploy a forward repair.
4. Treat this release as a core-only capability. There are no new routes or browser controls until a later HTTP/UI category is reviewed and deployed.

## Verification

- `dotnet run --project tests/Sem.ThemeSelectionChecks/Sem.ThemeSelectionChecks.csproj` — migration preservation, authorization, inheritance, optimistic concurrency, catalog-reference protection, audit atomicity, Identity V3, and read-only behavior.
- `dotnet run --project tests/Sem.StorageChecks/Sem.StorageChecks.csproj` — legacy adoption, passkey/data preservation, and empty preference-table preservation.

These checks use isolated SQLite files; they do not contact or modify a real service endpoint.