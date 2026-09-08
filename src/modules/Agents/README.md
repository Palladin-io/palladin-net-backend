# Module: Agents

## Projects
- `Palladin.Module.Agents/` — domain, features, persistence, authentication and triggers.
- `Palladin.Module.Agents.Contracts/` — integration events owned by Agents.

## Business responsibility
Owns the agent (AI client) lifecycle — enrollment, approval, deactivation — and API-key management. Provides the authentication path agents use to reach the API: API-key lookup plus Ed25519 request-signature verification.

## Dependencies / references
- **ProjectReferences:** `Palladin.Core.Hangfire`, `Palladin.Module.Identity.Contracts`, `Palladin.Module.Vault.Contracts`, `Palladin.Module.Notification.Contracts`, `Palladin.Module.Search.Contracts`, `Palladin.Module.Audit.Contracts`, plus its own `Palladin.Module.Agents.Contracts`.
- **Publishes** (`Palladin.Module.Agents.Contracts.Events`): `AgentUpsertedEvent`, `AgentBrowserPairingEnrolledEvent`, `AgentDeactivatedEvent`, `AgentReactivatedEvent`, `AgentDeletedEvent`, `ApiKeyCreatedEvent`, `ApiKeyActivatedEvent`, `ApiKeyRevokedEvent`, `ApiKeyDeletedEvent`.
- **Consumes:** `UserUpsertedEvent` from Identity and `AgentDeactivationCompletedEvent` from Vault.
- **Sends command** (`Palladin.Module.Notification.Contracts`): `BroadcastNotificationCommand` (fired when a newly enrolled agent is `Pending`, awaiting approval).
- **Sends commands** to OpenHost modules from its own `On{Event}Audit` / `On{Event}Search` / onboarding triggers: `AppendAuditLogCommand` (agent/apikey audit), `IndexSearchItemCommand`/`RemoveSearchItemCommand` (agent search), `MarkOrganizationOnboardingStepCommand` (org api-key / agent-enrolled onboarding).

## Technologies used
EF Core + Postgres, MassTransit, custom ASP.NET Core auth scheme (`AgentAuthenticationHandler`), Ed25519 signature verification (`AgentSignatureVerifier`), `ICdnService` (agent icons).
Form Discovery Maps are one global, provider-neutral catalog of public, value-free login metadata.
Authenticated Agents may submit bounded global `candidate` revisions with their Agent ID retained as
provenance. A tenant can never publish a catalog revision. Promotion to `verified` belongs only to a
trusted private catalog process, which is intentionally outside this repository and requires a
separate implementation before candidate revisions can be published.

Candidate version allocation is one reviewed explicit-transaction exception. Two Agents may submit
for the same `(OrganizationId, Domain, Provider)` concurrently and both valid submissions must be
stored with distinct monotonic versions. `MAX(MapVersion) + 1` and the insert are separate SQL
statements, so the endpoint holds one transaction-scoped advisory lock for that exact tuple until the
insert commits. The transaction is database-only and contains one logical lock plus one insert; it
does not acquire a second advisory key or perform external I/O, so this flow cannot form an advisory
lock-order cycle. The matching UNIQUE index is the final integrity fence. Removing the boundary
without replacing allocation semantics would either reject a legitimate racing submission or assign
the same version twice; adding a coordinator aggregate only to avoid this short critical section would
increase schema and retry complexity without reducing the invariant.

## Key aggregates (name + role)
- **Agent** — an AI client; `AgentStatus` Pending→Active→Deactivating→Deactivated; carries versioned X25519 recipient identity plus an Ed25519 request-signing public key. A bounded SHA-256 `NameKey`, computed with the same invariant normalization as pairing reservations, coordinates new names independently of database Unicode casing; legacy null-key rows retain exact and prior database-case comparison fallbacks. Active deactivation is identified by a durable request id. Emits a single `AgentUpsertedEvent` per unit of work via `AddOrReplaceEvent`.
- **ApiKey** — credential for agent auth; stored as a hash (`pl_` prefix) plus a 4-char suffix for display.
- **AgentPairingRequest** — a short-lived, signed browser-pairing state machine. It stores only
  canonical public keys and reviewable metadata before approval; its optimistic revision fences
  claim/reserve/approve/reject/expire races. `Pending` requests become `Approved`, `Rejected`, or
  `Expired`. Approved requests temporarily retain only the credential envelope encrypted to the
  runtime's X25519 key.
