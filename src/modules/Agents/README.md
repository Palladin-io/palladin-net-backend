# Module: Agents

## Projects
- `Palladin.Module.Agents/` — domain, features, persistence, authentication and triggers.
- `Palladin.Module.Agents.Contracts/` — integration events owned by Agents.

## Business responsibility
Owns the agent (AI client) lifecycle — enrollment, approval, deactivation — and API-key management. Provides the authentication path agents use to reach the API: API-key lookup plus Ed25519 request-signature verification.

## Dependencies / references
- **ProjectReferences:** `Palladin.Module.Identity.Contracts`, `Palladin.Module.Vault.Contracts`, `Palladin.Module.Notification.Contracts`, `Palladin.Module.Search.Contracts`, `Palladin.Module.Audit.Contracts`, plus its own `Palladin.Module.Agents.Contracts`.
- **Publishes** (`Palladin.Module.Agents.Contracts.Events`): `AgentUpsertedEvent`, `AgentDeactivatedEvent`, `AgentReactivatedEvent`, `AgentDeletedEvent`, `ApiKeyCreatedEvent`, `ApiKeyActivatedEvent`, `ApiKeyRevokedEvent`, `ApiKeyDeletedEvent`.
- **Consumes:** `UserUpsertedEvent` from Identity and `AgentDeactivationCompletedEvent` from Vault.
- **Sends command** (`Palladin.Module.Notification.Contracts`): `BroadcastNotificationCommand` (fired when a newly enrolled agent is `Pending`, awaiting approval).
- **Sends commands** to OpenHost modules from its own `On{Event}Audit` / `On{Event}Search` / onboarding triggers: `AppendAuditLogCommand` (agent/apikey audit), `IndexSearchItemCommand`/`RemoveSearchItemCommand` (agent search), `MarkOrganizationOnboardingStepCommand` (org api-key / agent-enrolled onboarding).

## Technologies used
EF Core + Postgres, MassTransit, custom ASP.NET Core auth scheme (`AgentAuthenticationHandler`), Ed25519 signature verification (`AgentSignatureVerifier`), `ICdnService` (agent icons).
Form Discovery Maps are public, value-free metadata for browser providers. Agents may submit bounded
organization `candidate` maps; publication as `verified` is never agent-controlled. A private
deployment catalog may additionally seed `system` maps directly into the public schema. The backend
repository deliberately contains no provider catalog or production seed data.

## Key aggregates (name + role)
- **Agent** — an AI client; `AgentStatus` Pending→Active→Deactivating→Deactivated; carries versioned X25519 recipient identity plus an Ed25519 request-signing public key. Active deactivation is identified by a durable request id. Emits a single `AgentUpsertedEvent` per unit of work via `AddOrReplaceEvent`.
- **ApiKey** — credential for agent auth; stored as a hash (`pl_` prefix) plus a 4-char suffix for display.
- **FormDiscoveryMap** — versioned login structure with no credentials, cookie values or executable
  code. Cookie/CMP data is limited to bounded same-origin selector clicks. Organization candidate
  versions are serialized per Organization, domain and provider. System revisions have no
  `OrganizationId` or submitting Agent, are accepted only as `verified` seed records, and use a
  separate unique domain/provider/version key.
- **User** — read-model replica synced from Identity.

## Contracts (namespaces / types)
- Agent identity is carried in the `agent_id` claim (`AgentClaimNames`, internal).
- Persistence contexts: `AgentsDbWriteContext`, `AgentsDbReadContext`, `AgentsDomainWriteContext`, `AgentsDomainReadContext`.

## Critical points / invariants
- Never return a full API key — only the masked `pl_••••{suffix}` form. The raw key is never persisted, only its hash.
- `X-Agent-Key` is one raw 32-byte X25519 public key encoded as Base64. Enrollment rejects every other decoded length so an authenticated Agent can always participate in the frozen Discovery protocol; there is no legacy-length compatibility path.
- `RecipientKeyVersion` is monotonic Agent identity metadata and is included with both public keys in `AgentUpsertedEvent`; Vault uses its replica to reject discovery envelopes sealed to a stale or substituted Agent identity.
- Agent summaries expose the non-secret `RecipientKeyVersion`; only the single-Agent detail additionally exposes the full X25519 public key. An unlocked client binds both values into a grant envelope and Vault validates them again against its current replica.
- Deactivation is staged: Agents first publishes `AgentDeactivationRequestedEvent`; Vault rotates the VDK and removes grant/discovery envelopes in each affected Vault, then publishes `AgentDeactivationCompletedEvent`. A completion matching the active `DeactivationRequestId` moves the Agent to `Deactivated`; timestamp ordering silently rejects a stale completion for any previous request, so delayed duplicates cannot override reactivation. Request identity remains authoritative despite transport/database timestamp precision differences. A `Deactivating` Agent cannot receive new grants or Discovery provisioning, while its existing access remains usable for Vaults whose rotation has not committed. The legacy `AgentDeactivatedEvent` cleanup remains bounded and resumable for compatibility. `AgentDeletedEvent` hard-removes the tombstone and Agent replica.
- Form-map lookup requires an explicit provider and returns the highest verified Organization revision
  before considering a verified System revision. Candidate/observed Organization maps never shadow a
  System map. Every returned revision is reparsed and must pass the full safety/fingerprint contract;
  an invalid newer row is skipped in favor of an older safe revision. Domain and login host must match
  exactly after normalization and use HTTPS. Login URL query values are rejected; only the empty
  legacy eBay `?SignIn` flag is accepted. Maps may reference only `credential.username` with a
  username-compatible control and `credential.password` with `password`.
- System catalog SQL is an operational deployment artifact owned by the private Palladin repository.
  It is idempotent, retains older revisions, rejects same-version fingerprint conflicts, and never
  updates or deletes Organization-scoped rows.
