# Module: Identity

## Projects
- `Palladin.Module.Identity/` — identity domain, authentication, organization lifecycle and persistence.
- `Palladin.Module.Identity.Contracts/` — commands, lifecycle events and stable shared identity values.

## Business responsibility
Owns user identity and authentication: email+password sign-up/sign-in (zero-knowledge, Variant A — the login password *is* the master password), OAuth sign-in, TOTP second factor, email verification, the double-opt-in waitlist and its one-time personal Developer benefit, JWT issuance/refresh, sessions, and the organization + user profile lifecycle. It is the system's source of truth for who a user is; every other module keeps a read-only replica fed from Identity events.

### Versioned password-only Identity KDF
Security version 1 uses the immutable `identity-argon2id-password-v1` profile. The exact password UTF-8 bytes (no normalization, pre-hash or terminator) are passed to Argon2id 1.3 with 32,768 KiB, `t=2`, `p=1`, a random 16-byte per-account salt and a 32-byte output (`AccountRoot`). HKDF-SHA-256 uses the RFC 4122 network-order AccountId bytes as its extract salt and expands two independent 32-byte outputs with the exact UTF-8 labels `palladin/identity/password-v1/auth-credential` and `palladin/identity/password-v1/master-key`. Only `AuthCredential` crosses TLS; Identity re-hashes it with an independent server salt in `PasswordCredential`. AccountRoot, MK, plaintext private keys, VK and VDK are structurally absent from every backend DTO. The frozen cross-client vector is `tests/Fixtures/IdentityKdf/password-only-v1.json`.

The `User` aggregate owns `SecurityVersion`, monotonic `MinimumSecurityVersion`, immutable profile ID, credential/private-key-wrap revisions and opaque device-wrapper metadata. Registration and setup create version 1 state. This is a pre-production breaking cutover: no prior Identity KDF profile, account, recovery material or migration route is accepted. Password change and recovery update all affected fields through one domain commit; EF's transaction rolls back verifier, wrapper and session-revocation changes together. Recovery preserves the Member keypair and invalidates old device-wrapper metadata.

Auth endpoints (all `AllowAnonymous` unless noted; the anonymous ones ARE the authentication and are enumeration-safe):
- `POST api/auth/register` — create a version 1 org+admin user + server-side credential verifier + email-verification token; the client supplies the non-empty random `AccountId` already bound into its KDF output, `EmailVerified=false`; issues a session.
- `POST api/auth/login/salt` — returns the registered password-only profile's public parameters, immutable UUIDv4 `AccountId` and KDF salt; unknown emails get deterministic profile-bound pseudo-values. Unsupported or missing profiles fail validation.
- `POST api/auth/login` — constant-time credential verify with exact profile/version matching; durable fixed-window limits per IP/account plus an atomic account-wide lockout after four failed password/TOTP attempts against the normalized e-mail within five minutes, regardless of source IP (429 + `Retry-After`, 15-minute block); returns a session or, when TOTP is enabled, `{ totpRequired, challengeToken }`.
- `POST api/auth/login/totp` — redeems the single-use challenge with a TOTP code (±1 window, replay-protected) or a recovery code. IP/account limits run before expensive verification; after the second factor succeeds, the lockout reset, challenge consumption, factor state and refresh session share one domain commit.
- `POST api/auth/verify-email` — consumes a single-use token; expired vs invalid distinguished by error key. It can activate the matching waitlist benefit and returns the verified `UserId` plus the active exact benefit window.
- `POST api/auth/verify-email/resend` (JWT) — throttled re-issue; already-verified/OAuth is a 204 no-op.
- `POST api/auth/totp/enroll` · `/confirm` · `/disable` (JWT) — TOTP lifecycle; confirm returns one-time recovery codes (only hashes stored).
- `PUT api/account/password` (JWT) — version 1 password change with current-credential proof and credential/wrap revision CAS; preserves recovery material and revokes all refresh sessions.
- `PUT api/account/recovery` (JWT) — recovery-secret proof happens client-side; atomically installs version 1 authentication/wrapper state, preserves the Member keypair, invalidates old device wrappers and revokes all refresh sessions.

Waitlist endpoints (anonymous, enumeration-safe):
- `POST api/waitlist` — accepts only `{ email, language }`, normalizes the e-mail and sends a double-opt-in verification link subject to resend cooldown.
- `GET api/waitlist/verify` — consumes the single-use waitlist token and redirects to the configured success/failure page. It activates the benefit immediately when a matching verified account already exists.

Every successful auth-session response carries nullable `WaitlistDeveloperBenefitStartedAt` / `WaitlistDeveloperBenefitEndsAt`. A token elevated only by this benefit expires no later than its end.

