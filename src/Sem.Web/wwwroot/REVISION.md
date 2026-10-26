# UI mock revision v2 (2026-10-10)

English is canonical. [日本語](REVISION.ja.md). Current behavior in this file supersedes the original README and PAGES pending-feature list. This remains a browser-only mock, not production Blazor/EF Core.

## Available

- Four palettes (Forest/Ocean/Plum/Amber); light/dark/system appearance. Priority: visitor override > event default > site default. Site defaults: global administrator → Service management → Edit → Site default theme. Event defaults: event/global administrator → Edit mode → Theme settings. Visitor override: header Display settings. Each property inherits independently; clearing both event properties removes the override.
- Header image upload/preview/removal in event overview edit: PNG/JPEG/WebP <=1MB; browser-local data URL, not a public shared upload.
- Admin edit controls hidden outside edit mode. Explicit edit mode changes page tint and adds an edit badge. Changes are staged until Save changes; Cancel restores the persisted state. Registration/comments as a participant are disabled in the same editing tab to avoid mixing with admin drafts. Role changes, phase changes, removals, invitation issuance, timetable operations and overview changes are staged.
- Unsaved-draft confirmation for app links, identity changes and browser history; browser-standard beforeunload where supported. Closed/killed browsers, especially mobile, cannot be guaranteed to warn. Two-tab conflicts reject stale saves rather than overwrite.
- Song performance >=1 second; preparation/transition >=0. Inputs m:ss / h:mm:ss, persisted as integer seconds. Legacy minute-only demo records are read compatibly. Start/end clocks include seconds and next-day markers. First part has a manual start, subsequent song parts add preparation after previous end; transitions start at previous end.
- Separate Add song/Add transition; existing metadata/duration editing. Existing song part configuration is intentionally fixed in this screen to protect assigned slots.
- Native desktop drag-and-drop via the grip; up/down buttons remain for touch/keyboard access.
- Location URL (http/https). Plain text, basic Markdown, and allowlisted HTML with live sanitized preview. Markdown supports headings, emphasis via double asterisks, inline code, links, bullet lines and line breaks; not a full CommonMark parser. Active content, arbitrary attributes, inline images and unsafe link protocols are removed. Production must use a vetted sanitizer and validate again server-side.

## Import decision

Owner selected: append unregistered data only, and only while the event is hidden. No replacement or assignment import.

In this mock, unregistered means new rows with empty assignee/user columns. Existing songs are matched by trimmed title+artist; transitions by name; duplicate rows are skipped. Supplied Assigned*/Assignee*/UserId/UserToken/担当者 columns with nonempty values cause a skip. Existing rows/assignments remain intact. All validation errors must be fixed before applying the preview. An import enters the draft, and that draft must also be saved in Hidden phase; save it before reopening registration. Switching to Hidden only in an unsaved draft is insufficient: save the phase first.

UTF-8 CSV <=1MB: required Kind,Title,Duration; optional Artist,Prep,Parts. Kind=Song/Transition; Parts uses a pipe, e.g. Vo|Gt|Ba|Dr; song requires parts. Preparation defaults to 0:00. Quoted cells, embedded newlines and BOM supported. Sample: sample-import.csv.

Google Sheets: attempts unauthenticated CSV fetch from docs.google.com URLs, including exported/published sheets and gid. Only anonymously accessible CSV that the browser can fetch is supported. Google login, OAuth, CORS restrictions, unpublished sheets and permission changes can block fetching; CSV download/upload is the fallback. No real remote Google sheet was available for live verification.

## Verification and limits

29 new Chromium/Playwright checks passed (qa/revision-results.json). Existing source remained unchanged; this revision layers enhancements.js/css over it. All state remains browser-local; themes/images/defaults do not propagate to other visitors. Real authentication, shared editing, mail, DB migration and production permission enforcement are still outside the mock.

Production schema follow-up: PerformanceSeconds, PrepSeconds, DurationSeconds; DescriptionFormat, LocationUrl, header image reference, event theme and site settings. All admin writes must revalidate permissions/current state and apply transactionally. Pending mail/audit records should be emitted only after commit. Do not treat the mock's client-side sanitizer or role toggles as a security boundary.
