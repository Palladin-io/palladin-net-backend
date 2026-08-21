# Shared Building Blocks (`src/core`)

Catalog of reusable cross-cutting pieces. **Reuse these — never re-implement them inside a module.** Paths are relative to `src/core/`.

## `Palladin.Core.Events`

| Building block | Type | File | Purpose | How a feature consumes it |
|---|---|---|---|---|
| `IEvent` | interface | `Palladin.Core.Events/Markers.cs` | Base marker for all domain and integration events | Domain event records implement it (directly or via `IIntegrationEvent`) |
| `IIntegrationEvent` | interface | `Palladin.Core.Events/Markers.cs` | Marks an event for cross-module transport via MassTransit | Any event record that crosses a module boundary implements it |
| `IEventEntity` | interface | `Palladin.Core.Events/Entity.cs` | Contract for entities that accumulate domain events; `PeekEvents` snapshots without draining and `FetchEvents` drains only after a successful save | Implemented via `EventEntityBase` |
| `EventEntityBase` | abstract class | `Palladin.Core.Events/Entity.cs` | `AddEvent` / `AddOrReplaceEvent` plus non-destructive `PeekEvents` and draining `FetchEvents` accumulation | Aggregate roots inherit it and call `AddEvent`/`AddOrReplaceEvent` from domain methods (emit stays private) |
| `IEventPublisher` | interface | `Palladin.Core.Events/Publisher.cs` | Single-method contract to publish a domain event to any transport | `DomainWriteContextBase` uses it on commit; non-domain callsites inject `IEnumerable<IEventPublisher>` |

**`AddEvent` vs `AddOrReplaceEvent`:** use `AddEvent` for each distinct occurrence; use `AddOrReplaceEvent` when the entity must emit at most one event of a given type per unit of work (e.g. `Agent` emitting a single `AgentUpsertedEvent` after several mutations in the same flow).

## `Palladin.Core.Persistence`

| Building block | File | Purpose | How a feature consumes it |
|---|---|---|---|
| `DomainReadContextBase` | `Palladin.Core.Persistence/DomainReadContextBase.cs` | Exposes no-tracking query roots and parameterized scalar/projection SQL backed only by the module read context | Each module's `XxxDomainReadContext` extends it; read features query entity properties, or use `SqlQuery<TResult>` when provider-side value-object conversion cannot express required ordering/range semantics without materializing sensitive columns |
| `DomainWriteContextBase` | `Palladin.Core.Persistence/DomainWriteContextBase.cs` | Exposes tracked aggregate roots, calls the default no-op `PrepareEventsAsync` hook exactly once before each save, drains events only after a successful save, and then dispatches them through `HandleEventAsync`; `FlushAsync` retains page events across `Clear()` until the final transaction commit | Each module's `XxxDomainWriteContext` extends it; ordinary mutations call `CommitAsync`, while atomic paged writes use `FlushAsync` + `Clear()` and finish with `CommitAsync(transaction)`. A module may override `PrepareEventsAsync` to stage a typed current-state dispatch row in the same database transaction as its aggregate mutation; a bounded Hangfire publisher then delivers that state at least once and advances its published revision only after broker success |
| `ReadOnlyContextSaveChangesException` | `Palladin.Core.Persistence/ReadOnlyContextSaveChangesException.cs` | Thrown when `SaveChangesAsync` is called on a read context | `*DbReadContext` overrides `SaveChangesAsync` to throw it — keeps read contexts query-only |
| `PostgresErrorCodes` | `Palladin.Core.Persistence/PostgresErrorCodes.cs` | Constant `UniqueViolation = "23505"` | Reference the constant; never hardcode the `"23505"` string |
| `IPersistenceOptions` | `Palladin.Core.Persistence/IPersistenceOptions.cs` | Shared options contract for per-module connection strings | Each `*PersistenceOptions` implements it |

## `Palladin.Core.Api`

| Building block | File | Purpose | How a feature consumes it |
|---|---|---|---|
| `InstantCursor` | `Palladin.Core.Api/InstantCursor.cs` | Base64 encode/decode of a `(Instant, Guid)` keyset cursor | Every list endpoint encodes/decodes its page token through it — never roll your own cursor |
| `IApplicationStartingHook` | `Palladin.Core.Api/Hooks.cs` | Lifecycle hook run before the app serves traffic | DB-creation / migration hooks implement it |
| `ErrorResponses` | `Palladin.Core.Api/ErrorResponses.cs` | `General(key)` formats `"errors.backend.{key}"` error keys | Use for all error-key responses so keys stay consistent across endpoints |
| `BasicAuthValidator` | `Palladin.Core.Api/BasicAuthValidator.cs` | HTTP Basic Auth header parsing for internal dashboards | Used by the Hangfire dashboard auth filter |

