# PR Review Criteria — Palladin .NET Backend

Detailed checklist for each review category. Load this file in full before starting the review.

> **PR czysto toolingowy** (workflow CI, pliki skill, dokumentacja — brak `.cs` w diffie): pomiń sekcje 1–7 w całości. Sprawdź wyłącznie bezpieczeństwo GitHub Actions (brak `${{ }}` interpolacji w blokach `run:` — używaj `env:`) i czy zmiana robi to co opisuje PR.

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
- Modules communicate **only** via MassTransit integration events — no direct service-to-service calls, no shared DbContexts across modules.
- Domain events stay within the module that emits them. Cross-module reactions use integration events.
- Architecture tests in `Palladin.Tests.Architecture` enforce boundaries — ensure new dependencies don't break them.

### DbContext split
- Read operations: read-only `*DbReadContext`. Write operations: `*DbWriteContext`.
- No write operations via a read context.
- EF configurations live in `Infrastructure/Persistence/Configurations/`.

---

## 2. Code Quality — DRY · SRP · OCP · Clean Code

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

## 3. Performance

- **N+1 queries** — check for `Select(…)` in a loop that executes a DB call per item. Use `Include`, `Join`, or batch operations instead.
- **Projection** — use `.Select(x => new { x.Id, x.Name })` when only a subset of columns is needed; avoid loading full entities for read-only queries.
- **Tracking** — `AsNoTracking()` on read-only EF queries where the result is never mutated.
- **Async** — no `.Result`, `.Wait()`, `.GetAwaiter().GetResult()`. All I/O must be `await`ed.
- **Filter at DB** — `.ToList()` must never appear before a `.Where()` or `.Select()` that belongs at the DB level.
- **Existence checks** — `.AnyAsync()` before fetching a full entity when only existence matters.
- **IAiAdapter naming** — `WithName` follows `ModuleName:FeatureName:Prompt`. No dynamic IDs.

---

## 4. Security

- **Authorization** — every FastEndpoints endpoint must call `Roles(…)`, `Permissions(…)`, or `AllowAnonymous()` (with a comment justifying it) inside `Configure()`.
- **Input validation** — all endpoints with a request body must have a corresponding `Validator<TRequest>`.
- **Sensitive data in logs** — passwords, tokens, private keys, PII must never appear in Serilog log messages or exceptions.
- **API boundaries** — domain entities never passed to API responses; map to DTOs. Value objects (records without `Id`) are safe to pass directly.
- **Secrets** — no hardcoded connection strings, API keys, or secrets; all sourced from `IConfiguration` or `IOptions<T>`.
- **SQL injection** — EF Core parameterizes queries; flag any raw SQL (`FromSqlRaw`, `ExecuteSqlRaw`) that concatenates user input.

---

## 5. Stability & Error Handling

- **Consumer idempotency** — MassTransit consumers that modify state must tolerate duplicate delivery (idempotent by design or via deduplication).
- **Exception types** — domain errors use typed exceptions, not raw `Exception`. Typed exceptions communicate intent.
- **No swallowed exceptions** — `catch { }` or `catch (Exception) { /* ignored */ }` is a bug.
- **Resource cleanup** — `IDisposable` / `IAsyncDisposable` resources wrapped in `using` or `try/finally`.
- **Retry policies** — external HTTP calls should go through Polly policies; flag raw `HttpClient` calls without resilience.

---

## 6. Domain & Analytics Conventions

### Domain events
- Emit methods are `private`, called from within domain entity methods — never from endpoints, consumers, or application services directly.
- Entities own their events.

### Analytics
- **Never** call `analyticsService.CaptureEvent()` (or any analytics method) inside endpoints, consumers, or application services.
- Analytics flow: domain event → MassTransit trigger → analytics publish. The `be:` prefix is added internally by the analytics infrastructure; do not add it manually.

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

## 7. Tests

- **Naming** — `When_Condition_Then_Expectation` for every test method.
- **Structure** — `// Given` / `// When` / `// Then` comment sections only; no other inline comments.
- **Collection** — `[Collection<ApiFactoryCollection>]` on every test class to share the `ApiFactory` fixture.
- **Do not mock** — `IAiAdapter` is never mocked; always use the real implementation in tests.
- **Consumer testing** — do not use `ITestHarness`; retrieve consumer instance directly from `ApiFactory.Services`.
- **Data setup** — Fakers + Seeders only; never use API endpoints to seed test data. Seeders must call `ExecuteDeleteAsync` on the PK before seeding.
- **Coverage for new features** — every new public-facing feature must have integration tests covering: happy path, at least one conditional branch, not-found / error case.
- **Unnecessary variables** — inline one-time-use values; do not assign intermediate variables for the sake of it.

---

## 8. Over-Engineering Check

Flag any of the following:
- An abstraction (interface, base class, generic wrapper) that has exactly one implementation and no concrete extension scenario.
- A pattern (Repository over EF, Command/Query over direct service calls) added "in case we need it later".
- Excessive configuration options for behaviour that is constant across the codebase.
- More than three layers of indirection to perform a simple operation.
- A design decision that makes the code harder to read today for a hypothetical benefit tomorrow.