Organization membership endpoints:
- `GET api/org` — current organization details plus its authoritative finite offline-access policy and monotonic policy version.
- `PUT api/org/offline-access-policy` (`OrganizationManagement`) — selects exactly `disabled`, `1h`, `4h` or `24h`. An exact retry is idempotent; a change increments the policy version, emits the normal organization-update Audit event and invalidates older access-token authority so the caller must refresh its session.
- `GET api/organizations` — memberships available to the user; identifies the organization selected by the current JWT.
- `POST api/auth/switch-organization` — issues a new access + refresh session only for a selected `Active` membership.
- `GET api/organization/member-directory` (authenticated organization member) — minimal `{userId, displayName}` identities for current and former members of the active organization. This endpoint exists for client-side attribution of opaque actor IDs in Audit and immutable Vault history; it never returns e-mail, roles, permissions or Vault data.
- `GET api/organization/members` (authenticated organization member) — members of the active organization, including role assignments, effective permissions and `AuthorizationVersion`; the Team view is read-only for callers without management permission.
- `GET api/organization/roles` (`OrganizationManagement` + verified email) — full system/custom role catalog, assignment counts and the full server-defined permission catalog. Every role and permission carries caller-aware `canAssign`; definitions are never hidden merely because the caller cannot delegate them.
- `GET api/organization/invitation-roles` (`AddUser` + verified email) — minimal `{id,name}` selector filtered to the immutable system `User` role and custom, non-`GrantManage` roles the current active caller may delegate. `Administrator` is never returned.
- `GET api/organization/invitations` (`AddUser` + verified email) — active-organization pending invitations only, including the role snapshot, original creation time, most recent sent time, expiry, server-computed resend availability and inviter display name; accepted, expired and cancelled history is never returned to the Team queue, and invitation tokens are never exposed.
- `POST api/organization/roles` (`OrganizationManagement` + verified email) — creates a custom role after tenant-scoped, case-insensitive name, assignable-mask and delegation-ceiling validation.
- `PUT api/organization/roles/{roleId}` (`OrganizationManagement` + verified email) — updates a custom role only when the current/proposed role and every affected member stay within the non-owner caller's effective permission ceiling; changed effective permissions invalidate sessions.
- `DELETE api/organization/roles/{roleId}` (`OrganizationManagement` + verified email) — deletes an unused custom role. System roles, member assignments and every invitation reference block deletion, so invitation history always retains its required role.
- `POST api/organization/invitations` — reserves a seat only for the immutable system `User` role or a custom, non-`GrantManage` role within the active caller's delegation ceiling, creates a hashed single-use token and emits an idempotent email command through Notification. Administrator is never invitation-assignable.
- `POST api/organization/invitations/{invitationId}/resend` (`AddUser` + verified email) — revalidates tenant, pending state, current actor authority, live role, delegation ceiling and the `GrantManage` fence, then rotates the token, invalidates the previous link and renews the TTL without changing `seatUsage`. A server-defined cooldown returns 429 before mutation; the new mail command has a resend-specific idempotency key.
- `DELETE api/organization/invitations/{invitationId}` (`AddUser` + verified email) — tenant-scoped, idempotent cancellation of a pending invitation. It invalidates the token and releases the reserved seat in the same optimistic organization-membership commit while retaining historical metadata.
- `PUT api/organization/invitations/{invitationId}/role` (`AddUser` + verified email) — replaces the role applied by the existing pending link without changing seat usage or resending the e-mail. The role must remain invitation-safe, caller-delegable and non-`GrantManage`; tenant, pending state and the shared organization fence are revalidated before commit.
- `POST api/organization/invitations/accept` — consumes the invitation only for an authenticated account with the same normalized email after revalidating the inviter's active `AddUser` authority, role-delegation ceiling and current `GrantManage` cutover fence, then returns a fresh auth session scoped to the joined organization.
- `PUT api/organization/members/{userId}/roles` (`OrganizationManagement` + verified email) — atomically replaces a non-owner member's non-empty role set only when both current and proposed effective permissions stay within the non-owner caller's ceiling; increments authorization version and revokes that organization membership's refresh sessions.
- `DELETE api/organization/members/{userId}` — idempotently starts removal of a non-owner membership. The membership remains `Removing` and refresh-token issuance fails closed while Vault completes every affected per-Vault rotation. Identity then revokes refresh sessions in bounded retryable commits and removes the membership in a final optimistic domain commit. Completion must match the active removal request; a timestamp-stale duplicate after the user has rejoined is ignored.

Account contract:
- `GET api/account` returns the current nullable `MemberKeyVersion` beside the wrapped private-key material. An unlocked client uses this server-authoritative version when sealing a new Vault key to its own Member public key; it must never assume version `1` after key rotation.
- `GET api/account/shared-unlock` (JWT) returns `{sharedUnlockEnabled, revision}` for the authenticated account, independent of the active organization. New and migrated accounts default to `true` with revision `1`; an explicitly stored `false` is retained. Responses are not cacheable.
- `PUT api/account/shared-unlock` (JWT) requires `{sharedUnlockEnabled, expectedRevision}`. Every accepted choice advances the revision, including choosing the already stored value: an explicit off must fence a concurrent enable even when already off. Stale revisions, concurrent writes and revision exhaustion return `409` with `shared-unlock-preference-conflict`; the client must fetch current state before a new intentional attempt. An optimistic user revision token also fences equal-clock writes. Missing fields return `400`; neither account nor environment can be selected by the payload. This preference creates no browser link, session or MK handoff by itself. The shared-unlock bootstrap and client propagation are separate work.

An organization role grants administrative capabilities only. It never grants cryptographic vault access: the Vault module still requires a separate `VaultMember` row containing a client-produced `WrappedVK`.

