# Authenticated basic member profile and author/respondent links

English is authoritative; see MEMBER-PROFILE.ja.md for the paired translation.

`/profiles/<opaque user ID>` now provides a real Blazor basic profile. Comment
authors and public-answer respondents link to that page. It reuses the existing
CoreFoundationService.ReadProfileAsync and authenticated profile API, rather
than introducing a new anonymous/public data source or a new authorization rule.
Current enabled/confirmed identity, security stamp and session are checked. The
target must still be enabled and confirmed. A retained answer/comment display
name can therefore remain in historical counts while its profile is no longer
viewable; the profile route then shows a generic failure without stale fields.

Only display name, normalized reading, call name and explicit saved social links
are rendered. Email, private event-question answers, session material and contact
fields are absent. Values are normal encoded Razor text. External links are
restricted to absolute HTTP/HTTPS without URL credentials, use a new tab with
noopener noreferrer, and are not automatically fetched or embedded. IDs are
URL-encoded and cannot choose an external navigation target.

The page loads after interactive hydration, clears the previous profile on route
change/refresh/error and ignores older asynchronous responses after navigation.
It is refreshed explicitly, not a promise to recall content already viewed.
No database/schema/migration changes, new account fields or visibility flags are
introduced. The approved authenticated-member basic-profile convention stays
unchanged.

This is **not** anonymous profile QR, 24-hour token links, event check-in QR,
registration history, additional profile questions or the final integrated v33
profile popup. Those remain distinct increments; do not reuse an event-reception
token as a public-profile credential. This increment merely connects the already
permitted basic profile to the existing event display-name links.

```bash
# Add to the existing isolated native key / storage-upgrade / real Blazor harness:
SEM_MEMBER_PROFILE_CHECKS=1 node scripts/e2e.cjs
```

The flag is inside SEM_STORAGE_UPGRADE_COMMAND acceptance, not a standalone
production test. member-profile-view.yml retains every previous suite and adds
native mobile profile/link/encoded-text/anonymous-denial checks. Synthetic data,
local SMTP and sandbox browsers only. No external social link is visited.
No merge, delivery bundle or live DXP .env/key/volume/hostname/image changes.
