# AGENTS.md

This file provides guidance to Codex when working with code in this repository.

## CI/CD

GitHub Actions workflow at `.github/workflows/test.yml` runs on PRs to `main`:
1. `dotnet restore`
2. `dotnet build --no-restore`
3. `dotnet test --no-build` (xUnit + Testcontainers — Docker required)

**All changes must go through PRs** — CI must pass before merging.

Repository: [Palladin-io/palladin-net-backend](https://github.com/Palladin-io/palladin-net-backend)

## Runtime Secrets

- Never commit or log secret values. Keep production secrets in a dedicated secret manager and inject them into ASP.NET configuration at runtime.
- Grant workloads access only to the exact secrets and operations they need.
- Do not pass plaintext secrets through CI inputs, workflow output, command-line arguments, or persistent deployment files.
- Use local-only `appsettings.Local.json` or environment variables for developer overrides; `appsettings.Local.json` is ignored by Git.
- A deployment is healthy only after the API health endpoint succeeds and the application remains running.

## Build, Test, and Run Commands

```bash
dotnet restore Palladin.sln              # Restore dependencies
dotnet build Palladin.sln                # Build (nullable + implicit usings enforced via Directory.Build.props)
dotnet test Palladin.sln                 # Run all xUnit test suites
dotnet run --project src/Palladin.Api/Palladin.Api.csproj  # Run API locally
```

### Running a single test
```bash
dotnet test tests/Palladin.Api.Tests --filter "FullyQualifiedName~ClassName.MethodName"
```

### Database migrations
Scripts live in `scripts/` with convention `module-migration-add.sh`:
```bash
./scripts/identity-migration-add.sh "MigrationName"
```
Each script runs `dotnet ef migrations add` against the module's write DbContext. Re-run the relevant migration script after modifying persistence models.

## Architecture Overview

**Modular Monolith** — single deployable ASP.NET Core app with independent domain modules. Target framework: **net10.0**.

### Pre-production Vault cutover

Palladin has not entered production and has no Vault data that requires compatibility preservation. The accepted zero-knowledge cutover replaces the existing canonical `Vault`, `Entry` and related persistence directly; incompatible local and staging data may be destructively reset.

- `protocolVersion = 2` is only the frozen wire/AAD discriminator, never a domain, endpoint or schema suffix.
- Never introduce `VaultV2`, `VaultV2State`, `EntryV2`, `*V2` tables, duplicate `/v2` endpoints, legacy/new nullable columns, dual reads/writes, backfills or compatibility repositories for this cutover.
- “Vault v2” in historical epic/task/contract/document names means Vault wire protocol `2`, not a second runtime model. Do not infer runtime versioning from those historical names.
- Keep one canonical aggregate, schema and write path. Any later compatibility requirement needs a new explicit architecture decision backed by real production constraints.

### Pre-production migration history

The approved pre-production database reset uses one `Initial` EF Core migration per active module context. After an explicitly approved squash, replace the complete migration history and model snapshot together, then recreate local and staging databases from the new Initial migrations.

- Do not preserve historical cutover migrations or compatibility SQL after an approved squash.
- After the squash, evolve schemas with normal incremental migrations. Do not squash again unless the owner explicitly requests another destructive pre-production reset.
- Initial and incremental migrations retain structural integrity only through primary keys, foreign keys, unique constraints and required columns. They never contain business-value CHECK constraints, triggers, stored functions or stored procedures.

### Project layout
- `src/Palladin.Api/` — Entry point (`Program.cs`), health checks, middleware, shared bootstrap
- `src/core/` — Cross-cutting utilities (Analytics, Ai, MassTransit, Hangfire, Persistence, NodaTime, Security, Events, Cdn, etc.)
- `src/modules/{ModuleName}/` — Domain module group containing the implementation project, contracts project and module README. Active domain groups: Identity, Vault and Agents.
- `src/modules/OpenHost/{ModuleName}/` — Shared Open Host Service group with the same internal layout. Active OpenHost groups: Audit, Notification, Search and PublicAssetCatalog. Billing is planned, not yet present in `src/modules/`.
- `tests/Palladin.Api.Tests/` — Main integration test suite
- `tests/Palladin.Tests.Architecture/` — Module boundary compliance tests

### Module group structure
```
{ModuleRoot}/
├── README.md                                      # Module architecture and ownership map
├── Palladin.Module.{ModuleName}/                  # Implementation assembly
│   ├── Domain/                                    # Models, value objects, events, exceptions
│   ├── Features/                                  # Vertical slices grouped by actor (`User`, `Agentic`, `System`)
│   ├── Infrastructure/                            # Persistence, messaging, external services and options
│   ├── Shared/                                    # Cross-feature DTOs and value objects
│   ├── Triggers/                                  # Integration event consumers
│   └── {ModuleName}Module.cs                      # Module registration
└── Palladin.Module.{ModuleName}.Contracts/        # Commands, Events and stable shared ValueObjects
```

The implementation and contracts projects are sibling directories inside their owning module group. `{ModuleRoot}` is `src/modules/{ModuleName}` for a domain module or `src/modules/OpenHost/{ModuleName}` for an OpenHost module. A contracts project lives at `{ModuleRoot}/Palladin.Module.{ModuleName}.Contracts/Palladin.Module.{ModuleName}.Contracts.csproj`; it must never be nested inside the implementation project's content root or placed as an unowned top-level pseudo-module. This keeps IDE project trees unambiguous while preserving an acyclic dependency graph and explicit bounded-context ownership. Each contract assembly has the following semantic namespaces:

- `Contracts.Commands` — integration commands handled by the owning module; another module may request the owner to perform the operation. Every such command implements `IIntegrationCommand`, never `IIntegrationEvent`.
- `Contracts.Events` — integration events published by the owning module; every type implementing `IIntegrationEvent` belongs here, even when its first consumer is inside the same module. `Domain/Events` is reserved for truly internal domain events that are not integration contracts.
- `Contracts.ValueObjects` — small immutable domain values intentionally shared through commands, events or public module APIs. They must be stable, serialization-safe and free of entities, persistence models, services and infrastructure dependencies.

Use one `Contracts` assembly per module, not separate command/event packages. Direction is always defined relative to the owning module, so do not use ambiguous `Upstream` or `Downstream` folders. Other modules reference the owner's `Contracts` assembly for messages and shared value objects; they must not reference the main module merely to obtain those types. A namespace or shape change in `Contracts` changes the MassTransit message identity/schema and requires explicit versioning or a deliberate pre-production cutover.

**`*Options` placement — co-locate with the concern, never a fixed `Options/` folder.** An `IOptions<T>` config class lives next to whatever it configures. In a module that is usually a subfolder of `Infrastructure/` named after the concern: `Persistence/*PersistenceOptions`, `Crypto/VaultCryptoOptions`, `AgentAuth/AgentAuthenticationOptions`, `Push/FirebaseOptions` (Identity additionally groups a few in `Infrastructure/Options/`). Cross-cutting options are **not in any module at all** — they live in their own `src/core` project beside the service they configure (`PostHogOptions` in `Core.Analytics`, `S3BucketOptions` in `Core.Cdn`, `HangfireOptions` in `Core.Hangfire`, `MassTransitOptions` in `Core.MassTransit`). Bind every one via its `Options.Position` prefix (see Module registration).

### Trigger naming convention
Triggers live at **module root** (e.g., `Palladin.Module.Vault/Triggers/`), never under `Infrastructure/`.
Named as `On{EventName}` — e.g., `OnVaultCreated`, `OnUserRegistered`. Never suffix with `Trigger` or `Handler`.

Classify every MassTransit consumer by the message it handles — never create a generic `Consumers/` directory:
- a consumer of an integration **event** is a trigger and lives in `Triggers/`;
- a consumer of an integration **command** is an asynchronous vertical slice and lives in `Features/`.

When an event has more than one consumer (e.g. notification + analytics), keep the consumers in a per-event folder `Triggers/{EventName}/`; shared trigger helpers go in `Triggers/Shared/`.

**Why & how:** a trigger is just a MassTransit consumer of an integration event. It may subscribe to an event from **its own module** — e.g. `OnVaultCreated` consumes Vault's own `VaultCreatedEvent`, and `OnGrantRequestedBroadcast` consumes Vault's own `GrantRequestedEvent`. A common trigger pattern is to **convert an Event into a Command** to avoid circular module dependencies: instead of Vault calling Notification, `OnGrantRequestedBroadcast` publishes a `BroadcastNotificationCommand` (from `Palladin.Module.Notification.Contracts.Commands`). The publishing module depends on the consumer's `Contracts` assembly — never the reverse.

### Module registration
Each module exposes `AddXxxModule(IServiceCollection, IConfiguration)` returning `IServiceCollection` for chaining. Modules are registered in `Program.cs` with configuration sections from `appsettings.*.json` under `Modules:ModuleName`. Do not re-register framework services — they are already wired in `Program.cs`.

#### Options binding — bind the section PREFIX, not full field paths

Follow the repository-local convention below whenever changing `IOptions<T>`, `*Options`, or persistence configuration.

Every `*Options` class carries a `public const string Position` that is the **section prefix only**; the option's property names resolve the rest of the path. Registration binds the section to the Position and lets `IOptions<T>` map each field — you never reference `Modules:Identity:Jwt:Secret` anywhere in code.

```csharp
// Options class — Position is the prefix; property names complete the path
internal sealed class JwtOptions
{
    public const string Position = "Modules:Identity:Jwt"; // → binds Modules:Identity:Jwt:*
    public string Secret { get; init; } = string.Empty;    // resolves Modules:Identity:Jwt:Secret
    public int RefreshTokenExpiryDays { get; init; } = 365;
}

// Registration (in the module's InfrastructureModule) — bind the prefix, nothing more
services.Configure<JwtOptions>(configuration.GetSection(JwtOptions.Position));
```

Convention: `Position = "Modules:{Module}:{Section}"` (e.g. `Modules:Vault:Persistence`, `Modules:Agents:Signature`, `Modules:Notification:Firebase`). Consume via injected `IOptions<JwtOptions>` — never read `IConfiguration` by string path in a feature.

### Cross-module communication
Modules communicate exclusively through **MassTransit integration events** (RabbitMQ). Domain events stay within module boundaries. Each module's published/consumed event catalog lives in its `{ModuleRoot}/README.md`.

Use one unified `Upserted` event carrying an `EntityChange { Created, Updated }` classifier instead of separate `Created` and `Updated` events. Subscribers branch on the classifier and use `UpdatedAt` for idempotency.

**Strong isolation — no cross-module read query interfaces.** A module must never expose (or consume) a live read-query interface into another module's data. A read concern that spans modules gets **its own module** built as an **OpenHost** (Open Host Service): a denormalized read-model with its own access scoping.

**OpenHost feeding is command-driven, NOT event-subscription.** The host module consumes **only its own integration commands** (`IndexXCommand` / `RemoveXCommand` / `UpdateUserScope`, defined in its `Contracts` assembly). It **never** subscribes to other modules' events. Each **owning module**, in its **own** `On{Event}` trigger, translates its domain event into the host's command and publishes it (dependency direction: owner → `Host.Contracts`; host → nothing). Reference implementation: the **Notification** module (`BroadcastNotificationCommand`, `NotificationScope`, `UpdateUserScope`; Vault/Agents `On{X}Broadcast` triggers publish it) — mirror it. Access via **scopes** (`{Type, ItemId}` + optional `RequiredPermission`), not per-relation tables / ifology. Prefer one **polymorphic item** (Type + Metadata jsonb) over per-type entities (Open-Closed). Store cross-module state (e.g. `is_onboarded`, onboarding steps) as materialized state fed by commands — never compute it cross-module.

See `src/modules/OpenHost/Search/README.md` and `src/modules/OpenHost/Notification/README.md` for complete repository-local examples.

### References between modules
- `Core.*` projects hold shared code (utilities, base contracts, cross-cutting concerns)
- Modules **may** reference other modules via `ProjectReference`, but **must** consume only the public API (`[PublicAPI]`) or contracts (`IIntegrationEvent`, `IIntegrationCommand`)
- Never import `internal` types from another module — every cross-module API must be `public`

### Database strategy
- **PostgreSQL**; the `pg_trgm` extension is enabled (trigram fuzzy search, used by Vault). No other extensions are currently enabled
- **No validation or executable behavior in PostgreSQL.** Business rules and integrity behavior belong in domain methods and application services. Migrations must not add business `CHECK` constraints, triggers, stored functions or stored procedures. The database retains only PK, FK, UNIQUE and NOT NULL structural constraints plus built-in transaction/concurrency mechanisms.
- **Read/Write split DbContexts** per module, named `{Module}DbReadContext` / `{Module}DbWriteContext` (e.g., `VaultDbReadContext` / `VaultDbWriteContext`)
- **DbContexts are Infrastructure-only.** `Features/` never injects, accepts, or accesses either DbContext. Each module exposes two domain persistence gateways: `{Module}DomainReadContext` wraps only `{Module}DbReadContext` and exposes no-tracking queries; `{Module}DomainWriteContext` wraps only `{Module}DbWriteContext` and exposes tracked aggregate queries plus `Add`/`Remove`/`CommitAsync` with domain-event dispatch. Never create a combined `{Module}DomainContext`.
- Separate databases per module in test isolation
- Entity configurations in `Infrastructure/Persistence/Configurations/`

### Key technology choices
- **FastEndpoints** for HTTP REST (not MVC controllers)
- **MassTransit + RabbitMQ** for async messaging
- **Hangfire** for cron jobs and background processing
- **SignalR** for real-time WebSocket communication
- **NodaTime** for all date/time operations (Instant for timestamps, Duration for spans)
- **Serilog** for structured logging
- **FluentValidation** for input validation

## Architecture Reference Docs

Cross-cutting architecture lives in `docs/architecture/`; each module's detailed architecture lives in `{ModuleRoot}/README.md`. Keep the relevant references open while working.

**Reuse rule:** everything cross-cutting (base entity/event types, persistence base classes, cursor pagination, JWT claim access, GUID generation, analytics, CDN, error formatting) lives once in `src/core/` as a `Palladin.Core.*` project. Reuse those building blocks — never re-implement them inside a module. The full catalog is in **`docs/architecture/building-blocks.md`** (it also lists missing abstractions to promote and dead code to remove).

**Before implementing a feature in a module, read its architecture doc first** — aggregates, endpoints, published/consumed integration events, DbContexts, Hangfire jobs, shared contracts, and invariants:

| Module | Doc |
|--------|-----|
| Identity | `src/modules/Identity/README.md` |
| Vault | `src/modules/Vault/README.md` |
| Agents | `src/modules/Agents/README.md` |
| Audit | `src/modules/OpenHost/Audit/README.md` |
| Notification | `src/modules/OpenHost/Notification/README.md` |
| Search | `src/modules/OpenHost/Search/README.md` |
| PublicAssetCatalog | `src/modules/OpenHost/PublicAssetCatalog/README.md` |

Start at `docs/architecture/README.md`.

## Commit Convention

Emoji-enhanced Conventional Commits:
```
✨ feat: add x oauth
♻️ refactor: clean imports
🐛 fix: correct logging
```
Keep subjects under 72 characters. Focus on observable behavior. Don't mix refactors with feature work.

---

# Features Development Guide

Every module uses **Vertical Slice Architecture** — features live in `/Features` directory.

**Persistence boundary:** every endpoint, command consumer, cron job, and helper in `Features/` accesses persistence exclusively through the module's split domain contexts. Inject `{Module}DomainReadContext` for no-tracking reads and `{Module}DomainWriteContext` for tracked aggregate loads and writes; inject both only when the feature genuinely does both. Never inject or pass `*DbReadContext`, `*DbWriteContext`, base `DbContext`, or a combined `*DomainContext` into a feature.

## Synchronous (HTTP REST via FastEndpoints)

### File structure (all in one file, named after the feature)
- Request record (with `[PublicAPI]`)
- Response record (with `[PublicAPI]`)
- Fluent Validation (with `[UsedImplicitly]`) — do not add `WithMessage()` on validation rules
- Endpoint (`internal sealed` with primary constructor, `[PublicAPI]`)

### Endpoint construction
```csharp
public override void Configure()
{
    Post("api/endpoint");
    Tags("Module/Resource");  // required — AutoTagPathSegmentIndex=0 disables auto-tagging
    Summary(summary =>
    {
        summary.Summary = "Short description";
        summary.Description = "More detailed explanation of the endpoint";
    });
}
```

`AutoTagPathSegmentIndex = 0` is set globally in `SwaggerDocument()` to disable automatic Scalar grouping. Every endpoint **must** declare an explicit two-level tag: `Tags("Agents/Agents")`, `Tags("Identity/Account")`, `Tags("Vault/Entries")`. Endpoints without `Tags()` will not appear in any Scalar group.

### Tips
- Use attribute-based model binding (e.g., `[UserIdFromJwt]`)
- Use primary constructors for dependency injection
- Use records for request and response
- Do not create separate DTOs for value objects — use them directly in request/response

### UI responses resolve names server-side
API responses meant for the UI must return human-readable names next to IDs (`agentName`, `entryLabel`, actor/user name…), resolved **server-side** via a join/projection (correlated subquery / `LEFT JOIN`) in the query — never make the client do `id→name` lookups, and never return a bare ID the user cannot read. Resolve from the module's own replicas/tables (e.g. Vault has the `Agent` replica with `Name` and `Entry` with `Label`); when the referenced entity no longer exists, return `null`/`""` (do not throw). These are metadata names, never crypto/secrets. For **audit**, denormalize names **at write time** (the Audit module has no replica of every module, e.g. Identity users), not at read time.

## Asynchronous (MassTransit)

Command consumers live in `Features/`. Event consumers live in `Triggers/` and follow the `On{EventName}` convention. A module must never have a generic `Consumers/` directory.

### File structure
- Integration Command record (with `[PublicAPI]`)
- Command Parameters/DTOs record (with `[PublicAPI]`)
- `internal sealed` Consumer Definition (with `[UsedImplicitly]`)
- `internal sealed` Consumer (with `[PublicAPI]`) with primary constructor

## Cron Jobs (Hangfire)

### File structure
- Job Options record (`internal sealed`)
- Job Implementation (`internal sealed` with `[UsedImplicitly]` and primary constructor), implements `ICronJob`

Register in IoC at `InfrastructureModule` via `HangfireModule.AddScopedCronJob`.

---

# .NET Development Rules

## Shared Building Blocks (always reuse)

Cross-cutting pieces already exist in `src/core/`. Use them — re-implementing any of these is a review-blocking finding. Full catalog: `docs/architecture/building-blocks.md`.

| Need | Use | Never |
|------|-----|-------|
| Unique-violation check | `PostgresErrorCodes.UniqueViolation` | Hardcode `"23505"` |
| JWT claim names | `JwtClaimNames` constants | Hardcode claim-name strings |
| Read identity from a request | `ClaimsPrincipalExtensions` (`GetUserId()`, `GetOrganizationId()`, `GetDisplayName()`, `GetPermissions()`) | Read raw `User.Claims` |
| New ID | inject `IGuidProvider` | Call `Guid.NewGuid()` |
| Error-key response | `ErrorResponses.General(key)` → `errors.backend.{key}` | Build the key string inline |
| Icon presign / S3 | `ICdnService` + `IconUploadHelpers` | Per-module presign logic |
| 409 conflict | subclass `ConflictException` (`Palladin.Core.Types.Exceptions`) | Throw a raw exception |
| Publish events outside a domain commit | the shared `IEnumerable<IEventPublisher>.PublishAsync<T>` extension (see building-blocks "missing" #2) | Copy the `foreach (publisher ...)` loop |
| Emit a once-per-unit-of-work entity event | `EventEntityBase.AddOrReplaceEvent` | `AddEvent` (use `AddEvent` only for genuinely distinct occurrences) |

## Code Style
- Latest C# features: primary constructors, records, pattern matching, file-scoped namespaces, global usings
- `internal sealed` for module-internal classes
- `var` when type is obvious
- LINQ and lambda for collections
- No comments — self-documenting code through naming
- Expression-bodied members where appropriate
- String interpolation, switch expressions, null-conditional/coalescing operators
- Always assign number values to enums
- Always use braces `{ }` for `if`/`else`/`for`/`foreach`/`while` — even single-line bodies
- Never use hardcoded `const` for configurable values — extract to dedicated `*Options` class registered via `IOptions<T>` pattern
- Never pass domain entities directly to external APIs or serialization boundaries — map to DTOs; value objects (records without `Id`) can be passed directly
- For optional parameters in builder/fluent APIs, use `if (value is not null)` guard before adding — never use `(object?)value ?? ""` cast pattern
- Do not create duplicate DTOs — reuse existing ones when the shape matches
- `IAiAdapter` `WithName` must follow `ModuleName:FeatureName:Prompt` pattern — no dynamic IDs (e.g., `Vault:UpdateEntry:Strategist`)

## Shared Types (`Palladin.Core.Types`)
- Enums or value types referenced by integration messages (`IIntegrationEvent` or `IIntegrationCommand`) must live in `src/core/Palladin.Core.Types/`, namespace `Palladin.Core.Types`
- Never declare a domain enum inside a module and use it directly in a public integration event — move it to `Core.Types` so all consumers can reference it without coupling to the module
- Exception types live in `Palladin.Core.Types.Exceptions` (e.g. `DomainException`, `EntityNotFoundException`)

## Entity Framework Core
- Minimal configuration — rely on conventions
- Configure only relationships and non-standard mappings in `Infrastructure/Persistence/Configurations/`
- Expression-bodied DbSet properties: `public DbSet<Entity> Entities => Set<Entity>();`
- Check existence before expensive operations, early returns, projection with `Select` when possible
- For polymorphic entities use EF Core TPH (Table-Per-Hierarchy): abstract base class, concrete subclasses, `HasDiscriminator(e => e.Type)` in configuration

## Mutating entities — ALWAYS load-edit-commit through the domain
To change an existing entity, **fetch it through a `{Module}DomainWriteContext` query property, mutate it through a domain method, and persist via `DomainWriteContext.CommitAsync`**. Features never receive a DbContext. Use `{Module}DomainReadContext` only for no-tracking reads; never load an entity through it and then mutate that detached entity. NEVER mutate with `ExecuteUpdateAsync`/`ExecuteDeleteAsync` or a raw DbContext set-based write: that bypasses domain invariants and domain-event dispatch. The state setter lives on the entity (e.g. `agent.UpdateOnConnect(...)`), not in the caller.

Large write-side collections must use deterministic keyset pagination with a bounded page size. When the operation is atomic across pages, keep one database transaction and its locks/fence for the whole operation, flush each page through `DomainWriteContext`, clear the EF `ChangeTracker` after every flush, and commit the transaction only after every page and final invariant check succeed. `Take()` alone is not memory-bounded when the scoped context keeps tracking prior pages; offset pagination is not allowed for mutable write sets. Add tests covering more than one page and rollback after a failure on a later page.

`ExecuteDeleteAsync`/`ExecuteUpdateAsync` are permitted **only** when the entity has no lifecycle domain events, and that must be explicit (a value object with no `EventEntityBase`, e.g. `PushToken`) — otherwise load-edit-commit through `DomainWriteContext` as above. A set-based write on an event-bearing aggregate silently drops its domain events.

## Module Organization
- `public` for module entry points and integration contracts
- `internal` for implementation details
- `private` for class-specific details
- `[PublicAPI]` attribute for public APIs, `[UsedImplicitly]` for DI-discovered types

## IoC Registration
- `AddModuleNameModule` extension method per module
- Infrastructure in `InfrastructureModule`, persistence in `PersistenceModule`
- Value objects: `public sealed record` (immutable, usable in responses)

## MassTransit
- **No `try/catch` in consumers** — MassTransit retry policy handles transient errors (duplicate PK, timeouts). Never catch `PostgresException { SqlState: "23505" }` or `DbUpdateException` in a consumer to paper over race conditions.
- **Idempotency via `UpdatedAt`** — integration event contracts must carry `UpdatedAt: Instant`. Consumer checks `msg.UpdatedAt > existing.UpdatedAt` before writing; if the event is older or equal, skip silently.
- **Features use only split domain contexts** — command consumers under `Features/` use `DomainReadContext` for no-tracking reads and `DomainWriteContext` for tracked loads and commits. Event consumers under `Triggers/` must at minimum persist through `DomainWriteContext`; neither kind ever calls `SaveChangesAsync` directly.

### Consumer registration — .NET 10 compatibility (critical)

`AddConsumers(Assembly[])` uses `AssemblyTypeCache.FindTypes()` internally — async, cached, and **silently returns empty on .NET 10** with no error or exception. Result: zero queues created, consumers never receive events. **Never use the assembly-based overload.** Always scan types explicitly:

```csharp
using MassTransit.Metadata;

var types = assemblies
    .SelectMany(a => { try { return a.GetTypes(); } catch { return []; } })
    .Where(RegistrationMetadata.IsConsumerOrDefinition)
    .ToArray();
configurator.AddConsumers(types);
```

### Module ordering in Program.cs

`AddMassTransitModule` **must be called after all module registrations**. Each `AddXxxModule` populates a static `_assemblies` list via `AddMassTransitAssembly()`. If `AddMassTransitModule` runs first, the list is empty and no consumers or queues are created.

```csharp
// ✅ Correct — modules first, MassTransit last
builder.Services
    .AddIdentityModule(configuration)
    .AddVaultModule(configuration)
    .AddAgentsModule(configuration);

builder.Services
    .AddMassTransitModule(configuration);
```

### cfg.Host() position

`cfg.Host(...)` must be the **first call** inside the `UsingRabbitMq` lambda — before middleware, endpoints, or any other configurator calls.

## Analytics (PostHog)

Backend events are the **source of truth** for business actions (create, revoke, reveal, sign-up…). They are more reliable than frontend events (no ad blockers, no race conditions); frontend/mobile track UI-only interactions. Do not double-track an action on both sides.

**Event name format:** `be:{module}:{event}` — colon-separated, `event` uses hyphens for multi-word (e.g. `be:vault:vault-created`). The `be:` prefix and the `{module}:` segment are assembled **internally** by `PostHogAnalyticsService` (`postHogClient.Capture(distinctId, $"be:{module}:{eventName}", …)`). You pass only `module` + `event` — never write the `be:` prefix yourself.

**How events are emitted:** a domain method emits a domain event → a MassTransit **trigger** consumes it → the trigger calls `IAnalyticsService.CaptureEvent`. **Never call `IAnalyticsService` inline in an endpoint** (see `feedback_analytics_via_domain_events`). This keeps endpoints thin and makes analytics a side-effect of the business event, not the request.

```csharp
// In a Trigger (MassTransit consumer of the domain event), not in the endpoint:
internal sealed class OnVaultCreated(IAnalyticsService analyticsService)
    : IConsumer<VaultCreatedEvent>
{
    public Task Consume(ConsumeContext<VaultCreatedEvent> context)
    {
        var msg = context.Message;
        // distinctId = user id; module + event only — be: is added internally
        analyticsService.CaptureEvent(msg.UserId.ToString(), "vault", "vault-created");
        return Task.CompletedTask;
    }
}
```

| Rule | Detail |
|------|--------|
| Signature | `CaptureEvent(string distinctId, string module, string eventName, Dictionary<string, object>? properties = null)` |
| `distinctId` | the acting user's id as string (`msg.UserId.ToString()`) |
| `module` | `identity` / `vault` / `agents` / `audit` / `notification` — no `be:` prefix |
| `eventName` | hyphenated action, e.g. `vault-created`, `log-queried`, `export-requested` |
| Where it lives | a `Triggers/On{Event}` consumer, never an endpoint |
| Secrets | never put passwords, keys, tokens, or plaintext credentials in `properties` |

---

# Testing Rules

## Frameworks
- **xUnit** — test framework
- **Shouldly** — fluent assertions
- **NSubstitute** — mocking
- **Bogus** — fake data generation (`PrivateCtorFaker` for entities with private constructors)
- **Testcontainers** — PostgreSQL + LocalStack containers
- **WireMock** — external API mocking

## Structure
- Integration tests in `tests/Palladin.Api.Tests/Tests/` mirroring module structure
- Test names: `When_Condition_Then_Expectation`
- Strict `// Given` / `// When` / `// Then` comments only
- Use `[Collection<ApiFactoryCollection>]` to share the `ApiFactory` fixture

## ApiFactory
`ApiFactory` (extends `AppFixture<Program>`) provides:
- PostgreSQL + LocalStack test containers
- Per-module database isolation
- WireMock servers for all external APIs
- `FakeClock` for time-based testing
- NSubstitute mocks (OAuth, GuidProvider, Notifier, Logger, etc.)
- `LoginAs(ApplicationUserType)` → returns `(user, client)` for authenticated requests

## Test Data
- Fakers: static `Create` methods with meaningful parameters, `RuleFor` for specific values
- Seeders: clean up existing data before seeding (`ExecuteDeleteAsync` over PK), return seeded entities
- Do not use API features to set up test data — use Fakers + Seeders directly

```csharp
var userFaker = UserFaker.Create(email: email)
    .RuleFor(x => x.Status, UserStatus.Active)
    .RuleFor(x => x.Name, "John Doe");
var reader = await apiFactory.Services.SeedUserAsync(userFaker);
```

## Endpoint testing
```csharp
var (user, client) = await apiFactory.LoginAs(ApplicationUserType.Guest);
var (response, result) = await client.GETAsync<EndpointType, RequestType, ResponseType>(request);
```

## Key rules
- Do not mock `IAiAdapter`
- Do not test MassTransit consumers via `ITestHarness` — get consumer instance from `ApiFactory.Services`
- Create service scope when verifying database state
- Focus on: happy path, conditional paths, error cases (not found) — skip testing FluentValidation rules via Shouldly
- Avoid unnecessary variables — inline one-time-use values
- Use collection expressions (`[item1, item2]`) where supported
- Always read actual record/DTO constructor signatures before using them — they change frequently and have many parameters

---

## Bounded Pull Request Review

Official Codex review is a bounded release gate, not an iterative design loop.

- Request the first official review only after the scoped implementation is complete, local validation passes, and CI is green.
- Batch all accepted findings into one remediation pass; do not run a separate review after each comment or commit.
- Run at most two standard official review rounds. A third round is allowed only to verify a concrete P0/P1 fix involving security, authorization, data integrity, atomicity, or material performance. Any further round requires explicit product-owner approval.
- Treat review comments critically. Before changing code, identify the reproducible production scenario, verify that the current code permits it, and confirm that the fix belongs to the issue's acceptance criteria.
- P0/P1 findings in scope are blocking. Fix a P2 only when it is real, in scope, and small; otherwise document it or create a follow-up. Do not expand the PR for P3/style feedback, speculative edge cases, or unrelated architecture work.
- A review comment does not expand the tracked issue scope by itself. If remediation would introduce a subsystem, broad abstraction, or substantial diff growth, stop and move it to a follow-up unless it closes a confirmed P0/P1.
- Inspect CI status first. Fetch logs only for failed checks, and then only the failing step and necessary surrounding context.
- Stop the review loop when CI is green, no unresolved in-scope P0/P1 remains, and lower-severity findings are either addressed or explicitly dispositioned.

# Maintaining this file

Repository-local review skills live under `.agents/`. Public CI must not depend on maintainer subscription credentials or execute untrusted pull-request code with elevated permissions.

`AGENTS.md` is always loaded into context, so keep it **lean**. Only guidance you need on *every* iteration belongs here (conventions, the shared-building-block reuse rule, the load-edit-commit rule, the analytics-via-triggers rule). Deep, reference-level detail lives in `docs/architecture/` for cross-cutting concerns and in each module group's `README.md` for aggregates, events and invariants; it is **pointed to** from here, not duplicated.

- Extend this file, module READMEs and `docs/architecture/` docs **autonomously** as you discover stable conventions — do not wait to be asked.
- New module-specific detail → add it to that module group's `README.md`; new cross-cutting detail → add it to `docs/architecture/`. Link it instead of inflating `AGENTS.md`.
- Keep application documentation and instruction files in English. Skill workflow narration and PR review/fix output may be Polish, matching the repository's PR language convention. If you find a stale or contradictory rule, fix it and note the change in your PR.
- `AGENTS.md` and `CLAUDE.MD` are intentionally maintained as complete, byte-for-byte identical copies by product-owner decision. Every instruction change must update both files in the same commit and verify them with `cmp`.

**PR reviewers must check whether a code change requires updating `AGENTS.md`, a module `README.md`, or a `docs/architecture/` doc** — a new module, a new shared building block in `src/core/`, a changed convention, or a new/changed integration event. **Documentation drift is a review finding**, treated like any other defect.