## Dependencies / references
- **ProjectReferences:** its own `Palladin.Module.Identity.Contracts`, the Audit, Notification, Search and Vault contract assemblies, plus shared `Palladin.Core.*` libraries.
- **Publishes** (`Palladin.Module.Identity.Contracts.Events`): `UserSignedUpEvent`, `UserLoggedInEvent`, `UserLoggedOutEvent`, `UserUpsertedEvent` (the canonical user-replica feed), `AccountSetupCompletedEvent`, `AccountRecoveryCompletedEvent`, `OrganizationCreatedEvent`, `OrganizationUpdatedEvent`, `OrganizationMemberInvitedEvent`, `OrganizationInvitationResentEvent`, `OrganizationInvitationCancelledEvent`, `OrganizationInvitationRoleChangedEvent`, `OrganizationMemberJoinedEvent`, `OrganizationMemberRoleChangedEvent`, `OrganizationMemberRemovalRequestedEvent`, `OrganizationMemberRemovedEvent`, `EmailVerifiedEvent` (→ analytics `be:identity:email-verified`), `EmailVerificationRequestedEvent` (in-module → `SendEmailCommand`, template `email-verification`), `WaitlistDeveloperBenefitActivatedEvent` (→ idempotent `waitlist-developer-benefit-activated` e-mail and `be:identity:waitlist-benefit-activated` with only `plan=Developer`), `TotpEnabledEvent` (→ `be:identity:totp-enabled`), `LoginAttemptFailedEvent` (→ structured security warning for every verifier-reached password/TOTP failure and, for a known account, denied `auth.login-failed` Audit row), `LoginLockedOutEvent` (→ `be:identity:login-locked-out` and, only for a verified known account, idempotent `login-lockout-alert` e-mail). Lockout analytics receives only the e-mail hash; the recipient address and collected source-IP list stay out of analytics. The per-attempt warning and Audit event intentionally retain that attempt's source IP as security evidence.
- **Consumes** `OrganizationMemberRemovalCompletedEvent` from Vault to resume the fail-closed membership/session removal workflow.
- **Publishes Search OpenHost commands:** active membership joins and profile changes index the server-visible Member catalog in every active organization; completed membership removal deletes only that organization's row.
- **Consumes** its own OpenHost onboarding commands (`Palladin.Module.Identity.Contracts.Commands`): `MarkOnboardingStepCommand` (per-user: entry-created, mobile-registered) and `MarkOrganizationOnboardingStepCommand` (org-level: api-key-created, agent-enrolled), published by the owning modules (Vault entry-created, Agents api-key-created / agent-enrolled, Notification mobile-registered). Otherwise Identity is source-only.

## Technologies used
EF Core + Postgres, MassTransit publish, custom JWT (`TokenService`), Google OAuth provider, `ICdnService` (org icon).

## Key aggregates (name + role)
- **User** — identity + on-device crypto key material + **per-user** onboarding. `IsOnboarded` is a stored flag set once at account-setup completion; `EntryCreated` / `MobileRegistered` are set by `MarkOnboardingStepCommand` (monotonic). A claimed waitlist benefit stores its exact personal start/end window here so effective session plan calculation does not depend on another module. Never computed cross-module.
- **Organization** — tenant + plan + **org-level** onboarding. `ApiKeyCreated` / `AgentEnrolled` are done for every member once anyone reaches them; set by `MarkOrganizationOnboardingStepCommand` (monotonic). The aggregate also owns the authoritative finite current-Entry offline policy (`disabled`, `1h`, `4h`, `24h`) and its monotonic policy version; new and migrated organizations default to version `1` with the bounded `24h` maximum. `GetAccount` composes the 4 onboarding steps from the User (2) + its Organization (2) — a plain join, no cross-module query.
- **Role / OrganizationMember / OrganizationMemberRole** — every organization starts with two immutable system roles: `Administrator` (`int.MaxValue`, assigned to the Owner) and `User` (`VaultCreate | VaultManage`, initially unassigned). `User` is invitation/direct-assignment eligible only within the same caller-aware delegation ceiling as a custom role; `Administrator` remains Owner-only for direct assignment and unavailable to invitations. Custom names are trimmed and unique per organization through `NormalizedName`; their flags must be a subset of the explicit server-owned assignable mask. A membership can have multiple roles through the composite junction but must always retain at least one; effective permissions are the bitwise OR of every assigned role. Owner protection lives on the membership. Role/member/invitation mutations increment the Organization `MembershipVersion` optimistic fence before their security-dependent reads, so a stale concurrent authorization decision loses without committing.
- **OrganizationMemberDirectoryEntry** — durable, organization-scoped attribution identity containing only `OrganizationId`, `UserId`, the last known `DisplayName` and `UpdatedAt`. Registration, OAuth account creation and invitation acceptance create or refresh it. It deliberately has no foreign key to `User` or `OrganizationMember`, so membership removal or account removal cannot erase historical attribution; deleting the organization cascades the directory. Future display-name mutation paths must refresh every directory row for that user. Vault history and Audit retain opaque actor IDs and never duplicate names into their event records.
- **OrganizationInvitation** — normalized email + required current role reference + denormalized role name + original `CreatedAt` + most recent `LastSentAt` + expiry + single-use token hash + accepted/cancelled terminal markers. Resend rotates the hash and moves `LastSentAt`/expiry while preserving creation time and seat usage; plaintext token exists only in the invitation event/email link. Every invitation retains its role reference for history, while only a pending invitation consumes an organization seat.
- **RefreshToken** — hashed long-lived session token scoped to one active organization and stamped with the membership `AuthorizationVersion` from issuance/rotation. Refresh requires both an `Active` membership and an exact version match, so a token lineage that races bulk revocation cannot revive after an authorization change.
- **OAuthConnection** — per-provider external identity link.
- **PasswordCredential** (1:1 User) — independently salted server Argon2id verifier of the request-only client `AuthCredential`, plus the public client KDF salt. Password users only; version/downgrade state remains account-wide on `User` so OAuth setup is covered too.
- **TotpCredential** (1:1 User) + **TotpRecoveryCode** — pending/active TOTP secret, replay high-water mark, and one-time recovery-code hashes.
- **VerificationToken** — single-use, hashed, TTL-bound token; `Purpose` (`EmailVerify` / `EmailChange` / `LoginTotpChallenge`) is always part of the lookup so a token can't be redeemed cross-purpose.
- **WaitlistEntry** — normalized e-mail, language, hashed double-opt-in token and verification time. A verified entry created no later than its matching verified account can be claimed once; the unique nullable `DeveloperBenefitUserId` plus the exact UTC start/end window records that one-time claim.
- **LoginLockout** — five-minute failure-window counter per normalized e-mail, protected by an optimistic `Version`; the fourth verifier-reached password/TOTP failure activates a 15-minute block for that account identifier regardless of source IP. Every successful authentication advances the account fence, inserting an empty row when the account was absent, so the final session commit loses against any concurrent failure. Every such failure publishes a distinct `LoginAttemptFailedEvent`; it contains only an attempt ID, SHA-256 e-mail hash, that attempt's IP, factor and optional opaque organization/target-user IDs. Known accounts receive an organization-scoped denied Audit row; unknown accounts cannot be tenant-scoped and therefore receive only the same-shaped structured security warning. Requests rejected before verification by outer rate limiting or an existing lockout do not create another failed-attempt event. Threshold activation publishes one `LoginLockedOutEvent` per account/window with the unique source IPs collected during that window. Only a verified known account contributes the recipient address/language used by the Identity e-mail trigger; unknown and unverified accounts never generate the security e-mail. The branded alert shows the exact threshold, all collected source IPs, UTC times, account-wide scope and automatic expiry, but contains no unlock action. A target-user/time-bucket idempotency key suppresses duplicate delivery within the aligned 15-minute lockout interval.
- **LoginRateLimitBucket** — PostgreSQL-backed fixed-window partition shared by all API replicas. Separate login/TOTP and IP/account partitions are HMAC-SHA-256 keyed, so the limiter table stores neither plaintext e-mail nor IP. A five-minute job removes partitions in bounded, `UpdatedAt`-indexed batches after the longer of the configured 15-minute idle retention and the active limiter window.

