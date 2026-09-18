# Versioned consent receipts

> Review note: this document's legal interpretation requires qualified-counsel
> review before being published as legal advice. GDPR does not require a lawyer's
> approval to deploy this receipt architecture. The linked policies retain their
> recorded draft/review status; this note does not approve them or enable analytics.

## Ownership decision — 2026-09-18

Web and mobile own the exact immutable text presented for each optional purpose.
Identity owns authenticated account decisions and their server timestamps. The
former embedded backend text catalogue is superseded by this decision. Storing
the entire notice text in each backend row was an implementation choice, not a
requirement imposed by GDPR.

The backend embeds `Infrastructure/Consents/notice-versions.json`, containing
only `purpose`, `scope`, `locale` and `version`. The UTC ISO version currently also
identifies its effective-from instant, for example `2026-09-18T00:00:00Z`. This is
notice-version metadata, not a claim that a separate Privacy Policy entered into
force at that instant.

### Review status versus implementation permission

The owner has authorized implementation of client-owned notices and account
receipts. A lawyer's sign-off is not an additional statutory condition imposed by
this design. Earlier `requiresCounselReview` metadata and the draft-notice
changelog describe an existing project review condition for those legal texts,
not a requirement in GDPR to store text on the backend or to block this code
change. Do not infer completed review of the linked policies from this
implementation decision or silently relabel archived drafts as published.

No further approval workflow is introduced here. The legal question for use of
the receipts remains whether the actual decision is voluntary, informed and
demonstrable; the technical evidence described below supports that assessment.
Optional processing remains separately controlled by its existing release gates.

## What the receipt proves

The client submits the version it displayed, not an instruction to accept whatever
version the server currently considers newest. Identity validates the exact known
purpose/locale/version tuple and rejects unknown or not-yet-effective versions.
It does not derive the version from `RecordedAt`. An older browser tab or mobile
release may still display a known older notice after a newer one becomes effective.
Neither publication time nor acceptance time alone identifies what was displayed.

| Evidence | Authority / location |
|---|---|
| Account identity | Authenticated JWT context, never a user-supplied account ID |
| Purpose, scope and decision | Identity domain state; analytics and marketing remain separate |
| Exact displayed version and language | Submitted `noticeVersion` and `locale`, validated against the registry |
| Time the decision was recorded | Server clock `RecordedAt`, in UTC |
| Interface source | Bounded `web_onboarding`, `web_settings`, `mobile_onboarding`, `mobile_settings` |
| Decision history | Current state plus append-only accepted revisions; original expected revision and request ID retain retry/concurrency semantics |
| Text, linked information and presentation | Immutable client notice archive, pinned policy sources, and the corresponding released client implementation |

This is an engineering evidence model, not a statutory list of required columns.
It does not require IP addresses, browser fingerprints, session tokens or extra
personal data solely to prove consent. Request IDs and revisions protect integrity
and idempotency; they are not substitutes for the user's actual decision.

## Immutable client archive

Both client repositories retain `docs/consent-notices/2026-09-18.json`. It contains
the four exact PL/EN bodies previously identified as backend `draft.3`, their
purpose/scope/locale, the new receipt version and effective-from instant, and
provenance. Text is not silently changed under an existing version.

The archive pins the PL/EN Privacy Policy `0.13.0` sources to root repository commit
`a7c71aaba3b12b43559c3b6385ba33ce1d02c587` and their paths, alongside canonical public
URLs. It records the source documents' actual draft status, null effective date
and counsel-review metadata; archiving them does not change that status. A mutable
live URL alone is insufficient to reproduce prior information.

At release, retain the client commit/artifact that contains the matching notice,
links, labels, switches and save actions. Together with the archive and receipt
source, it must be possible to reconstruct the information and choice mechanism.
Do not rely on an unsupported statement that the website was configured correctly.
Any change to purpose or text requires a new immutable version and corresponding
registry entry before a client starts submitting it. Preserve old evidence and
keep withdrawal available when an old version is no longer accepted for new grants.

## API and historical continuity

The current-consent response contains receipt state, not `currentNotice` or text.
Clients attach their own localized notice for display and submission. Server
scope, status, revision, activation epoch and timestamp remain authoritative.

The existing `NoticeText` database field is retained. Pre-existing values are not
rewritten or erased; new decisions store an empty string, with their text resolved
through the client archive. The history API retains the legacy text field so old
evidence remains available; an empty value for a new row means archived client
text, not an empty notice shown to the user. No migration or destructive history
rewrite is part of this change.

The existing atomic state/history commit, optimistic revision, idempotent replay,
stale-write conflict and activation-epoch invariants remain in place. Withdrawal
can still use the previous stored decision when its version is absent from the
current registry. Removing a registry entry must not erase history or reinstate a
withdrawn grant. See [Account consent and analytics](consent-and-analytics.md).

## Consent is distinct from terms acceptance

These receipts concern optional processing purposes. Accepting service terms is
a different event and cannot supply consent to analytics or marketing. A similar
version-and-time mechanism may document terms acceptance, but its document scope
and decision must remain separate. This change adds no terms-acceptance flow.

No analytics release flag, provider configuration or marketing sender is enabled
by this ownership change. An account analytics grant remains separate from local
installation activation. Existing retention and processing obligations are not
resolved by changing where the text is stored; this work adds no new retention
period or implementation.

## Primary references

- [GDPR Article 7](https://eur-lex.europa.eu/eli/reg/2016/679/oj/eng): the controller
  must be able to demonstrate consent, distinguish it from other matters and
  support withdrawal; it does not prescribe the database representation.
- [EDPB Guidelines 05/2020, paragraphs 104–108](https://www.edpb.europa.eu/system/files/documents/files/file1/edpb_guidelines_202005_consent_en.pdf#page=22):
  evidence of when/how consent was obtained and what information was presented,
  with proportionate data collection and freedom to choose the method. Paragraph
  108 rejects merely referring to a correctly configured website.
- [EDPB paragraph 81](https://www.edpb.europa.eu/system/files/documents/files/file1/edpb_guidelines_202005_consent_en.pdf#page=18):
  blanket agreement to general terms is not consent to optional data processing.