- **AgentDisplayNameFence** — an organization-scoped optimistic coordinator shared by browser
  reservations and every standard Agent name assignment. It makes the cross-table name decision
  atomic without a wide lock or external transaction.
- **FormDiscoveryMap** — versioned login structure with no credentials, cookie values or executable
  code. Cookie/CMP data is limited to bounded same-origin selector clicks. Versions are serialized
  by one global PostgreSQL sequence and are opaque monotonic tokens whose gaps are valid.
  `SubmittedByAgentId` records candidate provenance but does not scope visibility or confer
  publication authority.
- **User** — read-model replica synced from Identity.

## Contracts (namespaces / types)
- Agent identity is carried in the `agent_id` claim (`AgentClaimNames`, internal).
- `AgentUpsertedEvent.Type` is nullable and preserves the optional free-form Agent type without
  catalog mapping. Consumers must not infer authorization from it.
- Persistence contexts: `AgentsDbWriteContext`, `AgentsDbReadContext`, `AgentsDomainWriteContext`, `AgentsDomainReadContext`.
- Each feature operation uses one domain context, including its helpers: query-only operations use
  `AgentsDomainReadContext`; mutations use `AgentsDomainWriteContext` for reads and writes. Pairing
  start, claim, approval and API-key actor projections do not need a second read context. The Agents
  persistence architecture test checks direct and transitive feature-constructor dependencies.

## Critical points / invariants
- Pairing start accepts an optional bounded runtime-declared hostname in the signed body and stores
  the IP observed on that runtime connection through the existing trusted-proxy pipeline. Approval
  shows this start snapshot and initializes the standard Agent connection fields from it, never from
  the approving browser. Normal authenticated Agent requests continue updating `LastIp` and
  `LastHostname`; these fields describe the latest known connection, not a separate history.
  Replaying a pairing start does not replace its original snapshot. Hostname is informational,
  not a permission or verified machine identity; hostname/IP never enter pairing URLs or analytics.
- Never return a full API key — only the masked `pl_••••{suffix}` form. The raw key is never persisted, only its hash.
- Browser pairing never accepts an API key. `POST /api/agent-pairings` and signed status polling are
  anonymous by design and authenticate the exact HTTP request with the declared Ed25519 identity.
  Only opaque pairing IDs appear in approval URLs. Claim/reserve/approve/reject require the normal
  authenticated organization permissions and verified e-mail boundary.
- Pairing has separate key-selection and key-creation slices. `/claim` and `/approve` require
  `AgentManage + ReadApiKey` and approval accepts only an existing `ApiKeyId`.
  `/claim-for-new-key` and `/approve-with-new-key` require `AgentManage + WriteApiKey`, not
  `ReadApiKey`. The creation claim never queries or returns existing key metadata and approval
  accepts only `NewApiKeyName`. Both use the same organization-bound claim, reservation and atomic
  standard Agent activation mechanics; no new permission flag or grant is introduced.
- Pairing approval creates the same standard `Active` Agent aggregate and `AgentUpsertedEvent` as the
  normal lifecycle. The optional Agent `Type` remains a normalized, bounded free-form string; it is
  presentation metadata and grants no permission. The final display name is user-approved and may
  be edited later through the standard Agent endpoint. Approval accepts an optional user-selected
  `IconKey`, using the standard approval validation and storing it atomically on activation.
  Approval additionally publishes
  `AgentBrowserPairingEnrolledEvent` so the dedicated consumer emits the ordinary `agent.enrolled`
  audit without treating later active-Agent upserts as new enrollment.