## `Palladin.Core.Security`

| Building block | File | Purpose | How a feature consumes it |
|---|---|---|---|
| `JwtClaimNames` | `Palladin.Core.Security/JwtClaimNames.cs` | Claim-name constants (`org_id`, `permissions`, `authz_ver`, `plan`, `display_name`) | Token issuing writes via these constants; readers read via these constants — never hardcode claim names |
| `ClaimsPrincipalExtensions` | `Palladin.Core.Security/ClaimsPrincipalExtensions.cs` | `GetUserId()`, `GetOrganizationId()`, `GetDisplayName()`, `GetPermissions()`, `GetAuthorizationVersion()` | Endpoints read identity through these helpers — never read raw `User.Claims` |
| `Permission` | `Palladin.Core.Security/Permission.cs` | `[Flags]` permission bitmask | Endpoints gate via `this.RequirePermission(Permission.X)` in `Configure()` |
| `RequirePermissionPreProcessor<TRequest>` | `Palladin.Core.Security/RequirePermissionPreProcessor.cs` | FastEndpoints pre-processor checking the `permissions` claim against a required bitmask | Wired by `this.RequirePermission(...)`; do not add raw pre-processors |
| `AllowNonActiveOrganizationMembershipMetadata` | `Palladin.Core.Security/AllowNonActiveOrganizationMembershipMetadata.cs` | Marks one reviewed exception to the API's default active-membership boundary | Add `Options(builder => builder.AllowNonActiveOrganizationMembership())` directly in the endpoint's `Configure()` only for an audited account/exit operation or semantically read-only POST; every use is protected by an exact architecture-test allowlist |
| `IServerKeyDeriver` | `Palladin.Core.Security/IServerKeyDeriver.cs` | Domain-separates server-only HMAC subkeys without exposing another module's configuration layout | Infrastructure services request a purpose-specific in-memory key; they never read another module's secret path or persist the derived key |

The API attaches `RequireActiveOrganizationMembershipPreProcessor` (`src/Palladin.Api/Framework/`) to every endpoint by default. Safe HTTP methods remain readable during staged offboarding. Every unsafe method carrying the complete user-JWT claim set (`sub`, `org_id`, `authz_ver`) requires the current membership to be `Active`; anonymous, agent and system principals are outside this user-JWT boundary. Exceptions must call `Options(builder => builder.AllowNonActiveOrganizationMembership())` directly in the endpoint's `Configure()`—never through a class attribute, path string or constructor/DI reflection—and must be added to the exact allowlist in `ActiveOrganizationMembershipBoundaryArchitectureTests` after review. The current allowlist is limited to Identity account/exit flows, Notification read/preferences/push registration, and the read-only Vault member snapshot/delta and Search GlobalSearch POSTs.

## `Palladin.Core.Types`

Shared enums and exceptions referenced across module boundaries (see `CLAUDE.MD` → "Shared Types").

| Building block | File | Notes |
|---|---|---|
| `EntryType` | `Palladin.Core.Types/EntryType.cs` | `Key`, `Credential`, `Script`, `CreditCard` |
| `GrantDeliveryPolicy` | `Palladin.Core.Types/GrantDeliveryPolicy.cs` | `Standard`, `ExecOnly`, `InjectOnly`; authenticated structural delivery class, never inferred from plaintext Entry metadata |
| `GrantStatus` | `Palladin.Core.Types/GrantStatus.cs` | `Pending/Active/Expired/Revoked/Consumed/Denied` |
| `GrantType` | `Palladin.Core.Types/GrantType.cs` | `Granular`, `Full` |
| `GrantMethods` | `Palladin.Core.Types/GrantMethods.cs` | `[Flags]` `Get/Exec/Inject` + `IsValidSet()` / `IsSingleMethod()` |
| `AgentStatus` | `Palladin.Core.Types/AgentStatus.cs` | `Pending/Active/Deactivated` |
| `NotificationType` / `NotificationCategory` / `PushPlatform` | `Palladin.Core.Types/*.cs` | Notification discriminators |
| `DomainException` | `Palladin.Core.Types/Exceptions/DomainException.cs` | Invariant violation → 400/422 |
| `EntityNotFoundException` | `Palladin.Core.Types/Exceptions/EntityNotFoundException.cs` | Lookup failure → 404 |
| `ConflictException` | `Palladin.Core.Types/Exceptions/ConflictException.cs` | Abstract base for incompatible state-transition → 409; subclass it instead of throwing raw exceptions |
| `CdnNotConfiguredException` | `Palladin.Core.Types/Exceptions/CdnNotConfiguredException.cs` | Thrown by the unconfigured-CDN fallback |