## Contracts (namespaces / types)
- Public enums consumed cross-module: `Palladin.Module.Identity.Domain.Enums` (`PlanType`, `AuthProvider`) — Audit reads these.
- Persistence contexts: `IdentityDbWriteContext`, `IdentityDbReadContext`, `IdentityDomainWriteContext`, `IdentityDomainReadContext`.
- Current Entry sync authority is carried as signed access-token claims containing the membership generation plus the organization offline policy/version. Identity validates all three values against its own membership and organization state during JWT authentication; Vault consumes only that already-authenticated request context and performs no live cross-module read. An authorized policy update accepts only the frozen finite enum, increments the version on an actual change and publishes an organization-update Audit event. The change therefore rejects an older access token, while refresh issues the current policy binding. The policy is never derived from a Vault wrapper.

## Critical points / invariants
- Google OAuth is a direct Google Identity Services flow, not Firebase Auth. `Modules:Identity:Google:ClientId` must match the public client ID used by the web/mobile client; Identity validates that it is non-empty at application startup and checks every Google token audience against it. Tracked settings keep this value empty; local development supplies it through the ignored `appsettings.Local.json` or an environment variable, while deployed environments inject their own value at runtime.
- Refresh tokens are long-lived (365-day) and rotate only when ≤ 7 days remain — do not shorten without coordinating clients.
- **Waitlist Developer benefit:** the public name is Developer; legacy `PlanType.Pro` is only its current JWT mapping. Activation requires a verified waitlist entry created no later than a verified account with the same normalized e-mail, regardless of verification order. Every activation-capable write holds an explicit transaction and locks the shared waitlist row through commit, so concurrent account/waitlist verification cannot leave both records verified without a claim. Login, TOTP, OAuth, refresh, organization switch and invitation acceptance all re-evaluate eligible pre-migration history before issuing a session and commit the claim even when a refresh token is not rotated. The benefit grants one UTC calendar month to the user, never the organization, creates no card/subscription/trial/renewal/charge, and never lowers a real higher organization plan. A JWT elevated only by the benefit is capped at its end so refresh returns the underlying plan immediately after expiry.
- New JWT claims must go through `JwtClaimNames` + a `ClaimsPrincipalExtensions` reader (the existing `is_onboarded` claim predates this and is the exception, not the pattern). See `../building-blocks.md`. The `email_verified` and `authz_ver` claims follow the pattern (`JwtClaimNames.EmailVerified` / `GetEmailVerified()`, `JwtClaimNames.AuthorizationVersion` / `GetAuthorizationVersion()`).
- **Hard email-verification gate:** `this.RequireEmailVerified()` (Core.Security) is applied per-endpoint to every user-facing (JWT) sensitive endpoint in Vault and Agents (vault/entry/grant CRUD + reveal + lists, agents, API keys) — an unverified password user gets **403 with error key `errors.backend.email-not-verified`** (distinct from a plain permission 403, so the client can target only this case and redirect to `/verify-email`). It is NOT applied to Identity auth/account endpoints (login/register/verify/resend/logout/GET account/TOTP/recovery) or to agent-authenticated endpoints (agents have no email verification). OAuth users carry `email_verified=true`, so they pass. The pre-processor honours the runtime switch `IEmailVerificationGate` (backed by `Modules:Identity:EmailVerification:GateEnabled`, default true) — set it false to disable the gate via config without a redeploy.
- **Member key version:** Identity owns a persisted, monotonically increasing version for the user's X25519 public key. Registration/account setup establish version `1`; every actual public-key rotation increments it and publishes the authenticated directory event. Password/account recovery only re-wraps the same private key and therefore must not change the public key or its version. At the pre-production protocol cutover, existing key material is deliberately cleared and affected accounts repeat setup; no version backfill or compatibility mode may leave the separate Vault key directory empty.
- **KDF downgrade fence:** the version/profile pair is registry-pinned, `MinimumSecurityVersion` never decreases, revision columns are concurrency tokens, and validators plus domain methods reject any profile other than password-only v1.
- **KDF-bound account identity:** version 1 registration requires a client-generated random UUIDv4 `AccountId`, because that immutable public identifier is the frozen HKDF extract salt. Identity uses it as the aggregate ID in the same transaction as the organization, verifier and wrapped key material; an empty, wrong-version or colliding ID creates no partial account state.
- KDF binary fields use canonical unpadded base64url. Identity security writes are rate-limited per authenticated user (IP fallback before identity is available). Login/TOTP first acquire durable per-IP and per-account permits, then update the atomic account-wide lockout keyed by normalized e-mail. Failure-counter conflicts retry on fresh domain contexts; a full-auth reset is staged in the endpoint context and committed with the session, while a concurrent failure conflict rejects the authentication fail-closed with 429.
- The account-wide lockout is an explicit security/product trade-off: anyone who knows an account e-mail can intentionally cause a 15-minute password/TOTP denial of service by producing four verifier-reached failures in five minutes. The outer per-IP and per-account request limits constrain abuse volume but do not eliminate this targeted DoS. Do not silently weaken the global threshold or add an e-mail unlock link; either change requires an explicit product/security decision.
- Password/AuthCredential/AccountRoot/MK/TOTP-secret/recovery-codes/verification-token must never appear in logs, analytics, or Serilog. AuthCredential verification and the stored-hash comparison are constant-time (`CryptographicOperations.FixedTimeEquals`).
- Existing accounts are all OAuth, so the `AddEmailPasswordAuth` migration backfills `EmailVerified = true`; the OAuth sign-in path sets it true going forward.
- A user can belong to multiple organizations, but each JWT and refresh-token lineage carries exactly one `org_id`. JWT authentication validates `(sub, org_id, authz_ver)` against the current `OrganizationMember` on every request. A normal role-assignment or effective-permission change increments `AuthorizationVersion` and revokes all unrevoked refresh tokens for that membership in the same Identity commit, so old access and refresh tokens stop working immediately. A no-op does not invalidate sessions. A staged membership-removal row deliberately remains authorization-current for reads until its Vault removal workflow completes; every unsafe HTTP verb carrying the complete user-JWT claim set requires an `Active` membership by default. Only explicitly reviewed account/exit operations and the read-only Member snapshot/delta and GlobalSearch POSTs opt out through `AllowNonActiveOrganizationMembershipMetadata` in the endpoint's `Configure()` method. Login, TOTP completion, OAuth, refresh and organization switch never issue a session for a non-active membership.
- **Current fail-closed `GrantManage` stage:** Identity and Vault do not yet have the durable role-change operation, staged `VaultReasonRecipientSet` wire/client material or `CutoverPending` protocol required for an honest cross-module recipient cutover. Therefore any role definition or member-role replacement that would change a member's effective `GrantManage` eligibility returns `409` with `organization-role-grant-manage-cutover-unavailable` before any role, version or refresh-token mutation. A role containing `GrantManage` is not invitation-assignable, and acceptance revalidates the current role and returns the same `409` with zero invitation or membership mutation if the role would grant it. This is an intentional safe restriction, not the final `202 + operationId` saga described by the target PRD; it must not be relaxed to a synchronous role write.

