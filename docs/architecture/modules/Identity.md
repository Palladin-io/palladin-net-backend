# Module: Identity

## Business responsibility
Owns user identity and authentication: email+password sign-up/sign-in (zero-knowledge, Variant A — the login password *is* the master password), OAuth sign-in, TOTP second factor, email verification, JWT issuance/refresh, sessions, and the organization + user profile lifecycle. It is the system's source of truth for who a user is; every other module keeps a read-only replica fed from Identity events.

### Versioned password-only Identity KDF
Security version 1 uses the immutable `identity-argon2id-password-v1` profile. The exact password UTF-8 bytes (no normalization, pre-hash or terminator) are passed to Argon2id 1.3 with 32,768 KiB, `t=2`, `p=1`, a random 16-byte per-account salt and a 32-byte output (`AccountRoot`). HKDF-SHA-256 uses the RFC 4122 network-order AccountId bytes as its extract salt and expands two independent 32-byte outputs with the exact UTF-8 labels `palladin/identity/password-v1/auth-credential` and `palladin/identity/password-v1/master-key`. Only `AuthCredential` crosses TLS; Identity re-hashes it with an independent server salt in `PasswordCredential`. AccountRoot, MK, plaintext private keys, VK and VDK are structurally absent from every backend DTO. The frozen cross-client vector is `tests/Fixtures/IdentityKdf/password-only-v1.json`.

The `User` aggregate owns `SecurityVersion`, monotonic `MinimumSecurityVersion`, immutable profile ID, credential/private-key-wrap revisions and opaque device-wrapper metadata. Registration and setup create version 1 state. This is a pre-production breaking cutover: no prior Identity KDF profile, account, recovery material or migration route is accepted. Password change and recovery update all affected fields through one domain commit; EF's transaction rolls back verifier, wrapper and session-revocation changes together. Recovery preserves the Member keypair and invalidates old device-wrapper metadata.

Auth endpoints (all `AllowAnonymous` unless noted; the anonymous ones ARE the authentication and are enumeration-safe):
- `POST api/auth/register` — create a version 1 org+admin user + server-side credential verifier + email-verification token; the client supplies the non-empty random `AccountId` already bound into its KDF output, `EmailVerified=false`; issues a session.
- `POST api/auth/login/salt` — returns the registered password-only profile's public parameters, immutable UUIDv4 `AccountId` and KDF salt; unknown emails get deterministic profile-bound pseudo-values. Unsupported or missing profiles fail validation.
- `POST api/auth/login` — constant-time credential verify with exact profile/version matching; per-(email,ip) rate-limit + lockout (429); returns a session or, when TOTP is enabled, `{ totpRequired, challengeToken }`.
- `POST api/auth/login/totp` — redeems the single-use challenge with a TOTP code (±1 window, replay-protected) or a recovery code.
- `POST api/auth/verify-email` — consumes a single-use token; expired vs invalid distinguished by error key.
- `POST api/auth/verify-email/resend` (JWT) — throttled re-issue; already-verified/OAuth is a 204 no-op.
- `POST api/auth/totp/enroll` · `/confirm` · `/disable` (JWT) — TOTP lifecycle; confirm returns one-time recovery codes (only hashes stored).
- `PUT api/account/password` (JWT) — version 1 password change with current-credential proof and credential/wrap revision CAS; preserves recovery material and revokes all refresh sessions.
- `PUT api/account/recovery` (JWT) — recovery-secret proof happens client-side; atomically installs version 1 authentication/wrapper state, preserves the Member keypair, invalidates old device wrappers and revokes all refresh sessions.

Organization membership endpoints:
- `GET api/organizations` — memberships available to the user; identifies the organization selected by the current JWT.
- `POST api/auth/switch-organization` — issues a new access + refresh session scoped to a selected membership.
- `GET api/organization/members` — members of the active organization.
- `GET api/organization/roles` — system and custom roles available in the active organization.
- `POST api/organization/invitations` — reserves a seat, creates a hashed single-use token and emits an idempotent email command through Notification.
- `POST api/organization/invitations/accept` — consumes the invitation only for an authenticated account with the same normalized email.
- `PUT api/organization/members/{userId}/roles` — atomically replaces a non-owner member's role assignments.
- `DELETE api/organization/members/{userId}` — idempotently starts removal of a non-owner membership. The membership remains `Removing` until Vault confirms every affected per-Vault rotation; Identity then revokes all organization refresh sessions and removes the membership atomically. Completion must match the active removal request; a timestamp-stale duplicate after the user has rejoined is ignored.

