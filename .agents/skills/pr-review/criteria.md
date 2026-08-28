# PR Review Criteria — Palladin .NET Backend

Detailed checklist for each review category. Load this file in full before starting the review.

> **PR czysto toolingowy** (workflow CI, pliki skill, dokumentacja — brak `.cs` w diffie): pomiń kryteria aplikacyjne. Sprawdź bezpieczeństwo GitHub Actions (brak `${{ }}` interpolacji w blokach `run:` — używaj `env:`), spójność zmienianych instrukcji i czy zmiana robi to, co opisuje PR.

---

## 1. Vertical Slice Architecture & Module Boundaries

### Feature file structure
- Each feature lives in a single file under `Features/`. The file contains, in order:
  1. `Request` record (with `[PublicAPI]`)
  2. `Response` record (with `[PublicAPI]`)
  3. `Validator` class (with `[UsedImplicitly]`, inherits `AbstractValidator<Request>`)
  4. `Endpoint` class (`internal sealed`, `[PublicAPI]`, primary constructor)
- Validator rules: **no** `.WithMessage()` on individual rules — rely on FluentValidation defaults.
- No separate DTO files created when the request/response shape already matches an existing record.

### MassTransit consumers
- One file contains: `IntegrationCommand` record, optional parameters record, `ConsumerDefinition`, `Consumer`.
- Consumer class: `internal sealed`, `[PublicAPI]`, primary constructor.
- Consumer Definition: `internal sealed`, `[UsedImplicitly]`.

### Hangfire cron jobs
- Implement `ICronJob`. Class: `internal sealed`, `[UsedImplicitly]`, primary constructor.
- Registered via `HangfireModule.AddScopedCronJob` in `InfrastructureModule`.

### Module boundaries
- Modules communicate through MassTransit integration events and commands — no direct service-to-service calls or shared persistence contexts across modules.
- Domain events stay within the emitting module. OpenHost read models consume only their own commands; the owning module translates its event into the host command.
- Architecture tests in `tests/Palladin.Tests.Unit/Architecture/` enforce boundaries — ensure new dependencies don't break them.

### Domain persistence split
- Features never inject `*DbReadContext`, `*DbWriteContext`, base `DbContext` or `DbSet`.
- Read operations use the module `*DomainReadContext` and no-tracking projections. Mutations load tracked aggregates through `*DomainWriteContext`, call domain methods and persist through `CommitAsync(...)`.
- A feature may inject both split domain contexts only when it genuinely reads and writes. A combined `*DomainContext` is forbidden.
- EF configurations live in `Infrastructure/Persistence/Configurations/`.

---

## 2. Business Requirements & Scope

- Build a requirement checklist from the PR description, linked or quoted acceptance criteria and the applicable module README.
- Map every stated requirement to an implementation path and focused test. Flag a missing business branch, authorization rule, no-op/idempotency behavior, error contract or observable outcome.
- Verify that tests prove the requirement rather than only execute the happy path. Security and zero-knowledge requirements are blocking even when omitted from the PR description because they are repository invariants.
- Do not invent unavailable product requirements. If the PR does not contain enough business context to determine completeness, state that limitation instead of approving based on assumptions.
- A review finding does not expand scope by itself. Defer unrelated enhancements unless they close a confirmed Critical or Warning issue in the stated acceptance criteria.

---

## 3. Code Quality — DRY · SRP · OCP · Clean Code

### Naming & structure
- Classes: `internal sealed` unless they are module entry points (`AddXxxModule`) or public integration contracts.
- `[PublicAPI]` on public APIs; `[UsedImplicitly]` on DI-discovered types (validators, consumers, jobs).
- No folder named `Services/` — use concern-based names (`OAuth/`, `Jwt/`, `Persistence/`).
- Large `InfrastructureModule` registrations split into submodules: `PersistenceModule`, `OAuthModule`, `JwtModule`, etc.

