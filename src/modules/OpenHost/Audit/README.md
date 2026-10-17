# Module: Audit

## Projects
- `Palladin.Module.Audit/` — append-only audit implementation, queries and export jobs.
- `Palladin.Module.Audit.Contracts/` — commands, events and stable audit value objects shared with producers.

## Business responsibility
Owns the append-only audit trail of business actions across every module, plus async CSV export of that trail. It is an **OpenHost** module: it exposes a command (`AppendAuditLogCommand`) that the owning modules publish; it consumes ONLY that command and writes immutable, opaque rows. It never subscribes to another module's events and never mutates another module's state. It mirrors the repository-local Search and Notification OpenHost implementations.

## OpenHost feeding — command-driven, never event-subscription
Audit does NOT listen to `Vault*`/`Agent*`/`Identity*` events. Each owning module, in its OWN `On{Event}Audit` trigger, constructs the row from tenant/subject IDs, a result, permitted actor attribution, and allow-listed structural metadata. Vault and Entry display data and agent-provided reasons are forbidden. Audit's single consumer writes the row and resolves nothing.

| Owning module | Triggers (per-event folders) | EventTypes |
|---|---|---|
| Vault | `On{Vault,Entry}Upserted{,Audit}`, `On{Vault,Entry}Deleted…Audit`, `OnVaultExportedAudit`, `OnGrant{Created,Requested,Approved,Denied,Revoked,Consumed,Expired}Audit`, `OnCredentialAccessed{,Denied}Audit` | `vault.*`, `entry.*`, `grant.*`, `credential.*` |
| Agents | `OnAgentUpsertedAudit` (pending→`agent.enrolled`), `OnAgent{Deactivated,Reactivated,Deleted}Audit`, `OnApiKey{Created,Activated,Deleted,Revoked}Audit` | `agent.*`, `apikey.*` |
| Identity | `OnLoginAttemptFailedAudit`, `OnOrganization{Created,Updated}Audit`, `OnUserSignedUpAudit`, `OnAccount{SetupCompleted,RecoveryCompleted}Audit` | `auth.login-failed`, `org.*`, `user.*`, `account.*` |
| Vault sharing | `OnEntryShareActivityAudit` | `entry-share.created`, `.delivered`, `.confirmed`, `.protection-changed`, `.expired`, `.revoked`, `.ended`, `.source-access-removed` |

Vault's entry / credential-denied events carry no `OrganizationId`; that owning trigger resolves it from the Vault read-model before publishing.

## Dependencies / references
- **ProjectReferences:** `Palladin.Module.Audit.Contracts` (own contract), `Palladin.Module.Identity.Contracts` (for `PlanType`) and `Palladin.Module.Vault` (for `IVaultDirectory` used by the vault-scoped audit query). Dropped the Agents and Identity runtime references.
- **Contract project `Palladin.Module.Audit.Contracts`:** command `AppendAuditLogCommand`, events, and the `AuditActorType`, `AuditEventType`, `AuditResult` value objects. Owning modules reference this to publish.
- **Publishes** (`Palladin.Module.Audit.Contracts.Events`): `AuditLogExportRequestedEvent`, `AuditLogsQueriedEvent`, `VaultAuditLogsQueriedEvent` (own events for export + analytics).
- **Consumes:** `AppendAuditLogCommand` (its own) — nothing else inbound from other modules.

## Technologies used
EF Core + Postgres, MassTransit, **Hangfire** (export job), **S3 via `ICdnService`** (export file storage). Append-only is enforced by the domain/application write boundary; migrations contain no triggers, stored functions or business-value validation.

## Key aggregates (name + role)
- **AuditLogEntry** — one immutable trail row containing opaque subject IDs, event/result/timestamps, permitted actor attribution, and structural metadata.
- **AuditExportJob** — async export state Pending→Processing→Completed/Failed.

## Contracts (namespaces / types)
- Persistence contexts: `AuditDbWriteContext`, `AuditDbReadContext`, `AuditDomainWriteContext`, `AuditDomainReadContext`.
- Command consumer: `Features/AppendAuditLog` (`ConsumerDefinition.EndpointName = AuditEndpoints.Append` = `audit.commands.append`).

## Critical points / invariants
- Rows are append-only through the domain/application model — never expose an update or delete path for `AuditLogEntry`. PostgreSQL does not implement this rule with executable database behavior.
- The single `AppendAuditLogConsumer` writes rows. A producer with a stable occurrence ID supplies it as `IdempotencyKey`, which becomes the row ID; other producers retain the natural key `(OrganizationId, EventType, VaultId, AgentId, EntryId, OccurredAt)`. Never insert rows elsewhere and never add an `IConsumer<ForeignEvent>` here — publish `AppendAuditLogCommand` from the owning module instead.
- Canonical Vault and Entry names are never stored or exported; clients resolve opaque IDs locally. Agent request reasons are likewise excluded. `AgentName` and `ActorName` remain solely for forensic attribution.
- CSV export uses `AsNoTracking` projection and keyset pages of 500 rows; only the current page is materialized. The tracked export job is independent of row paging, so EF tracking cannot retain the full result set.
- The canonical opaque cutover truncates legacy audit rows and export-job references, then the startup cutover purges prior external CSV objects before marking completion. This intentionally destructive migration has no downgrade path. Normal retention operates only on post-cutover opaque records and exports.
- One polymorphic command (`EventType` + `Metadata`) covers every event type — Open-Closed. Metadata is restricted to non-sensitive structural facts.

## Explicit occurrence identity and guest sharing

`HasExplicitOccurrenceId` distinguishes producer-supplied occurrence IDs from the
legacy natural-key path. Explicit IDs use the existing row PK for uniqueness;
the timestamp-based unique index applies only to rows without an explicit ID.
The incremental `AddExplicitAuditOccurrenceIdentity` migration keeps historical
rows unchanged and adds the discriminator plus filtered index. A PostgreSQL test
reproduced the former unique violation for two legitimate receipts at the same
timestamp before the repair. Independent same-time occurrences now survive;
retries deduplicate, and natural-key producers retain their existing semantics.
Append uses one domain write context for idempotency lookup and insertion.

Vault derives a stable UUIDv8 occurrence from the domain-separated SHA-256 digest
of the share ID and monotonic sequence, not from time or a fresh random ID.
Sharing metadata contains only `shareId` and `sequence`; subject scope occupies
the existing opaque columns. Delivery, confirmation and recipient termination
use the explicit `ExternalRecipient` actor without a User ID, name, address or IP.
Sender operations use the sender's User ID; expiry and source access removal use
System. Client display/export support for the new event and actor values is a
coordinated release gate, not yet accepted by backend tests.