Account contract:
- `GET api/account` returns the current nullable `MemberKeyVersion` beside the wrapped private-key material. An unlocked client uses this server-authoritative version when sealing a new Vault key to its own Member public key; it must never assume version `1` after key rotation.

An organization role grants administrative capabilities only. It never grants cryptographic vault access: the Vault module still requires a separate `VaultMember` row containing a client-produced `WrappedVK`.

## Dependencies / references
- **ProjectReferences:** its own `Palladin.Module.Identity.Contracts`, the Audit, Notification, Search and Vault contract assemblies, plus shared `Palladin.Core.*` libraries.
- **Publishes** (`Palladin.Module.Identity.Contracts.Events`): `UserSignedUpEvent`, `UserLoggedInEvent`, `UserLoggedOutEvent`, `UserUpsertedEvent` (the canonical user-replica feed), `AccountSetupCompletedEvent`, `AccountRecoveryCompletedEvent`, `OrganizationCreatedEvent`, `OrganizationUpdatedEvent`, `OrganizationMemberInvitedEvent`, `OrganizationMemberJoinedEvent`, `OrganizationMemberRoleChangedEvent`, `OrganizationMemberRemovalRequestedEvent`, `OrganizationMemberRemovedEvent`, `EmailVerifiedEvent` (→ analytics `be:identity:email-verified`), `EmailVerificationRequestedEvent` (in-module → `SendEmailCommand`, template `email-verification`), `TotpEnabledEvent` (→ `be:identity:totp-enabled`), `LoginLockedOutEvent` (→ `be:identity:login-locked-out`; carries an email **hash**, never the address).
- **Consumes** `OrganizationMemberRemovalCompletedEvent` from Vault to finalize the membership and session removal transaction.
- **Publishes Search OpenHost commands:** active membership joins and profile changes index the server-visible Member catalog in every active organization; completed membership removal deletes only that organization's row.
- **Consumes** its own OpenHost onboarding commands (`Palladin.Module.Identity.Contracts.Commands`): `MarkOnboardingStepCommand` (per-user: entry-created, mobile-registered) and `MarkOrganizationOnboardingStepCommand` (org-level: api-key-created, agent-enrolled), published by the owning modules (Vault entry-created, Agents api-key-created / agent-enrolled, Notification mobile-registered). Otherwise Identity is source-only.

## Technologies used
EF Core + Postgres, MassTransit publish, custom JWT (`TokenService`), Google OAuth provider, `ICdnService` (org icon).

## Key aggregates (name + role)
- **User** — identity + on-device crypto key material + **per-user** onboarding. `IsOnboarded` is a stored flag set once at account-setup completion; `EntryCreated` / `MobileRegistered` are set by `MarkOnboardingStepCommand` (monotonic). Never computed cross-module.
- **Organization** — tenant + plan + **org-level** onboarding. `ApiKeyCreated` / `AgentEnrolled` are done for every member once anyone reaches them; set by `MarkOrganizationOnboardingStepCommand` (monotonic). `GetAccount` composes the 4 onboarding steps from the User (2) + its Organization (2) — a plain join, no cross-module query.
- **Role / OrganizationMember / OrganizationMemberRole** — roles hold bitwise `Permission` flags and may be custom. A membership can have multiple roles through the composite junction; effective permissions are the bitwise OR of every assigned role. Owner protection lives on the membership. The system `Administrator` role stores `int.MaxValue` so future permission flags are inherited automatically.
- **OrganizationInvitation** — normalized email + role + expiry + single-use token hash; plaintext token exists only in the invitation event/email link.
- **RefreshToken** — hashed long-lived session token scoped to one active organization, so refresh cannot silently jump tenants.
- **OAuthConnection** — per-provider external identity link.
- **PasswordCredential** (1:1 User) — independently salted server Argon2id verifier of the request-only client `AuthCredential`, plus the public client KDF salt. Password users only; version/downgrade state remains account-wide on `User` so OAuth setup is covered too.
- **TotpCredential** (1:1 User) + **TotpRecoveryCode** — pending/active TOTP secret, replay high-water mark, and one-time recovery-code hashes.
- **VerificationToken** — single-use, hashed, TTL-bound token; `Purpose` (`EmailVerify` / `EmailChange` / `LoginTotpChallenge`) is always part of the lookup so a token can't be redeemed cross-purpose.
- **LoginLockout** — sliding-window failed-login counter per (email, ip).