### Patterns
- Primary constructors for DI — no field-per-dependency boilerplate.
- Records for request/response/value objects.
- Expression-bodied members and switch expressions where they improve readability.
- `var` when the type is obvious from the right-hand side.
- Braces `{ }` always — no braceless single-line `if`/`else`/`for`/`foreach`/`while`.
- String interpolation over concatenation.
- Null-conditional/coalescing operators where appropriate.
- Complex logic extracted into well-named private methods (self-documenting code).

### Anti-patterns to flag
- Hardcoded configurable values — must be extracted to `*Options` classes and registered via `IOptions<T>`.
- `(object?)value ?? ""` — use `if (value is not null)` guard instead.
- Enum values without explicit integer assignments.
- Comments explaining *what* rather than *why* (the code should be obvious).
- Unnecessary constructor injection when a simple static helper suffices.
- Duplicate DTOs when an existing shape matches.

---

## 4. Database Access & Performance

Load `docs/architecture/database-guidelines.md` for every PR that changes a query, index, persistence path, transaction, lock, raw SQL, EF configuration or migration.

### Query shape
- Review every changed database access path, regardless of whether it is reached from HTTP GET/POST, a consumer or a job.
- **N+1** — no database call per item; use one projection, join, correlated projection or bounded batch.
- **Projection and tracking** — select only required columns and keep reads no-tracking. Never load a full aggregate for a read-only response when a projection is sufficient.
- **Filter and paginate in PostgreSQL** — tenant, authorization, lifecycle, cursor and limit are applied before materialization. Growing or mutable collections use deterministic keyset pagination.
- **Async** — no `.Result`, `.Wait()` or `.GetAwaiter().GetResult()` for I/O.
- **Existence** — use `AnyAsync()` when only existence matters, unless an additional query would introduce a race or information leak.

### Indexes
- Compare `WHERE`, `JOIN`, range predicates, `ORDER BY` and keyset tie-breakers with PK, UNIQUE constraints and every existing ordinary, partial or specialized index.
- Prefer an existing index when its leftmost prefix, predicate and ordering cover the access path.
- Flag a missing index only when expected scale, cardinality and frequency make the resulting scan or sort materially harmful. Do not require an index for every query or small bounded table.
- Flag a new reordered or overlapping index when it has no distinct current access path. Compare leftmost prefixes, equality versus range columns, ordering, partial predicates, included columns and uniqueness; the same column set in another order is not automatically equivalent.
- Consider write amplification, storage, vacuum and migration cost. Hypothetical future queries do not justify another index.
- A material performance claim or non-obvious overlapping index should include a representative, non-sensitive PostgreSQL plan or equivalent structural evidence; CI does not use unstable wall-clock thresholds.

### EF Core, raw SQL and transactions
- EF Core LINQ through split domain contexts is the default.
- Raw SQL is a last resort for an EF expressiveness gap, verified material query-plan issue or reviewed PostgreSQL concurrency primitive. It stays behind a domain context, uses interpolated parameterized APIs, selects only required columns and has focused integration tests.
- Flag `FromSqlRaw`, `ExecuteSqlRaw`, concatenated SQL or any user-controlled value outside parameter binding.
- A raw write must preserve domain invariants and event dispatch through `DomainWriteContext`; it is never a direct endpoint-to-DbContext path.
- `CommitAsync(...)` is the normal transaction boundary. Every explicit transaction or lock must name the invariant one commit cannot preserve, remain short/database-only and bounded, use deterministic ordering and have concurrency plus rollback coverage.
- Set-based update/delete is allowed only for an entity with no lifecycle domain events.

### Other performance
- **IAiAdapter naming** — `WithName` follows `ModuleName:FeatureName:Prompt`. No dynamic IDs.

---

## 5. Security