### Candidate shared-unlock receiver proof

`Infrastructure/SharedUnlock/SharedUnlockIdentityProof` verifies the candidate
`palladin.shared-unlock.identity-proof.v1` Ed25519 contract for the receiver's
separate consume and commit stages. Expected operation ID, random challenge,
SHA-256 of the authorized MK transcript, signing public key and exact
millisecond validity come from Identity's authoritative operation state;
untrusted input supplies only the signature. Lifetime is at most 30 seconds.
The verifier does not create sessions or implement replay prevention itself.

The matching fixtures in `tests/Fixtures/SharedUnlock/identity-proof-v1.json`
are generated synthetic Node Ed25519 vectors from palladin-protocol and are
verified independently with NSec 26.4.0. The operation endpoints below use this
verifier with persisted one-time state and current authorization. No MK or client
private key is sent to or stored by Identity. Browser adapters and linked logout
remain incomplete.

### Shared-unlock local-link authority (increment)

Identity now persists nonsensitive browser-link state in `SharedUnlockLinks`,
scoped by `(UserId, Id)`. The client supplies a random, stable local link ID after
verified browser discovery; that ID is a correlation marker, not authentication
or proof of extension identity. Each operation remains authenticated as its owner.
The browser adapter must retain the marker after disconnect and never generate a
new ID merely to bypass revocation.

