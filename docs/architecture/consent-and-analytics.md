# Account consent and analytics

## Consent register

Identity owns account-level optional decisions. They do not depend on organization permissions or the selected organization. JWT authentication remains mandatory; the update endpoint explicitly permits a current session whose membership is being removed so offboarding does not prevent withdrawal.

`UserConsents` has key `(UserId, Purpose)` and an optimistic `Revision`. Missing rows represent `unknown`, never consent. States are `granted`, `denied` and `withdrawn`. Purposes/scopes are fixed:

| Purpose | Scope |
|---|---|
| `product_analytics` | `palladin_web_mobile` |
| `email_marketing` | `palladin_email_news_and_offers` |

`UserConsentHistory` has key `(UserId, Purpose, Revision)` and unique `(UserId, Purpose, RequestId)`. Every accepted explicit decision updates current state and appends history in one domain commit. History includes the original expected revision, server time, bounded source and locale, and the exact client-displayed notice version. Historical `NoticeText` values remain unchanged; new decisions store an empty string in that legacy field, with text evidence held in the immutable client archive. An unchanged off decision advances the revision to reject concurrent stale grants. Retrying the same request ID/payload appends nothing and returns the current state, including any later withdrawal; reusing the ID with a different payload returns 409. Exact PK/unique races and optimistic conflicts roll back the entire pair.

API:

- `GET /api/account/consents?locale=en|pl`: both decision receipts without notice text, and `maxAgeSeconds` (default 60, configurable range 1–300).
- `PUT /api/account/consents/{purpose}`: `granted`, `expectedRevision`, `requestId`, `noticeVersion`, `locale`, `source`. Source is `web_onboarding`, `web_settings`, `mobile_onboarding` or `mobile_settings`. User identity always comes from JWT.
- `GET /api/account/consents/{purpose}/history?beforeRevision=...`: at most 50 decisions, descending revision, with the next revision cursor. This supports bounded self-service export; it does not invent a separate full-account export feature.

All responses use `Cache-Control: no-store`. No consent endpoint calls PostHog. FK deletion cascades from User through current decisions to history; account deletion itself is outside this change. The earlier publication process recorded retention and final-review conditions for the draft notices. These are project release conditions, not a statutory requirement for a lawyer's sign-off on this receipt implementation; this change does not claim to resolve retention or publish the linked draft policies.

The owner decision of 2026-09-18 replaces the server-owned text catalogue with
client-owned immutable PL/EN notices. Identity embeds the text-free
`Infrastructure/Consents/notice-versions.json` registry: purpose, scope, locale and
an exact UTC version/effective-from value. It accepts a submitted version only
when that exact tuple is registered and already effective; it never infers the
shown version from the time of acceptance. Clients attach their local notice to
the receipt state and submit that displayed version. Historical drafts remain
source evidence, not runtime text. The earlier server-text requirement was an
architecture decision, not a legal requirement to duplicate text in every row.
See [Versioned consent receipts](consent-version-receipts.md) for the archive and
proof model. Known previous decisions remain withdrawable when their versions
are no longer registered. The owner has authorized this implementation; it adds
no further approval workflow. It does not enable analytics or marketing, certify
legal clearance of the linked policies, or change retention obligations.

Client activation additionally needs an explicit local choice for that browser/application installation and fresh account consent. The API grant alone must not initialize an SDK on another installation. Anonymous landing localStorage decisions are separate and must not silently become account grants. Client denial does not disable backend business metrics.

### Deployment verification

Deploying the version-aware API, releasing matching client notice archives and
enabling optional processing are separate operations. Deploy registry support
before clients submit the corresponding versions.

- Verify the merged source revision completed the main test, release-artifact and
  staging-deployment jobs. A CI skip directive retained in a squash commit body
  also skips the push pipeline; a merged PR alone does not prove deployment.
- Confirm `/api/health` returns 200 and the API container remains running with
  the expected source revision after deployment.
- An unauthenticated `GET /api/account/consents` must return 401, not 404.
  This checks route availability without extracting or logging a user's token.
- Verify the deployed clients display the archived version they submit, and the
  API returns that receipt version with server time. Unregistered/future versions
  must fail without fabricating consent. Existing grant withdrawal must still
  work. This deployment does not enable analytics capture or marketing delivery.

`ActivationRevision` identifies one continuous grant epoch. A repeat grant for
the same notice retains that epoch, allowing another installation to activate
without disabling the first. A denial sets it to zero; a new grant or notice
version starts a new epoch. Clients store that value and notice version locally,
and require both to match fresh server state. Withdrawal followed by regrant must
never revive an installation activated under the old epoch.

The clients offer optional choices on eligible authenticated application entry,
after setup, verification and unlock, rather than during registration. Unknown
purposes are not grants. Refusal does not prevent account access; receipt and
archive changes do not redefine the existing client presentation flow.

## Backend analytics

The path remains domain event → successful commit → MassTransit trigger → `IAnalyticsService`. Analytics has no transport-context dependency or consent lookup. `PostHogAnalyticsService` copies only its explicit allowlist of business properties, drops arbitrary properties including client/session headers and per-resource IDs, disables person-profile processing and GeoIP, and adds server send time. Pseudonymous distinct IDs remain personal data; they are not advertised as anonymous. The unused person-identification API was removed.

Backend legitimate-interest retention and objection handling are separate release requirements; denying `product_analytics` is not an objection to that processing. Security audit is unaffected.

The import API's `format` label is free-form and is therefore omitted from capture,
even though import/export events still count completed operations. Reviewed
`filters_used` contains only filter names/counts, not filter values; failure codes
are closed enums and credential-denial reasons are server constants.

## Waitlist metrics

`WaitlistUpsertedEvent` carries `EntityChange.Created` for the first insert and `Updated` for a new verification link. Both deliver the requested verification email. Only `Created` produces `be:identity:waitlist-joined`; successful verification produces `be:identity:waitlist-verified`. No email or token reaches analytics. `occurred_at` carries the original business time, unchanged on message retries; `distinct_id` is the waitlist entry ID.

Report each metric as **unique distinct IDs**, grouped by `occurred_at` in UTC. This remains idempotent across MassTransit redelivery even though the SDK's raw event stream is at-least-once. Do not use raw event totals or delivery timestamps as the signup metric. Compare joined counts to `WaitlistEntries.CreatedAt` and confirmed counts to `VerifiedAt` in the same half-open UTC window; never use a frontend success event as the authority. An SDK/broker outage can leave reporting behind the database; the database remains authoritative.

The rename changes MassTransit message identity. Deployment must drain any existing `WaitlistJoinedEvent` messages before replacing the consumer; do not discard queued verification emails. This implementation does not deploy or purge a queue.

TokenHash and VerifiedAt are concurrency tokens. A link replacement invalidates stale verification writes; only one concurrent verification can commit. Concurrent inserts for the same normalized email return the same public 202 response using the exact email uniqueness constraint. Reissue/verify conflicts do not report another successful outcome.