- A pairing creates one random per-Agent `ApiKeyCredential` below the selected logical `ApiKey`.
  Only its hash and Agent binding are persisted. The child credential is never listed or audited as
  a separate user object; logical-key revoke/delete invalidates all children, and Agent requests
  attribute usage to the logical key.
- Canonical 32-byte Base64 is required for both X25519 and Ed25519 public keys. Whitespace or another
  textual representation of the same bytes is rejected before identity lookup or persistence.
- Pairing approval lasts exactly 30 minutes; startup rejects shorter or longer configured lifetimes.
  Standard enrollment retries organization name-fence conflicts at most twice, rechecking the
  requested name rather than silently dropping it. Exhausted contention fails without creating an unnamed Agent.
- `agents.cleanup-agent-pairings` runs in bounded batches. It persists expiry (releasing display-name
  reservations) and removes all terminal request material after the configured 15-minute retrieval
  grace. An approved envelope remains retrievable after the original 30-minute approval deadline
  until that terminal retention elapses.
- `X-Agent-Key` is one raw 32-byte X25519 public key encoded as Base64. Enrollment rejects every other decoded length so an authenticated Agent can always participate in the frozen Discovery protocol; there is no legacy-length compatibility path.
- `RecipientKeyVersion` is monotonic Agent identity metadata and is included with both public keys in `AgentUpsertedEvent`; Vault uses its replica to reject discovery envelopes sealed to a stale or substituted Agent identity.
- Agent summaries expose the non-secret `RecipientKeyVersion` and `AccessEpoch`; only the single-Agent detail additionally exposes the full X25519 public key. An unlocked client binds the authoritative recipient key version into GRANULAR grant envelopes, and binds both it and the access epoch into a FULL `AgentWrappedVaultKey`. Vault validates every value again against its current Agent replica.
- Deactivation is staged: Agents first publishes `AgentDeactivationRequestedEvent`; Vault rotates the VDK and removes grant/discovery envelopes in each affected Vault, then publishes `AgentDeactivationCompletedEvent`. A completion matching the active `DeactivationRequestId` moves the Agent to `Deactivated`; timestamp ordering silently rejects a stale completion for any previous request, so delayed duplicates cannot override reactivation. Request identity remains authoritative despite transport/database timestamp precision differences. A `Deactivating` Agent cannot receive new grants or Discovery provisioning, while its existing access remains usable for Vaults whose rotation has not committed. The legacy `AgentDeactivatedEvent` cleanup remains bounded and resumable for compatibility. `AgentDeletedEvent` hard-removes the tombstone and Agent replica.
- Form-map lookup requires an explicit, safely normalized provider identifier and returns the highest
  global `verified` revision for the normalized domain/provider pair. Provider identifiers are open;
  the backend has no provider allowlist. An invalid newer row is skipped in favor of an older safe
  revision within the configured lookup bound.
- Domain and login host must match exactly after IDN normalization. Login URLs require HTTPS on the
  default port and reject userinfo, query and fragment data, but accept any path; the contract is not
  coupled to English paths or a catalog of known sites.
- Definitions use typed, unknown-field-rejecting DTOs. Form field IDs follow the provider-neutral
  Inject schema rather than a language or product-field allowlist. Resource bounds come from
  `Modules:Agents:FormDiscoveryMaps` options.
- The SHA-256 fingerprint serializes a typed payload in this order: normalized `domain`, login URL
  absolute path as `loginUrl`, normalized `provider`, and the complete typed `map` definition,
  including form steps and optional cookie overlays. The same typed serialization stores definition
  JSON; no handwritten canonical JSON writer is used.
- The global-catalog migration is an irreversible pre-production cutover. It resets retired
  organization-scoped rows because their caller-supplied fingerprints were not bound to the typed
  definition. All replicas running the old schema must be stopped before the migration is applied;
  retaining `organization_id` or dual reads/writes for a rolling deployment is not supported.