## `Palladin.Core.Analytics`

| Building block | File | Purpose | How a feature consumes it |
|---|---|---|---|
| `IAnalyticsService` | `Palladin.Core.Analytics/IAnalyticsService.cs` | `CaptureEvent(distinctId, module, eventName, props)` → fires `be:{module}:{eventName}`; `IdentifyUser` | Called from triggers (analytics consumers), never inline in endpoints — see `CLAUDE.MD` analytics rule |
| `PostHogAnalyticsService` | `Palladin.Core.Analytics/PostHogAnalyticsService.cs` | Concrete impl; prepends `be:`, enriches with `ITransportContext` (session/correlation id, feature flags) | Registered in IoC; do not new it up |

## `Palladin.Core.MassTransit`

| Building block | File | Purpose |
|---|---|---|
| `IntegrationEventPublisher` | `Palladin.Core.MassTransit/Events/IntegrationEventPublisher.cs` | `IEventPublisher` impl: filters for `IIntegrationEvent` and publishes via `IPublishEndpoint` |
| `KebabCaseWithNamespacesEndpointNameFormatter` | `Palladin.Core.MassTransit/KebabNamespaceEndpointFormatter.cs` | Derives kebab-case RabbitMQ queue names from consumer type namespace + class name |
| `MassTransitModule` | `Palladin.Core.MassTransit/MassTransitModule.cs` | Registers MassTransit + RabbitMQ; must run **after** all `AddXxxModule()` calls (see `CLAUDE.MD`) |
| `BuildingBlocks/*` | `Palladin.Core.MassTransit/BuildingBlocks/` | Outbox, retries, circuit breaker, kill switch, rate limiter, host options, JSON options, explicit consumer scanning |

## `Palladin.Core.Transport`

| Building block | File | Purpose |
|---|---|---|
| `CustomHeaders` | `Palladin.Core.Transport/CustomHeaders.cs` | Header-name constants: `x-session-id`, `x-correlation-id`, `x-user-agent`, `x-app-version`, `x-ff-*` prefix |
| `ITransportContext` | `Palladin.Core.Transport/ITransportContext.cs` | Carries session id, correlation id, headers across request and message context |

## `Palladin.Core.Guid`

`IGuidProvider` / `GuidProvider` (`Palladin.Core.Guid/GuidProvider.cs`) — generates time-ordered UUIDv7 via `Guid.CreateVersion7(...)`. **Inject `IGuidProvider` instead of calling `Guid.NewGuid()`** so tests stay deterministic (the test fixture substitutes it).

## `Palladin.Core.Hangfire`

| Building block | File | Purpose |
|---|---|---|
| `ICronJob` / `ICronJobOptions` | `Palladin.Core.Hangfire/CronJobs/ICronJob.cs` | Scheduled-job contract: `Name`, `Expression`, `Enabled`, `ExecuteAsync` |
| `HangfireModule` | `Palladin.Core.Hangfire/HangfireModule.cs` | `AddHangfireModule` + `AddScopedCronJob<T,TOptions>` + `UseHangfireModule`; Postgres-backed + dashboard |
| `HangfireCronJobsInitializer` | `Palladin.Core.Hangfire/CronJobs/HangfireCronJobsInitializer.cs` | Hosted service registering all `ICronJob` impls into Hangfire's recurring registry at startup |

## `Palladin.Core.Cdn` (legacy; removal in progress)

| Building block | File | Purpose |
|---|---|---|
| `ICdnService` | `Palladin.Core.Cdn/ICdnService.cs` | S3-compatible CDN: presigned upload/download, public URL, object existence check (`ExistsAsync`), stream, copy (server-side upload with optional `contentType`/`cacheControl`), delete folder |
| `IconUploadHelpers` | `Palladin.Core.Cdn/IconUploadHelpers.cs` | Shared icon presign-URL generation — use across modules; do not write per-module presign logic |
| `UnconfiguredCdnService` | `Palladin.Core.Cdn/UnconfiguredCdnService.cs` | Null-object fallback when S3 config is absent (throws `CdnNotConfiguredException`) |