## Contracts (namespaces / types)
- Public enums consumed cross-module: `Palladin.Module.Identity.Domain.Enums` (`PlanType`, `AuthProvider`) — Audit reads these.
- Persistence contexts: `IdentityDbWriteContext`, `IdentityDbReadContext`, `IdentityDomainWriteContext`, `IdentityDomainReadContext`.

## Critical points / invariants
- Refresh tokens are long-lived (365-day) and rotate only when ≤ 7 days remain — do not shorten without coordinating clients.
- New JWT claims must go through `JwtClaimNames` + a `ClaimsPrincipalExtensions` reader (the existing `is_onboarded` claim predates this and is the exception, not the pattern). See `../building-blocks.md`. The `email_verified` claim follows the pattern (`JwtClaimNames.EmailVerified` + `GetEmailVerified()`).
- **Hard email-verification gate:** `this.RequireEmailVerified()` (Core.Security) is applied per-endpoint to every user-facing (JWT) sensitive endpoint in Vault and Agents (vault/entry/grant CRUD + reveal + lists, agents, API keys) — an unverified password user gets **403 with error key `errors.backend.email-not-verified`** (distinct from a plain permission 403, so the client can target only this case and redirect to `/verify-email`). It is NOT applied to Identity auth/account endpoints (login/register/verify/resend/logout/GET account/TOTP/recovery) or to agent-authenticated endpoints (agents have no email verification). OAuth users carry `email_verified=true`, so they pass. The pre-processor honours the runtime switch `IEmailVerificationGate` (backed by `Modules:Identity:EmailVerification:GateEnabled`, default true) — set it false to disable the gate via config without a redeploy.
- **Member key version:** Identity owns a persisted, monotonically increasing version for the user's X25519 public key. Registration/account setup establish version `1`; every actual public-key rotation increments it and publishes the authenticated directory event. Password/account recovery only re-wraps the same private key and therefore must not change the public key or its version. At the pre-production protocol cutover, existing key material is deliberately cleared and affected accounts repeat setup; no version backfill or compatibility mode may leave the separate Vault key directory empty.
- **KDF downgrade fence:** the version/profile pair is registry-pinned, `MinimumSecurityVersion` never decreases, revision columns are concurrency tokens, and validators plus domain methods reject any profile other than password-only v1.
- **KDF-bound account identity:** version 1 registration requires a client-generated random UUIDv4 `AccountId`, because that immutable public identifier is the frozen HKDF extract salt. Identity uses it as the aggregate ID in the same transaction as the organization, verifier and wrapped key material; an empty, wrong-version or colliding ID creates no partial account state.
- KDF binary fields use canonical unpadded base64url. Identity security writes are rate-limited per authenticated user (IP fallback before identity is available); login retains its per-email/IP lockout.
- Password/AuthCredential/AccountRoot/MK/TOTP-secret/recovery-codes/verification-token must never appear in logs, analytics, or Serilog. AuthCredential verification and the stored-hash comparison are constant-time (`CryptographicOperations.FixedTimeEquals`).
- Existing accounts are all OAuth, so the `AddEmailPasswordAuth` migration backfills `EmailVerified = true`; the OAuth sign-in path sets it true going forward.
- A user can belong to multiple organizations, but each JWT and refresh-token lineage carries exactly one active `org_id`. JWT authentication validates that `(sub, org_id)` still exists in `OrganizationMember` on every request; membership removal therefore rejects existing access tokens immediately and also revokes refresh tokens for that organization.
