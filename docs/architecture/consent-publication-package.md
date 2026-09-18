---
status: preparation-only
reviewed_on: 2026-09-18
effective_date: null
requires_counsel_review: true
---

# Consent publication package

> Preparation only. The candidate notices require qualified-counsel review before
> publication, as already required by their source metadata and the existing
> consent release gate. This document is not publication approval or a legal opinion.

This package prepares the account-consent notices for the user-facing environment
at `stage.palladin.io`. The environment name does not make real-user processing a
test. It does not activate a catalogue, analytics transport or marketing delivery.

## Verified baseline

- Backend: `8f9dbf199a003ea1ded8f084f40b57338603d3da`.
- Web: `5aaff3942a49aa9d71f2ac4fe75114a46f92dcf1`.
- The embedded [active catalogue](../../src/modules/Identity/Palladin.Module.Identity/Infrastructure/Consents/notices.json)
  is empty. The separate [candidate catalogue](../../src/modules/Identity/Palladin.Module.Identity/Infrastructure/Consents/notices.draft.json)
  is not embedded and is not read by the runtime.
- [Consent architecture](consent-and-analytics.md) describes the existing legal,
  retention and objection gates. This package separates their processing purposes;
  it does not mark any open gate complete.
- Web has a separate `VITE_CLIENT_ANALYTICS_RELEASED` build flag. Its transport
  additionally requires a configured EU endpoint/key, a fresh account grant and
  matching local activation. A catalogue publication alone need not enable it.
  The reviewed staging build workflow supplies PostHog key/host inputs but does
  not set this release flag; missing values default to false in `env.ts`. This is
  source/workflow evidence, not proof of the deployed artifact's network behavior.
- The backend analytics module selects its no-op implementation when its project
  key is absent. Account consent is not its activation or objection mechanism.
- No marketing sender is implemented in the reviewed scope.

These are source facts, not evidence of current provider settings or live traffic.
Deployment checks must confirm the actual artifact and runtime configuration
without exposing keys, tokens or customer records.

## Candidate text and immutable version mapping

The following text is copied exactly from `notices.draft.json` at the backend
revision above. The [notice changelog](../../src/modules/Identity/Palladin.Module.Identity/Infrastructure/Consents/CHANGELOG.md)
records its provenance. Keep the short text together with the accessible policy
information; it is not a standalone Article 13 notice.

| Purpose | Scope | Existing candidate, both locales | Proposed publication identifier |
|---|---|---|---|
| `product_analytics` | `palladin_web_mobile` | `product-analytics-2026-09-13-draft.3` | `product-analytics-YYYY-MM-DD.1` |
| `email_marketing` | `palladin_email_news_and_offers` | `email-marketing-2026-09-13-draft.3` | `email-marketing-YYYY-MM-DD.1` |

`YYYY-MM-DD` and the effective date are **TBD after approval**, not this package's
review date. These are proposed names, not runtime records. Both locales of a
purpose use the same version. Preserve draft archives and every published text;
never change text or scope under an existing published version. Any required text
revision must return to review before receiving a publication identifier.

### Product analytics — Polish

Za zgodą mierzymy w PostHog EU korzystanie z funkcji Palladin, aby ulepszać aplikację. Nie zbieramy treści sejfu ani formularzy, haseł lub kluczy i nie nagrywamy sesji. Zgodę możesz wycofać w ustawieniach prywatności.

### Product analytics — English

With your consent, we use PostHog EU to measure how Palladin features are used and improve the app. We do not collect vault or form contents, passwords or keys, or record sessions. You can withdraw consent in Privacy settings.

### Email marketing — Polish

Za zgodą wyślemy Ci e-maile z nowościami i ofertami Palladin. Niezbędne wiadomości transakcyjne, dotyczące konta i bezpieczeństwa wysyłamy niezależnie od tej zgody. Zgodę możesz wycofać w ustawieniach prywatności.