- `POST api/account/shared-unlock/links`: creates a **locked** link, revision/epoch
  1, only with the current enabled account preference. A duplicate ID returns 409
  and cannot overwrite or recreate a revoked link.
- `GET api/account/shared-unlock/links/{LinkId}`: returns only link ID, revision,
  epoch, state (`locked`, `active`, `revoked`), `lastInvalidationSequence` and
  `lastLogoutSequence`, with `Cache-Control: no-store`.
- `POST .../{LinkId}/lock`: requires link revision plus current enabled preference
  revision. It locks the link and advances both counters even for a repeated lock.
  A queued lock after OFF returns 409 without propagating the action.
- `POST .../{LinkId}/disconnect`: requires the current link revision, records
  `revoked` and advances both counters. It works independently of the preference.
- `POST .../{LinkId}/reconnect`: requires the current revision and a revoked link;
  it explicitly returns the link to **locked** in a new epoch. It never enables
  the preference or restores an old unlock.

Lock/disconnect remain available for an authenticated account even when the JWT's
selected organization membership is no longer active, so users can close their
local security context. Create/reconnect retain the normal mutation membership
boundary. Foreign accounts receive 404 for both reads and mutations. Other link
rows and other devices' account preferences are unaffected.

The link revision is an EF concurrency token. Creation and lock additionally
force a no-op UPDATE of the tracked User preference revision, using its existing
concurrency tokens to fence concurrent OFF in the same domain commit. A losing
race rolls back the whole operation, including a newly inserted link. No raw SQL,
explicit transaction or extra index is needed beyond the account-scoped PK/FK.
The migration is incremental; all prior migrations remain untouched.

Activation is available only through the authenticated manual-unlock authorization
below. These endpoints do not prove that a browser is trusted, issue receiver
tokens, transfer keys or yet notify clients. The operation endpoints below issue
receiver sessions after consuming current authority. Linked logout is described below;
browser propagation remains required before the complete feature is enabled. A successful
metadata mutation must not be presented as completed browser lock/logout until
adapters have actually handled it.

### Session second-factor authority for shared unlock (increment)

Refresh sessions now retain nullable `SecondFactorRevision` and
`SecondFactorVerifiedAt`. Only successful `login/totp` completion records this
assurance, including a single-use recovery-code completion. Password-only,
OAuth, organization-switch and invitation sessions do not infer assurance from
the account's enabled factor or from a fresh session creation timestamp.
Existing sessions migrate with no assurance. Refresh rotation copies both
values exactly; it never upgrades an unverified session or renews verification
age. The fields are server-owned and are absent from client request/response DTOs.

`TotpCredential.ConfigurationRevision` advances on enrollment restart,
confirmation/replacement and disable, independently of timestamps and ordinary
code use. It is an optimistic
concurrency token. Login completion forces an update of that tracked factor even
when only a recovery-code row changed, so a concurrent configuration replacement
rolls back challenge consumption and session issuance through the existing
authentication-conflict response. The login mutation uses one domain write
context throughout. Current-factor evaluation additionally rejects inactive
refresh sessions and a factor belonging to a different account.

This metadata preserves already-completed 2FA for the source-unlock
authorization below. It does not itself authorize MK transfer or activate a link.
The bootstrap must still verify the source's own live session, manual-unlock
authority or inherited lease, current factor, preference, link epoch, account key
revisions, membership and inherited deadlines in its atomic commit. A missing or
obsolete factor assurance requires actual step-up; having MK is insufficient.


### Manual-unlock source authorization and local binding (increment)

`POST api/account/shared-unlock/authorizations` requires JWT plus the source's own
refresh token, fresh password-derived AuthCredential, its RAM generation, current
preference/credential/wrapper revisions and its current idle/absolute/offline
ceilings. AuthCredential goes directly to Identity and is cleared after the
verifier; it is never handed to a peer. Account and organization come from JWT,
not the payload. The refresh session must match that account/organization and
current active membership. Missing or obsolete 2FA assurance returns 403 with
`shared-unlock-step-up-required`; MK alone cannot establish this authorization.
Password verification reuses the existing durable IP/account rate limits and
account lockout. Expired/revoked/foreign sessions are rejected before verification.
A manual unlock may record its own authority while sharing is OFF, with the current
preference revision. It creates no peer session and permits no sharing until ON;
subsequent binding still requires enabled preference and unchanged valid limits.

Identity stores one `SharedUnlockAuthorization` per logical source session. A
fresh manual proof replaces its nonce/generation and snapshots key, membership,
2FA and time authority. Deadlines must still be future, idle cannot exceed
absolute, and none may exceed the current source refresh-session expiry. These
are ceilings declared by the trusted source under its client timeout policy;
they do not grant offline Vault access or replace the Vault's signed offline
policy/leases. Browser consumers must preserve their reviewed policy and timers.
Binding and refresh never reset those ceilings or the original unlock timestamp.