- **Authorization** — every FastEndpoints endpoint explicitly declares an authenticated boundary with `AuthSchemes(…)` and/or a reviewed `Roles(…)`, `Permissions(…)` or `RequirePermission(…)` declaration, or calls `AllowAnonymous()` with justification. Never rely only on a mutable global default.
- **Input validation** — all endpoints with a request body must have a corresponding `Validator<TRequest>`.
- **Sensitive data in logs** — passwords, tokens, private keys, PII must never appear in Serilog log messages or exceptions.
- **API boundaries** — domain entities never passed to API responses; map to DTOs. Value objects (records without `Id`) are safe to pass directly.
- **Secrets** — no hardcoded connection strings, API keys, or secrets; all sourced from `IConfiguration` or `IOptions<T>`.
- **SQL injection** — EF Core parameterizes queries; flag any raw SQL (`FromSqlRaw`, `ExecuteSqlRaw`) that concatenates user input.

---

## 6. Stability & Error Handling

- **Consumer idempotency** — MassTransit consumers that modify state must tolerate duplicate delivery (idempotent by design or via deduplication).
- **Exception types** — domain errors use typed exceptions, not raw `Exception`. Typed exceptions communicate intent.
- **No swallowed exceptions** — `catch { }` or `catch (Exception) { /* ignored */ }` is a bug.
- **Resource cleanup** — `IDisposable` / `IAsyncDisposable` resources wrapped in `using` or `try/finally`.
- **Retry policies** — external HTTP calls should go through Polly policies; flag raw `HttpClient` calls without resilience.

---

## 7. Domain & Analytics Conventions

### Domain events
- Emit methods are `private`, called from within domain entity methods — never from endpoints, consumers, or application services directly.
- Entities own their events.

### Analytics
- **Never** call analytics inside endpoints, command consumers or application services.
- Analytics flow: domain event → MassTransit event trigger → analytics publish. The trigger is the only consumer allowed to call `CaptureEvent()`. The `be:` prefix is added internally; do not add it manually.

### Permissions
- Implemented as `[Flags]` bitwise enum on the `Role` entity. Sum of binary values stored in JWT claims.

### Junction tables
- Composite PK from the two FK columns — no separate auto-increment `Id` column.
- Most-queried FK column listed first in the composite key.

### Queue naming
- Convention: `{module}.{type}.{from-module}` (e.g., `identity.command.billing`).
- All triggers originating from the same source module share a single endpoint.

### Refresh tokens
- Expiry: 365 days.
- Rotate **only** when ≤ 7 days remain; return the same token otherwise.

### NodaTime
- All date/time handling uses NodaTime (`Instant`, `LocalDate`, `Duration`, `ZonedDateTime`).
- `DateTime`, `DateTimeOffset`, and `TimeSpan` are forbidden in domain and application code.

### String constants
- String literals used in infrastructure (e.g. MassTransit endpoint names, header names) must be extracted to `const` fields in the relevant `Infrastructure/` class — no inline strings.

---

## 8. Tests

- **Naming** — `When_Condition_Then_Expectation` for every test method.
- **Structure** — `// Given` / `// When` / `// Then` comment sections only; no other inline comments.
- **Collection** — `[Collection<ApiFactoryCollection>]` on every test class to share the `ApiFactory` fixture.
- **Do not mock** — `IAiAdapter` is never mocked; always use the real implementation in tests.
- **Consumer testing** — do not use `ITestHarness`; retrieve consumer instance directly from `ApiFactory.Services`.
- **Data setup** — Fakers + Seeders only; never use API endpoints to seed test data. Seeders must call `ExecuteDeleteAsync` on the PK before seeding.
- **Coverage for new features** — every new public-facing feature must have integration tests covering: happy path, at least one conditional branch, not-found / error case.
- **Unnecessary variables** — inline one-time-use values; do not assign intermediate variables for the sake of it.

---

## 9. Over-Engineering Check

Flag any of the following:
- An abstraction (interface, base class, generic wrapper) that has exactly one implementation and no concrete extension scenario.
- A pattern (Repository over EF, Command/Query over direct service calls) added "in case we need it later".
- Excessive configuration options for behaviour that is constant across the codebase.
- More than three layers of indirection to perform a simple operation.
- A design decision that makes the code harder to read today for a hypothetical benefit tomorrow.
- Do not classify required domain contexts, module boundaries, authorization controls, zero-knowledge boundaries or a documented concurrency invariant as over-engineering. Simplification must preserve those constraints.
