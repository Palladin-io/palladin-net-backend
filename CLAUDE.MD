# AGENTS.md

This file provides guidance to Codex when working with code in this repository.

## CI/CD

GitHub Actions workflow at `.github/workflows/test.yml` runs on PRs to `main`:
1. `dotnet restore`
2. `dotnet build --no-restore`
3. `dotnet test --no-build` (xUnit + configured Docker-backed infrastructure — Docker required locally)

**All changes must go through PRs** — CI must pass before merging.

Repository: [Palladin-io/palladin-net-backend](https://github.com/Palladin-io/palladin-net-backend)

## Code Review Rules

### Business requirements

- Compare the implementation and tests with every requirement stated in the PR and the applicable module README. Flag missing behavior, an unhandled business branch or a test suite that cannot prove an acceptance criterion. Do not invent requirements that are absent from the available context.
  Safe path: keep the PR description self-contained, map each acceptance criterion to code and focused tests, and call out unavailable product context instead of assuming it.

### Simplicity

- Flag material accidental complexity: a new abstraction, configuration surface, subsystem or chain of indirection that has no current requirement or concrete second use and makes the change harder to reason about.
  Safe path: implement the simplest design that satisfies the current acceptance criteria while preserving the repository's required domain contexts, module boundaries, security controls and zero-knowledge invariants.

### Database access and performance

- Apply [docs/architecture/database-guidelines.md](docs/architecture/database-guidelines.md) to every changed database access path, regardless of whether it is reached from HTTP GET/POST, a consumer or a job. EF Core through the split domain contexts is the default; raw SQL, explicit transactions and locks are reviewed last-resort exceptions that require a concrete reason, bounded scope, parameterization and focused tests.
- Flag a material read whose `WHERE`, `JOIN`, ordering or keyset pagination cannot use a suitable current index at the expected scale. Also flag a new index that duplicates or overlaps a PK, UNIQUE constraint or existing index without serving a distinct current access path. Compare leftmost prefixes, range and ordering columns, partial predicates, included columns and uniqueness; a different column order is neither automatically redundant nor automatically justified. Do not require an index for every query.

## Runtime Secrets

- Never commit or log secret values. Keep production secrets in a dedicated secret manager and inject them into ASP.NET configuration at runtime.
- Grant workloads access only to the exact secrets and operations they need.
- Do not pass plaintext secrets through CI inputs, workflow output, command-line arguments, or persistent deployment files.
- Use local-only `appsettings.Local.json` or environment variables for developer overrides; `appsettings.Local.json` is ignored by Git.
- A deployment is healthy only after the API health endpoint succeeds and the application remains running.

## Public Repository Configuration

Assume this repository and its complete Git history will be public.

- Public client IDs are not secrets, but generic runnable configuration never contains Palladin-owned environment values unless the platform requires it and a security review records why.
- Tracked generic configuration uses empty values or obvious placeholders. Real local values stay in ignored local config, user-secrets or environment variables; staging and production use approved deployment configuration or secret storage.
- A clone or fork must not silently use Palladin cloud projects, quota, telemetry or callbacks. Before tracking a public identifier, verify environment separation and provider-side origin, redirect, app-ID, referrer and API restrictions.
- Client secrets, private keys, service accounts, tokens, signing material and credentials are always secret and never tracked.

## Build, Test, and Run Commands

```bash
dotnet restore Palladin.sln              # Restore dependencies
dotnet build Palladin.sln                # Build (nullable + implicit usings enforced via Directory.Build.props)
dotnet test Palladin.sln                 # Run all xUnit test suites
dotnet run --project src/Palladin.Api/Palladin.Api.csproj  # Run API locally
```

### Running a single test
```bash
dotnet test tests/Palladin.Tests.Integrations --filter "FullyQualifiedName~ClassName.MethodName"
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

Checked-in migration history is append-only. Never delete, edit, regenerate, reorder, consolidate or squash an existing migration without an explicit owner request for that exact rewrite.

- Features, fixes, model changes, migration generation, database resets, disposable environments and earlier approvals never imply rewrite permission. Add an incremental migration by default.
- If a rewrite appears necessary, stop and name the affected contexts and environments plus the required reset or reconciliation. Never apply rewritten history to an existing database outside that explicit approval.
- The earlier pre-production reset produced one `Initial` migration per active context. A newly approved squash replaces history and snapshot together and recreates only approved databases; it keeps no compatibility SQL. All later changes are incremental.
- Migrations contain structural PK, FK, UNIQUE, NOT NULL and index changes, never business CHECK constraints, triggers, stored functions or procedures.

### Project layout
- `src/Palladin.Api/` — Entry point (`Program.cs`), health checks, middleware, shared bootstrap
- `src/core/` — Cross-cutting utilities (Analytics, Ai, MassTransit, Hangfire, Persistence, NodaTime, Security, Events, Cdn, etc.)
- `src/modules/{ModuleName}/` — Domain module group containing the implementation project, contracts project and module README. Active domain groups: Identity, Vault and Agents.
- `src/modules/OpenHost/{ModuleName}/` — Shared Open Host Service group with the same internal layout. Active OpenHost groups: Audit, Notification, Search and PublicAssetCatalog. Billing is planned, not yet present in `src/modules/`.
- `tests/Palladin.Tests.Integrations/` — Integration suite, including endpoint security compliance tests under `Architecture/`
- `tests/Palladin.Tests.Unit/` — Unit suite, including module boundary compliance tests under `Architecture/`

### Module group structure
```
{ModuleRoot}/
├── README.md
├── Palladin.Module.{ModuleName}/
│   ├── Domain/ · Features/ · Infrastructure/ · Shared/ · Triggers/
│   └── {ModuleName}Module.cs
└── Palladin.Module.{ModuleName}.Contracts/
```

Implementation and contracts are siblings under `src/modules/{ModuleName}` or `src/modules/OpenHost/{ModuleName}`. The contracts project lives at `{ModuleRoot}/Palladin.Module.{ModuleName}.Contracts/Palladin.Module.{ModuleName}.Contracts.csproj`, never inside the implementation or as an unowned top-level module.

- `Contracts.Commands` owns commands handled by the module; they implement `IIntegrationCommand`.
- `Contracts.Events` owns every published `IIntegrationEvent`, including self-consumed events; `Domain/Events` is internal only.
- `Contracts.ValueObjects` contains stable immutable serialization-safe values, never entities, persistence models, services or infrastructure.

Use one `Contracts` assembly per module and no `Upstream`/`Downstream` folders. Consumers reference the owner's contracts, not its implementation. Changing a contract namespace or shape changes MassTransit identity/schema and requires versioning or a deliberate pre-production cutover.

**`*Options` placement — co-locate with the concern, never a fixed `Options/` folder.** An `IOptions<T>` config class lives next to whatever it configures. In a module that is usually a subfolder of `Infrastructure/` named after the concern: `Persistence/*PersistenceOptions`, `Crypto/VaultCryptoOptions`, `AgentAuth/AgentAuthenticationOptions`, `Push/FirebaseOptions` (Identity additionally groups a few in `Infrastructure/Options/`). Cross-cutting options are **not in any module at all** — they live in their own `src/core` project beside the service they configure (`PostHogOptions` in `Core.Analytics`, `S3BucketOptions` in `Core.Cdn`, `HangfireOptions` in `Core.Hangfire`, `MassTransitOptions` in `Core.MassTransit`). Bind every one via its `Options.Position` prefix (see Module registration).

### Trigger naming convention
Triggers live at **module root** (e.g., `Palladin.Module.Vault/Triggers/`), never under `Infrastructure/`.
Named as `On{EventName}` — e.g., `OnVaultCreated`, `OnUserRegistered`. Never suffix with `Trigger` or `Handler`.

Classify every MassTransit consumer by the message it handles — never create a generic `Consumers/` directory:
- a consumer of an integration **event** is a trigger and lives in `Triggers/`;
- a consumer of an integration **command** is an asynchronous vertical slice and lives in `Features/`.

When an event has more than one consumer (e.g. notification + analytics), keep the consumers in a per-event folder `Triggers/{EventName}/`; shared trigger helpers go in `Triggers/Shared/`.

A trigger may consume its own module's event and translate it into another module's command to avoid circular dependencies. The publisher depends only on the destination `Contracts` assembly.

### Module registration
Each module exposes `AddXxxModule(IServiceCollection, IConfiguration)` returning `IServiceCollection` for chaining. Modules are registered in `Program.cs` with configuration sections from `appsettings.*.json` under `Modules:ModuleName`. Do not re-register framework services — they are already wired in `Program.cs`.

#### Options binding — bind the section PREFIX, not full field paths

Every `*Options` class declares `Position = "Modules:{Module}:{Section}"` as the section prefix only. Property names complete the configuration path. Bind that prefix with `configuration.GetSection(Options.Position)`, consume it through `IOptions<T>` and never read a full field path or inject `IConfiguration` into a feature.

### Cross-module communication
Modules communicate through MassTransit integration events and commands; domain events stay within their module. Each message catalog lives in the owning `{ModuleRoot}/README.md`.

#### Receive endpoint queue naming

Every consumer has an explicit `ConsumerDefinition<T>` and an endpoint constant in `Infrastructure/MassTransit/*Endpoints.cs`. Names follow `{module}.{type}.{destination}`:

- Events use `{receiver}.events.{source}`; use `self` for owned events and command faults. Consumers in one module for the same source share the endpoint, regardless of side effect. Never introduce another type segment such as `faults`.
- Commands use `{receiver}.commands.{subcontext}`. Prefer `general` or a stable noun such as `inbox`, `realtime`, `scope`, `items` or `member-key-directory`, never the sender or one command verb.

Examples: `vault.events.identity`, `notification.commands.inbox` and `audit.commands.general`. Never hardcode an endpoint name in a definition.

Use one unified `Upserted` event carrying an `EntityChange { Created, Updated }` classifier instead of separate `Created` and `Updated` events. Subscribers branch on the classifier and use `UpdatedAt` for idempotency.

**No cross-module read query interfaces.** A read concern spanning modules becomes a scoped denormalized OpenHost read model.

**OpenHost feeding is command-driven, not event-subscription.** An owner translates its event in `On{Event}` into a command defined by `Host.Contracts`; the host consumes only its own commands and never subscribes to another module's events. Model access with scopes, prefer polymorphic items over per-type tables, and materialize cross-module state instead of querying it live.

See `src/modules/OpenHost/Search/README.md` and `src/modules/OpenHost/Notification/README.md` for complete repository-local examples.

### References between modules
- `Core.*` projects hold shared code (utilities, base contracts, cross-cutting concerns)
- Modules **may** reference other modules via `ProjectReference`, but **must** consume only the public API (`[PublicAPI]`) or contracts (`IIntegrationEvent`, `IIntegrationCommand`)
- Never import `internal` types from another module — every cross-module API must be `public`

### Database strategy

- PostgreSQL is the database; `pg_trgm` is the only enabled extension.
- Features use split `{Module}DomainReadContext` and `{Module}DomainWriteContext`; DbContexts remain Infrastructure-only and test databases remain isolated per module.
- PostgreSQL owns structural PK, FK, UNIQUE and NOT NULL integrity, not business validation or executable behavior.
- Read [docs/architecture/database-guidelines.md](docs/architecture/database-guidelines.md) before changing a query, index, persistence path, transaction, lock, raw SQL, EF configuration or migration.

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

### Human-readable identity in UI responses
By default, UI-facing resource responses return human-readable names beside IDs, resolved server-side from the owning module's table or replica. Do not create per-row client lookups or return a bare identifier as the only user-facing label.

Historical member attribution is the exception: Vault/Audit keep opaque actor IDs, while Identity exposes the durable `GET api/organization/member-directory` (`userId` plus last known `displayName`, including former members) for one organization-scoped in-memory web cache. Do not copy names or plaintext Entry metadata into history or use the role/e-mail-heavy Team endpoint as the resolver. See `src/modules/Identity/README.md` and `brain/Technical/Organization Member Directory.md`.

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
- Do not introduce an interface by default for a class with one module-internal implementation. Add one only for a real module/external boundary, runtime-selected multiple implementations, or a deliberate test seam that tests actually substitute. DI registration or hypothetical future mocking is not sufficient; inject and register the concrete internal class instead.
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

- EF Core LINQ through the split domain contexts is the default. Reads use no-tracking database-side projections; writes load tracked aggregates, call domain methods and finish through `DomainWriteContext.CommitAsync(...)`.
- Keep configuration minimal and in `Infrastructure/Persistence/Configurations/`; rely on conventions except for relationships, indexes and non-standard mappings. Use TPH for polymorphic entities.
- Raw SQL is parameterized and exceptional. It stays behind a domain context and requires a concrete EF limitation or verified material plan/concurrency reason.
- Explicit transactions, row locks and transaction-scoped advisory locks are reviewed architecture exceptions. Prefer one domain commit and optimistic concurrency.
- Large mutable writes use bounded deterministic keyset pages. Set-based updates or deletes are allowed only for entities with no lifecycle domain events.
- The complete query, indexing, raw SQL, transaction, concurrency and migration rules are in [docs/architecture/database-guidelines.md](docs/architecture/database-guidelines.md).

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

Never call `AddConsumers(Assembly[])`. Its internal type cache can silently return no consumers on .NET 10, leaving zero queues. Resolve assembly types explicitly, filter them with `MassTransit.Metadata.RegistrationMetadata.IsConsumerOrDefinition` and pass the resulting type array to `AddConsumers(types)`.

### Module ordering in Program.cs

Call `AddMassTransitModule` after every `AddXxxModule` registration. Modules populate the assembly list consumed by MassTransit; registering it first produces no consumers or queues.

### cfg.Host() position

`cfg.Host(...)` must be the **first call** inside the `UsingRabbitMq` lambda — before middleware, endpoints, or any other configurator calls.

## Analytics (PostHog)

- Backend events are the source of truth for completed business actions; clients track UI-only interactions and never duplicate the outcome.
- A domain method emits the event and a `Triggers/On{Event}` MassTransit consumer calls `IAnalyticsService.CaptureEvent`. Endpoints and application services never emit analytics inline.
- Pass the acting user ID, lowercase owning module and a hyphenated event name. `PostHogAnalyticsService` adds `be:` internally.
- Analytics properties never contain passwords, keys, tokens, plaintext credentials or other sensitive vault data.

---

# Testing Rules

## Frameworks
- **xUnit** — test framework
- **Shouldly** — fluent assertions
- **NSubstitute** — mocking
- **Bogus** — fake data generation (`PrivateCtorFaker` for entities with private constructors)
- **PostgreSQL / LocalStack-compatible test infrastructure** — provisioned outside `ApiFactory` (CI services or the local Docker environment)
- **WireMock** — external API mocking

## Structure
- Integration tests in `tests/Palladin.Tests.Integrations/Features/` mirroring module structure
- Test names: `When_Condition_Then_Expectation`
- Strict `// Given` / `// When` / `// Then` comments only
- Use `[Collection<ApiFactoryCollection>]` to share the `ApiFactory` fixture

## ApiFactory
`ApiFactory` (extends `AppFixture<Program>`) provides:
- configured PostgreSQL databases and LocalStack-compatible endpoints; it does **not** start a container per test process
- Per-module database isolation
- WireMock servers for all external APIs
- `FakeClock` for time-based testing
- NSubstitute mocks (OAuth, GuidProvider, Notifier, Logger, etc.)
- `LoginAs(ApplicationUserType)` → returns `(user, client)` for authenticated requests

The current integration suite uses fixed testing connection strings on `localhost`. Do not assume the referenced Testcontainers package provides per-run isolation, and do not run several full suites concurrently against the same local PostgreSQL instance. In CI, `.github/workflows/test.yml` provisions one PostgreSQL service and initializes the module databases before `dotnet test`.

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

- Start only after implementation, local validation and CI are complete. Batch accepted fixes into one pass and run at most two standard rounds; a third may verify one concrete P0/P1 security, authorization, integrity, atomicity or material-performance fix.
- Verify every finding against a reproducible path, current code, acceptance criteria and repository rules. P0/P1 issues in scope block; small real P2 issues may be fixed, while speculative, P3, unrelated or scope-expanding work is rejected, documented or deferred.
- Inspect only failed CI logs. Stop when CI is green, no in-scope P0/P1 remains and every lower-severity finding has an explicit disposition.

# Maintaining this file

Repository-local review skills live under `.agents/`; public CI never relies on maintainer credentials or runs untrusted PR code with elevated permissions.

Keep always-loaded `AGENTS.md` lean. Put cross-cutting detail in `docs/architecture/` and module detail in the owning `README.md`, then link it from here instead of duplicating it.

- Extend this file, module READMEs and `docs/architecture/` docs **autonomously** as you discover stable conventions — do not wait to be asked.
- New module-specific detail → add it to that module group's `README.md`; new cross-cutting detail → add it to `docs/architecture/`. Link it instead of inflating `AGENTS.md`.
- Keep application documentation and instruction files in English. Skill workflow narration and PR review/fix output may be Polish, matching the repository's PR language convention. If you find a stale or contradictory rule, fix it and note the change in your PR.
- `AGENTS.md` and `CLAUDE.MD` are intentionally maintained as complete, byte-for-byte identical copies by product-owner decision. Every instruction change must update both files in the same commit and verify them with `cmp`.

**PR reviewers must check whether a code change requires updating `AGENTS.md`, a module `README.md`, or a `docs/architecture/` doc** — a new module, a new shared building block in `src/core/`, a changed convention, or a new/changed integration event. **Documentation drift is a review finding**, treated like any other defect.