Refresh rotation retains a logical `SessionId` (old pre-feature rows use their
own ID until rotation). Authority therefore survives legitimate token rotation
without copying tokens between peers or renewing its deadlines. Presenting the
revoked old refresh token still fails; the source submits its own current token.
The authorization ID itself is neither a bearer token nor browser authentication.

`POST api/account/shared-unlock/links/{LinkId}/activate` binds that exact nonce,
RAM generation and source session to one account-owned link. It revalidates
preference, membership/status, key revisions, current 2FA, source session and all
ceilings. A locked link becomes active in a new epoch. An already-active link
keeps its epoch; retries for the same bound epoch are harmless. One authorization
cannot bind another link or return after its bound epoch changes.

A monotonic `User.SharedUnlockSequence` orders fresh manual proofs and local
lock/disconnect/reconnect commits, independently of clock resolution. Each closing
or reconnect action stores its sequence in that link's `LastInvalidationSequence`.
An old unbound proof cannot activate it even after explicit reconnect; fresh
manual authority must be newer than the barrier. Barriers are per link, so another
device's link is not implicitly revoked. The stable nonsensitive client marker
must still survive disconnect; inventing a new marker is not a reconnect path.

User sequence, authorization sequence/binding, link revision, membership status
and source-token revocation use optimistic concurrency in one domain commit.
TOTP confirm/disable also advance the User sequence, fencing first-factor-row
creation as well as replacement; stale writes return 409. No raw SQL or extra
transaction is introduced. `MarkPropertyAsUpdated` preserves original tracked
concurrency values and pending domain changes rather than reattaching the object.
The new migration is incremental and no historical migration is changed.

The operation and activity endpoints below use this authorization. Browser
transport/UI remain required; these APIs alone do not deliver or install an MK.


### One-time shared-unlock operation and independent receiver session (increment)

The source first authenticates the exact browser route, fresh client generations,
X25519 offers and receiver Ed25519 public key. The browser/configuration is the
independent authority for extension ID, origins, document and generation; request
shape checks on Identity do not attest browser identity. Owner-approved same-ID
extension substitution/full-profile compromise remains outside this boundary.

- `POST api/account/shared-unlock/operations` requires source JWT, its own current
  refresh token, bound authorization nonce/link epoch/preference revision, explicit
  receiver organization, the source's current effective `idleDeadlineMs`,
  `absoluteDeadlineMs`, `offlineDeadlineMs`, and verified channel/public-key metadata.
  Required ceilings include a shorter local client policy. Identity clamps them to
  the current root and refresh expiry, additionally caps idle by effective absolute,
  and rejects expired ceilings. It snapshots current authority, generates a challenge, binds the complete MK transcript and
  creates an offered operation. TTL is at most 30 seconds, capped by every source
  effective deadline and current refresh expiry. The committed receiver root keeps
  these shorter ceilings through subsequent handoffs, even if the next request
  declares longer limits. Original unlockedAt, sequence and MFA age remain unchanged;
  the original source root is not shortened by a peer handoff. Receivers still apply
  their own shorter policy and submit those effective limits if they become a source.
  The source never forwards its own tokens.
- `POST api/auth/shared-unlock/operations/{OperationId}/consume` is anonymous at the
  JWT layer because the receiver may be signed out. A valid Ed25519 consume proof
  bound to the persisted operation is mandatory. Identity rechecks all current
  authority and atomically moves offered to consumed. It returns the operation
  and authoritative account key descriptor; it issues no session yet.
- `POST .../{OperationId}/commit` requires the distinct commit proof and consumed
  state. Identity repeats current authority checks, issues the receiver's own
  access/refresh session, stores inherited unlock authority and records committed
  state in one domain commit. A replay, wrong order or losing concurrency race
  cannot issue a second session. Unknown/invalid/expired proof returns 401;
  changed authorization or already-used state returns 409. Responses are no-store.

The receiver organization may differ from the source organization for the same
account. Source and target membership, authorization versions and offline policy
versions are independently checked. The new session uses only the target's current
permissions/version. No foreign account or organization without active membership
can be selected. Client account selection and browser generation remain mandatory
independent adapter checks.

`palladin.shared-unlock.key-context.v1` hashes the account ID, security/minimum
versions, KDF profile/salt, credential/wrapper/member-key revisions, member public
key and encrypted private-key wrapper. Identity obtains these from its own current
User and rechecks the digest before consume/commit. It does not accept a descriptor
from the peer as authority. Node-generated fixtures verify every field binding;
independent complete MK-transcript vectors also match the backend byte-for-byte.
This is ciphertext/public metadata, never MK, private key or Vault plaintext.

Inherited authority has a fresh nonce and the receiver's own logical session ID,
organization and RAM generation. It copies the original source sequence, link/epoch,
unlock timestamp, all three ceilings and original MFA verification time. Issuance
uses the ordinary session issuer; it does not activate or restart a waitlist benefit.
After commit the child has no dependency on the parent refresh record. It may become
a source for a new peer within the original limits. Source token rotation during
an unfinished operation invalidates that attempt; the source starts a fresh attempt
with its own current token. No handoff resets idle, absolute or offline time.

