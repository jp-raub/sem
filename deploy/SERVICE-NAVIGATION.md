# Common service navigation and own unread messages

## Scope

This incremental integration connects existing real functions, not all mock-v33 screens. The shared header offers a message-box link with the current user's unread count and a hamburger navigation. Its order is service administration (global administrators only), event list, own profile, account, notifications, invitations, logout. Existing `/workspace` sections and root authentication/device/invitation panels are the destinations. Email change, authentication recovery, key deletion, theme preferences and event-tab integration remain separate work.

## Authorization and privacy

`GET /api/core/navigation` revalidates the current enabled/email-confirmed user, security stamp and unique nonrevoked session against the database on every request. Global service administration is based on the current database flag, never an identity role claim. The projection contains only own user ID/display name, service name/description, current global-admin flag and own unread count. It excludes emails, message bodies, other members, invitation tokens and authentication material. Responses are `no-store`; POST is rejected. No schema migration is added.

The client reloads the projection on initialization, root data refresh, return from background and before opening a closed menu. Counts are snapshots; this is not SignalR delivery or a read receipt. Denied/failed reads clear private header state. Response generation and element ownership discard obsolete requests after navigation or replacement. No authentication or header state is saved in localStorage/sessionStorage/files.

Logout uses the existing CSRF-protected `/api/logout` endpoint and then performs a full navigation to remove the old circuit/private DOM. An unknown transport result clears the menu, prevents automatic re-exposure and asks for reload. It is not automatically resubmitted. Existing authentication operations and business authorization remain unchanged.

## Interaction

Escape/outside action closes the menu and restores toggle focus. Opening focuses the first permitted link. Links are ordinary navigation links, not an ARIA application menu requiring arrow-key semantics. Header destinations opt out of enhanced navigation so root authentication scripts initialize on the fresh page. Minimum control height is 44px; 390px and desktop layouts, visible focus, dark/forced-color presentation are provided.

Global service-name/description editing previews the header using literal text only, clearly marked unsaved. Save or cancel removes that preview and reloads the authoritative service value. It does not change the save/conflict policy, persist a draft or preview a theme. Unknown root `panel` parameters are ignored through an exact allowlist.

## Verification and operational boundary

Core checks cover own unread isolation/read changes, private-field exclusion, current global-admin removal, forged claims and disabled/stale/revoked/anonymous sessions. A browser-only synthetic fixture covers menu lifecycle, focus, permission loss, text-only draft, replacement and unknown logout handling. Native HTTPS/passkey/real-Blazor checks cover actual navigation, own unread, desktop/390px interaction, preview/cancel and logout/relogin; all existing regressions are retained in the dedicated workflow.

No real DXP data, `.env`, keys, hostnames, images or volumes are modified by this development. A passing local check is not real-device acceptance or deployment approval. Delivery and merge remain separate.