## Small core projects

| Project | Key type | Purpose |
|---|---|---|
| `Palladin.Core.Normalization` | `Normalization.Do(string)` | `Trim().ToLower()` normalization for lookups |
| `Palladin.Core.Json` | `PalladinJsonSerializationSettings`, `Base64UrlByteArrayJsonConverter` | Shared JSON options plus strict canonical unpadded-base64url serialization for binary protocol fields |
| `Palladin.Core.Options` | `GeneralOptions`, `NetworkingOptions` | Shared options records |
| `Palladin.Core.Bcp47` + `Palladin.Core.Ietf` | Language-tag parsing | Backs `PreferredLanguage` |
| `Palladin.Core.Http` | `RetryPolicies` | Polly policies for outbound HTTP |
| `Palladin.Core.NodaTime` | `PeriodExtensions` | NodaTime Period helpers |
| `Palladin.Core.Ai` | `IAiAdapter` | Semantic Kernel adapter, Langfuse integration |

---

## Shared abstractions to add (missing)

Patterns reimplemented in 2+ modules that should be promoted to `src/core`. Counts are current as of the 2026-06-28 audit.

| # | Abstraction | Where it goes | Duplication today |
|---|---|---|---|
| 1 | **Page-size clamp helper** — `ClampPageSize(int? requested, int @default, int max)` | `Palladin.Core.Api/Pagination.cs` | The `Default`/`Max` + `Math.Min(...)` triple is copy-pasted into **10 list endpoints** (`ListEntries`, `ListGrants`, `ListOrganizationGrants`, `ListPendingGrants`, `ListApiKeyAgents`, `SearchEntries`, `ListAuditLogs`, `ListVaultAuditLogs`, `ListNotifications`, `SearchOrganizationEntries`). Naming also drifts between `PageSize` and `Limit` — standardize on `PageSize`. |
| 2 | **`IEnumerable<IEventPublisher>.PublishAsync<T>` extension** | `Palladin.Core.Events/Publisher.cs` | The `foreach (var publisher in eventPublishers) await publisher.PublishAsync(...)` loop is repeated in **9 non-domain callsites** (`ListAuditLogs:82`, `ListVaultAuditLogs:100`, `SearchEntries:137`, `DeliverCredential`, `GetOrRequestCredential`, `RegisterPushToken:91`, `RemovePushToken:63`, `WebNotifier:34`, `FirebasePushNotificationService:53`). An extension collapses each to one line. |
| 3 | **`NotificationTitleKeys` constants** | `Palladin.Module.Notification.Contracts` (already the shared contract project) | i18n title keys (`"notification.grant_requested.title"`, etc.) are raw string literals across **5 trigger files** in Vault and Agents. |
| 4 | **`UserReplica` base record** — `(Id, OrganizationId, DisplayName)` + shared upsert | `Palladin.Core.Types` (or a new `Palladin.Core.Replicas`) | Vault, Agents, and Notification each define their own `User` read-model and `OnUserUpserted` upsert; Vault and Agents replicas are byte-for-byte identical. Lower priority — module isolation is a deliberate goal. |
| 5 | **`AuditEventType` enum** with a string converter | `Palladin.Core.Types` | `AuditEventType` is currently a string-constants class inside Audit — no compile-time exhaustiveness, no safe pattern matching. Promote when another surface needs type-safe event-type filtering. |

## Dead code to remove or justify

Present in `src/core` but referenced by no module. Remove, or add an explicit reason to keep.

| Item | File |
|---|---|
| `IPaginationOverTokenRequest<TToken>` | `Palladin.Core.Api/Pagination.cs` — never implemented |
| `PaginatedOverTokenList<T,TToken>` | `Palladin.Core.Api/Pagination.cs` — never instantiated |
| `SortingDirection` enum | `Palladin.Core.Api/SortingDirection.cs` — never referenced |
| `IModule` interface | `Palladin.Core.Modules` — no module implements it; all use static `AddXxxModule` extensions |
| `Palladin.Core.AbTesting` (`IActionSelector` / `RoundRobinSelector`) | not referenced by any module |
