# Account passkey management

Japanese reference: [本人向けパスキー管理](PASSKEY-ACCOUNT-MANAGEMENT.ja.md).

This candidate adds passkey listing, renaming, deletion, and the existing email-confirmed passkey-addition flow to `/profile/settings`. The authentication email is displayed read-only. It does not add or republish `/Account`; that route remains unavailable (404).

## Owner and request boundary

- The dedicated `/api/account-passkeys/{action}` endpoint serves account operations. Mutations require the exact configured public `Origin`, the existing antiforgery token, and a live authenticated actor whose account is enabled, email-confirmed, and has an unchanged security-stamp claim and an unrevoked authentication-session claim. The caller's expected actor is checked against that account.
- Mutation bodies are bounded to 64 KiB, strict UTF-8 `application/json`, a single JSON object with no duplicate or unknown properties. Inputs are not accepted in a query string. Responses are `no-store`; requests reject redirects. Legacy account-add route names are adapters to this service; authenticated legacy creation-options/completion go through the same gated addition flow, while anonymous invitation/bootstrap enrollment remains with its existing handler.
- Native ASP.NET Core Identity WebAuthn operations validate assertions and registrations. No custom WebAuthn cryptography is added. Credential identifiers and proof material are not written to navigation URLs, browser storage, or audit text.

Legacy input exception: the external-test page still accepts an existing `passkeyAddToken` query link for compatibility. It removes that query parameter before its first asynchronous API request, then, for an already signed-in user, submits the value in the POST body through the guarded addition adapter. This is not a new link-generation path: newly issued email contains a code, not a navigation link. The initial page request can still contain the legacy query token; client-side removal cannot undo that request or sanitize upstream logs. The adapter continues to enforce the same live owner/session/stamp, one-use, and expiry conditions. Legacy-link support has not been retired. Do not interpret the no-new-URL rule above as a claim that old query links are never received.

## Rename and deletion

Rename and list require a live owner session. Deletion is a separate purpose-bound ceremony: request options restricted to the selected key, native assertion from that key, then explicit confirmation. The challenge and the post-verification one-use deletion proof each have a five-minute expiry; the latter is bound to the owner, current authentication session, current security stamp, and target credential. It cannot substitute for email-addition or recovery proof. Cancel consumes a valid proof before or after native verification; expired, consumed, wrong-owner/session/stamp/target proofs are rejected.

The last enabled passkey is blocked using the latest key count inside the same serializable database transaction as removal. Addition completion and credential recovery also use serializable transactions so those changes cannot silently race past the last-key check. Concurrent or stale mutations must be re-read rather than assumed successful.

## Addition

The existing addition service and Identity WebAuthn flow are reused. After reauthentication with an existing key, a one-time 64-hex confirmation code is sent to the saved email and entered in this settings panel (not in a URL). The complete operation remains limited to 15 minutes from reauthentication. After email confirmation, registration is allowed for five minutes or until that overall deadline, whichever comes first. Resending invalidates the previous code and does not extend either deadline. The token is stored as a hash; queued email content remains protected by the existing notification service.

## Schema and rollout status

`20261017000000_AddPasskeyDeletionProof` introduces the proof table, account foreign key, state/time/hash constraints, and an index for active proofs. The context model and snapshot are updated with the same candidate schema. This has not been deployed to DXP or applied to a live database. Disposable synthetic test databases may exercise the migration during validation. Review the complete parent change set and validate migrations against a copy before any deployment.

## Validation status

The initial repair was prepared source-only. Parent-led build, focused .NET, static UI, and synthetic model validation have since run; exact results apply only to their recorded source manifests. Interactive browser acceptance, Docker image validation, physical-device acceptance, live delivery, and DXP rollout must be reported separately rather than inferred from those checks.