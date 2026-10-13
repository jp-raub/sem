# Public answer summaries — first increment

English is authoritative; see PUBLIC-ANSWERS.ja.md for the paired translation.

## Scope

Authenticated `GET /api/core/public-answers?id=<event>` and the interactive
`/events/<event>/public-answers` page expose **only** current active, undeleted
questions with `Public=true`. The workspace and participation page link to it.
The existing manager questionnaire remains manager-only. This is an accessible
text/table increment, not completion of the mock v33 graphs or the integrated
event screen. No comments, QR reception or import support is added here.

Each read revalidates enabled/confirmed identity, security stamp, session and
current event visibility using the same manual/automatic phase rules as
participation. Managers may preview Hidden events but cannot bypass question
privacy. A transaction supplies a consistent read snapshot of publication
settings and answers. Responses are `no-store`; the public surface rejects POST.
The UI clears old results before refresh, navigation or failed access. It is
**not** automatically updated: changes take effect at the next refresh/load, not
by remotely recalling content already viewed or downloaded.

## Meaning of a count

- Population: all retained, nonblank saved answers for that event/question,
  including answers retained after participation removal. Current participation
  is not required. This follows the approved mock; no answer is erased here.
- YesNo: only exact YES / NO after whitespace trimming, two explicit groups,
  denominator = YES + NO. Null, blank and invalid legacy values are excluded.
- Text, Public=true and PublicText=false: answered count and display names only;
  one body-free group whose value is null. The private body is not projected from
  the database into the response or rendered DOM.
- Text, both flags true: groups use the exact trimmed body (case-sensitive ordinal
  equality), each count and respondent display names. No invented case/kana
  normalization or truncation is applied to the answers.
- Disabled/deleted/unpublished questions are entirely omitted even for admins.
  Text-body permission has no effect without aggregate publication.
- Identity output is only opaque user ID and display name, not email, contact
  fields, session identifiers, answer timestamps or authentication material.
  Retained disabled respondents remain represented in historical counts; this
  does not grant them login or profile access.

The participant form and DTO now explicitly indicate public counts/respondents,
YES/NO choice publication, and the independent text-body flag. Only response
records changed: submission payloads, fingerprints, replay identity, partial
success behavior, audit and business notification enqueueing remain unchanged.

The UI uses normal Razor text encoding, never raw HTML from questions, answers
or names. Captioned tables and column headers are available on small screens.
Horizontal ratio/text graphs, pointer/touch/focus respondent popups and the
integrated position between timetable and comments remain separate UI work.

## Storage, checks and operational limits

No migration or schema mutation is needed. Existing answer/privacy flags are
used as stored. Run:

```bash
dotnet run --project tests/Sem.PublicAnswerChecks -c Release
# Native passkey/migration/real Blazor harness, in an isolated SMTP/browser setup:
SEM_PUBLIC_ANSWER_CHECKS=1 node scripts/e2e.cjs
```

The native flag is exercised inside the existing storage-upgrade harness;
setting it alone without SEM_STORAGE_UPGRADE_COMMAND does not run the module.
`.github/workflows/public-answer-summary.yml` adds it to the full isolated Docker
acceptance pipeline and retains every earlier regression suite.

All fixtures are synthetic; no live NAS, real email recipients or Push providers
are contacted. This change does not merge a PR, publish a delivery bundle, or
change the DXP image, data, .env, keys or public hostname.

## Next increment: comments

Approved behavior: trimmed body minimum three characters; newest first; only
administrators can delete (even the author cannot self-delete). Ordinary viewers
can post through the end of the event day in Asia/Tokyo, excluding midnight of
the following day; administrators can post afterwards. Standalone comments do
not require performance/spectator registration. The combined registration flow
must preserve the mock's partial-success behavior and safe idempotent replay.
These are recorded prerequisites, **not implemented by this PR**.

The follow-on event-comments increment implements the comment prerequisites; see EVENT-COMMENTS.md for its scope and validation.

The follow-on MEMBER-PROFILE.md increment connects author/respondent names to authenticated basic profiles; anonymous QR/history remain separate.