### Email marketing — English

With your consent, we will send you emails with Palladin news and offers. Essential transactional, account and security messages are sent independently of this consent. You can withdraw consent in Privacy settings.

## Supporting information

Public pages checked on 2026-09-18 are still drafts, without an effective date.
Their availability is not evidence of publication clearance.

| Information | Version | Polish | English |
|---|---|---|---|
| Privacy, including controller, data, rights and processing purposes | `0.13.0` | [Policy and consent section](https://palladin.io/pl/polityka-prywatnosci#8-analityka-marketing-i-prawa) | [Policy and consent section](https://palladin.io/privacy/#8-analytics-marketing-and-rights) |
| Cookies and similar technologies, including local activation | `0.6.1` | [Policy](https://palladin.io/pl/polityka-cookies) | [Policy](https://palladin.io/cookie-policy) |

Before collecting decisions, reconcile both language versions with the exact
release scope, including whether choices are only recorded while processing
remains disabled. Keep controller identity, the types of data, purpose and
withdrawal information accessible before the decision. The current Privacy
section expressly says client analytics is not cleared and marketing delivery is
not implemented; do not silently turn those statements into claims of activation.
Record the approved document versions alongside the notice publication record.

## Separate release gates

| Stage | Evidence required before this stage | Current gap |
|---|---|---|
| Publish account notices and record decisions | Approved PL/EN notice presentation and supporting information; a justified consent-evidence retention schedule with an executable erasure process; verified refusal/withdrawal without loss of essential service | Candidates still require counsel review; retention of history has no approved maximum or implemented scheduled cleanup; user-row cascade does not provide full account deletion |
| Enable web/mobile client analytics | Recording gate plus actual transport release approval; EU provider configuration and applicable processor/transfer arrangements; an operational retention limit and tested deletion of historical events; withdrawal across installations; applicable age eligibility | Provider and retention evidence is not supplied by this package; public documents retain clearance conditions; the adopted under-18 exclusion needs enforcement before optional processing |
| Enable backend business measurement | Separate purpose/basis assessment and legitimate-interest balancing; event minimization; operational retention and deletion; effective individual objection/suppression; applicable age eligibility | The reviewed capture path has no individual objection lookup or suppression mechanism; source configuration alone does not establish provider-side deletion or retention |
| Send marketing email | Valid current purpose-specific consent checked before sending; easy withdrawal/unsubscribe and effective suppression; sender/provider and retention controls; applicable age eligibility | No marketing delivery pipeline is implemented or approved here |

Recording a marketing preference does not require inventing a marketing sender.
Equally, a saved preference does not approve one. Necessary transactional and
security messages keep their own purpose and basis; waitlist signup is not
marketing consent.

An unresolved objection mechanism for separate backend measurement is not a
reason to enable that measurement with account consent. Keep it disabled until its
own requirements are met. If the release is limited to recording preferences,
document and verify that limit instead of claiming the analytics gates passed.

## Retention decision needed for consent evidence

The smallest outstanding owner/legal decision is:

> For each purpose, approve how long the minimal evidence remains necessary while
> relying on consent, what event starts any period after withdrawal, replacement
> or account closure, and the justified period for that later evidence. Define
> which records of refusals and superseded decisions are needed, and the narrowly
> scoped exception for a specific dispute or legal duty, with an owner and review
> date. Specify how erasure requests and backup restoration are handled.

No numerical period is approved by this package. Do not infer one from a general
limitation period or use the longest possible claim period for every record.
Analytics-event retention is a separate decision from consent-evidence retention.

Current evidence is limited to:

- `UserConsentHistory` stores purpose/scope, decision, revisions, request ID,
  notice version/text, locale/source and server time.
- `UserConsentConfiguration` cascades deletion from the user to current consent
  and then history. This does not implement account deletion or time-based cleanup.
- `UpdateUserConsent` uses history for idempotency and withdrawal when the current
  notice is unavailable. A retention implementation must preserve these required
  behaviors; arbitrary removal of the latest proof would break withdrawal.

Missing evidence includes an approved schedule, executable cleanup or an
operationally adequate documented erasure procedure, its tests and monitoring,
and handling of necessary evidence during account deletion and backup restoration.
Record those results before declaring the recording gate complete.

## Activation procedure after the applicable gates pass

1. Record the exact scope of approval: recording only, client analytics, backend
   measurement and marketing are separate decisions. Fill the approved notice
   versions/date and policy versions; retain the existing draft evidence.
2. Confirm the actual user-facing web artifact leaves client analytics unreleased
   for a recording-only release, and that other clients of this API cannot begin
   capture from the newly available notices. Verify backend measurement is disabled
   unless separately cleared. Record configuration state without copying secrets.
3. Create a reviewed PR adding only approved immutable records to `notices.json`.
   Preserve prior versions; the last matching purpose/locale is selected by the
   current catalogue. Check PL/EN text equality against the approved package.
4. Test unknown choices, explicit denial, each separate grant, full acceptance,
   retries/conflicts, partial failure, unavailable notices and withdrawal of older
   grants. Verify that a save is not successful telemetry activation in a
   recording-only release. Test refusal/continuation without blocking the account.
5. Complete repository CI and release the reviewed commit through the existing
   deployment pipeline. Confirm the expected source revision, health HTTP 200 and
   a still-running API container; an unauthenticated consent read must return 401.
6. With an authorized test account, verify exact PL/EN versions and text, save and
   withdrawal/history behavior on the deployed artifact. Check absence of optional
   capture and marketing delivery for a recording-only release. Do not collect
   customer credentials or tokens in deployment evidence.

Enabling a transport later requires a separate release check. Revalidate that any
earlier decision still covers the same disclosed purpose/data and that local
activation is current; changed scope or notice must not reuse an old grant as
permission for new processing.

## Rollback and withdrawal continuity

- Stop optional transports first when an activation problem affects their lawful
  operation. Do not treat a UI hide or a cleared catalogue as immediate network
  cancellation on already-running clients; verify the resulting behavior.
- For a recording-only first publication, the pre-publication API artifact is a
  potential rollback target because it has the consent endpoints and an empty
  current catalogue. Prove it can still withdraw decisions stored under the new
  version using their persisted history before relying on it as rollback.
- Preserve database current state and history, including confirmed partial saves.
  Never reset consent tables, restore an old grant over a withdrawal, or delete
  proof merely to make the dialog disappear. No migration-history rewrite is part
  of this release or rollback.
- Preserve published source texts in version control and the release record;
  artifact rollback is not permission to rewrite an existing version. A later
  source release retains immutable previously published versions. There is no
  per-notice active/inactive selector in the current catalogue, so selectively
  suspending a purpose while retaining its records is not an existing switch.
- Verify withdrawal with no current notice, access to essential functions and
  fail-closed client behavior after rollback. Repeat the health/container/source
  checks and record the outcome.

## Applicable references

- [GDPR](https://eur-lex.europa.eu/eli/reg/2016/679/oj/eng): purpose/storage
  limitation and accountability (Article 5), consent evidence/withdrawal
  (Article 7), information (Articles 12–13), erasure and objection (Articles 17/21).
- [EDPB Guidelines 05/2020](https://www.edpb.europa.eu/system/files/documents/files/file1/edpb_guidelines_202005_consent_en.pdf):
  informed consent (paragraph 64), necessary and proportionate evidence
  retention (paragraphs 106–108), withdrawal (section 5.2).
- [Polish Electronic Communications Law](https://eli.gov.pl/api/acts/DU/2024/1221/text.html):
  prior marketing permission and terminal-device access (Articles 398–399).

These references support the existing gate; they do not establish that the
outstanding factual or approval evidence has been supplied.