User, source session, source authorization, both memberships/organizations, factor
and link are fenced with existing concurrency tokens. Operation revision provides
the one-time fence; a conflict rolls back operation state, new tokens and child
authority together. The operation key lookup uses its PK; source/member/link lookups
use their existing account-scoped indexes. No explicit transaction or raw SQL is
introduced. Two append-only migrations add the metadata table and the distinct
`(ExpiresAt, Id)` cleanup access path; no earlier migration is rewritten.

`CleanupSharedUnlockOperationsJob` removes only expired operation metadata in
ordered bounded batches, clearing tracking between commits. It runs every five
minutes by default and never removes the independently stored session, inherited
root or link. Concurrent cleanup conflicts are retried on a subsequent batch/run.
Options live at `Modules:Identity:CleanupSharedUnlockOperationsJob`; test hosting
disables scheduling and invokes the job directly. The API bounds offer POSTs to
30 per authenticated account/minute and anonymous proof POSTs to 60 per IP/minute.

This remains an incomplete feature draft: consumer/API contract closure, receiver
installation/ACK, browser adapters/UI and the complete supported-browser artifact
matrix are still required. Passing Identity tests does not prove that a
browser received or installed an MK.


### Linked logout, own activity and operational availability

`POST api/account/shared-unlock/links/{LinkId}/logout` requires JWT, the current
link revision and enabled preference revision. One domain commit locks the link,
advances its epoch/revision and records both invalidation and logout sequences.
It emits the existing `UserLoggedOutEvent`. It remains available when the selected
membership is inactive, including an already locked link; a foreign link returns
404. OFF rejects propagation with 409. The client must use ordinary local logout
while OFF and must not echo another shared logout after receiving a peer action.

A linked session whose authorization sequence is at or below the durable logout
barrier cannot refresh, create another handoff or replace its root with a fresh
password proof. Rotation retains the same logical session and cannot escape the
barrier. A newly authenticated Identity session may manually unlock and bind with
a newer sequence; that never revives previous sessions. OFF/ON, reconnect and
reactivation retain the old logout barrier. Other links and accounts are unaffected.
The receiver commit response exposes `authorizationSequence` beside its nonce so
clients can compare their own authority with authenticated link barriers.

Refresh fences the User sequence, source authorization, link revision, refresh
revocation and membership, including when no rotation is needed. This covers a
concurrent first authorization or logout without a phantom-session escape. The
existing waitlist activation transaction remains in place. Conflicting refreshes
return 409 (`session-refresh-conflict`) without partial issuance; clients must
coordinate their own refresh and recover current state, not blindly replay a
superseded token. Existing stateless access JWTs retain their ordinary expiry;
adapters still have to clear keys and revalidate link state before using them.

Ordinary logout and replay rejection share an account-scoped refresh-lineage walk.
They follow replacement pointers even when the presented token has already been
rotated. A concurrent rotation causes a bounded reload/retry of the whole lineage;
a lost race cannot silently leave a new descendant active. Exhausted retries return
409 instead of a false success. `Modules:Identity:Jwt:RefreshTokenConcurrencyRetryLimit`
defaults to 4 (allowed 1–16). Related lookups use the existing account-scoped PKs.
Authorization roots and link barriers must remain available while their refresh
lineages can be used; operation cleanup never removes either authority.

`POST api/account/shared-unlock/authorizations/activity` requires JWT, the client's
own current refresh token, authorization nonce, RAM generation and an absolute
`idleDeadlineMs` calculated at an actual activity event. It only advances that
client's idle deadline within its original absolute deadline. It never changes
unlocked-at, absolute/offline limits, another peer's timer or MFA age. Replaying
the same deadline cannot add server time. Expired, locked, disconnected, logged-out
or obsolete authority cannot be revived; old activity loses to current authority
through optimistic concurrency, including the idle deadline itself. The endpoint
also works while preference is OFF because it maintains only the client's own
still-valid authority. It returns the existing manual-authorization response and
is limited to 60 requests per account/minute. Browser adapters must submit actual
trusted activity, not heartbeat, navigation, worker restart or handoff events.

`Modules:Identity:SharedUnlock:Enabled` is a separate operational switch, default
true. Create, consume and commit check it on entry and before their domain commit;
when false they return 503 without issuing a receiver session. The switch neither
changes the user's preference nor disables ordinary login/refresh, local activity
or closing actions. Apply it consistently to every API process; it is configuration,
not a distributed transaction fence against a change during an in-flight commit.

Two incremental migrations add `LastLogoutSequence` and record idle-deadline
concurrency metadata. The latter has no SQL schema changes. No prior migration
is edited. Integration tests cover linked and independent sessions, rotation and
logout races, fresh login after group logout, OFF behavior, replayed/expired/stale
activity, hard-limit preservation and all three disabled operation stages.


The candidate protocol `v1/session-api.md` and generated `session-api-fixtures.json`
specify REST ownership, receiver installation/ACK and lifecycle requirements. The
backend keeps a byte-identical test copy at `tests/Fixtures/SharedUnlock/session-api-v1.json`.
Provider contract tests cover all 47 wire response variants (including all 16
combinations of independent session fields in each direction), distinct source/
recipient organizations and versions, coherent descriptor/proof bindings and the seven
actual link-domain transitions. These tests do not replace browser consumer tests
or prove MK installation; adapters must use the same fixtures before release.
