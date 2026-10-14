# Manual Push operation status view

## Read-only integration

GET `/api/core/manual-notification-push-status` takes one event ID, one frozen actor ID and one canonical accepted request ID. Current cookie actor must match; Core independently validates current session/stamp/account/event management and exact operation author. POST is rejected; all responses are no-store. It never sends, resends, stages or changes inbox/audit state.

The accepted composer result offers an explicit status-read button. No automatic initial fetch, polling or retry. Snapshot is cleared before each read; mismatch/failure never reuses stale counts. Outstanding reads disable a new-operation switch. In-memory ticket/version guards reject late results after reset/new operation/disposal. A403 hides accepted result and status until current original actor is reauthorized; reauthorization does not restore old status automatically. No browser storage, auth-state files or status-body logging.

Original recipient/preparation counts are people. Queue state counts are devices, not people. Other non-prepared outcomes include expired, not-admitted and denied cases; they are not all one suppression cause. Admission gate is only the current global epoch/deadline gate, not current per-device send permission. Persisted Pending/Claimed may await housekeeping. Provider Accepted is not handset receipt or read status; Unknown is not automatically retried. Original deadline is never extended by refresh. The endpoint returns no message content, recipient/device identities or binding secrets.

Dedicated display state/transport tests and actual HTTP/virtual-passkey/Blazor tests cover private reads, method/actor/query guards, explicit fetch, held read, mismatch, authority rejection/reauthorization and mobile/PC. Final source counts/CI are recorded after actual completion. The synthetic native wrapper explicitly unsets host Push keys; no real Push service is contacted.

No production activation, DXP/.env/real data/keys/public deployment, new migration, distribution or merge. This view exposes only the current composer's own accepted operation, not a history browser or other authors' operations. Production rollout/real device acceptance remain separate.
