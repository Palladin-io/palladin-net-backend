# Database Guidelines

This document defines the cross-cutting database rules for the Palladin .NET backend. It applies to HTTP endpoints regardless of verb, MassTransit consumers, Hangfire jobs, domain persistence contexts, EF Core configurations and migrations.

The goal is predictable correctness and performance without speculative indexes, unnecessary transactions or persistence abstractions that bypass the domain model.

## Persistence boundaries

- Features access persistence only through their module's split `DomainReadContext` and `DomainWriteContext`. `DbReadContext`, `DbWriteContext`, base `DbContext` and `DbSet` are Infrastructure-only.
- Read operations use `DomainReadContext` and no-tracking projections. Write operations load tracked aggregates through `DomainWriteContext`, change state through domain methods and persist through `CommitAsync(...)`.
- Use one context per operation. A mutation uses `DomainWriteContext` for every read and write in that operation, including authorization, existence checks, audit labels, projections and retries. Do not also inject `DomainReadContext`, directly or through a helper. Query-only operations retain `DomainReadContext`; never create a combined `DomainContext`.
- Direct EF Core writes are limited to infrastructure concerns such as migrations and test seed or cleanup code. They are not an alternative feature write path.
- PostgreSQL stores structural integrity through PK, FK, UNIQUE and NOT NULL constraints. Business-value validation and executable behavior belong in domain methods and application services, not CHECK constraints, triggers, stored functions or stored procedures.

## Query design

EF Core LINQ through a domain context is the default query API.

- Apply tenant, authorization and lifecycle filters in the database before materialization.
- Project only the columns required by the response or decision. Do not load a complete aggregate for a read-only response when a projection is sufficient.
- Keep query-only reads no-tracking. Within a mutation, use scalar/DTO projections or `AnyAsync` on `DomainWriteContext` for read-only decisions; these do not materialize tracked entities. Use `AsNoTracking()` when an entity result is deliberately read-only. Load aggregates to mutate with tracking through the same write context and reuse freshly loaded/fenced state; never mutate an entity returned by a read context.
- Prevent N+1 access by joining, projecting correlated values or loading a bounded set in one query.
- Use `AnyAsync()` when only existence matters, and perform cheap existence or authorization checks before expensive materialization when doing so does not introduce a race or information leak.
- Keep pagination deterministic and database-side. Use keyset pagination for growing or mutable collections; offset pagination is not permitted for mutable write sets.
- Bound page size, response size and tracked write batches. A scoped EF change tracker must not retain every earlier page of a large operation.
- Resolve UI-facing names server-side from module-owned data or replicas in the same projection. Do not introduce client-side ID-to-name fan-out queries.

## Index design

Indexes are access-path decisions, not a requirement attached mechanically to every endpoint. Review every new or materially changed database query together with the entity configuration and migration that own its schema.

### Match indexes to real access paths

For each relevant query, inspect:

- equality and tenant-scope predicates in `WHERE` and `JOIN`;
- range predicates and lifecycle or status filters;
- `ORDER BY` and keyset cursor columns, including deterministic tie-breakers;
- expected table growth, result cardinality and query frequency;
- the primary key, unique constraints, foreign-key indexes and existing ordinary, partial or specialized indexes.

Add an index when a credible production-sized access path would otherwise perform materially avoidable scanning or sorting and the index provides a clear read benefit. Large list, sync, search, history, audit and background-repair paths deserve particular attention. Small bounded tables, rare maintenance paths and point lookups already served by PK or UNIQUE constraints normally do not need another index.

### Reuse before adding

- Prefer an existing index when its leftmost prefix, predicate and ordering cover the access path.
- Treat PK and UNIQUE constraints as existing indexes during the comparison.
- Do not add a reordered or partially overlapping index merely because the LINQ property order differs.
- Do not treat two indexes as equivalent merely because they contain the same column set. Compare leftmost prefixes, equality versus range columns, sort requirements, partial predicates, included columns and uniqueness.
- A new overlapping index requires a distinct, current access path that the existing index cannot serve adequately. Hypothetical future queries are not justification.
- Consider write amplification, storage, vacuum and migration cost. Remove or avoid a redundant index instead of accumulating permutations of the same key.

Configure owned indexes in `Infrastructure/Persistence/Configurations/` with EF Core and include the resulting migration in the same change. Provider-specific indexes or filters still need a concrete query and PostgreSQL-specific justification.

### Evidence

The PR should make the access path and index decision reviewable from the query, configuration, migration and tests. For a material performance claim or a non-obvious overlapping index, use a representative non-sensitive data set and inspect the PostgreSQL plan, normally with `EXPLAIN (ANALYZE, BUFFERS)` for a read-only statement. Do not use production secrets or vault data, and do not introduce unstable wall-clock thresholds into CI.

An absent index is a review finding only when the expected scale and query shape make the resulting scan or sort materially harmful. An additional index is a finding when an existing PK, UNIQUE or index already covers the same access path and no distinct benefit is demonstrated.

## Raw SQL

Raw SQL is a last resort, not a parallel persistence style.

It is allowed only when at least one of the following is demonstrated:

- EF Core cannot express required provider-side ordering, range, locking or value-object conversion semantics without incorrect behavior or materializing sensitive or unbounded data;
- a measured, material query-plan problem cannot be resolved with an equivalent maintainable LINQ query;
- a reviewed concurrency or bounded set-based invariant requires a PostgreSQL capability that EF Core does not expose safely.

Every surviving raw SQL use must:

- live behind the owning `DomainReadContext` or `DomainWriteContext`, not in an endpoint against a raw DbContext;
- use interpolated, parameterized APIs such as `SqlQuery(FormattableString)`, `FromSqlInterpolated` or `ExecuteSqlInterpolatedAsync`; never concatenate values or use a `*Raw` API with user-controlled input;
- select only the required columns and preserve tenant, authorization and lifecycle filters;
- document the concrete EF limitation or verified performance/concurrency reason close to the owning persistence abstraction or module architecture note;
- have focused integration tests for result shape, boundaries and failure behavior;
- keep write-side domain invariants and event dispatch intact. A raw write that bypasses the domain commit is not allowed.

Prefer promoting a reusable, parameterized capability to the shared domain-context base only after more than one concrete caller needs the same behavior.

## Transactions and concurrency

`DomainWriteContext.CommitAsync(...)` is the normal write boundary. EF Core already wraps one `SaveChangesAsync` call in a database transaction, so several tracked changes or tables do not by themselves justify `BeginTransactionAsync`.

Prefer domain invariants, PK/FK/UNIQUE constraints and optimistic concurrency tokens. Prepare the complete tracked graph and commit once whenever possible.

An explicit transaction is allowed only when a reviewed invariant cannot be preserved by one domain commit, normally because a bounded operation must flush and clear multiple write-side pages while retaining all-or-nothing rollback. It must:

- state the concrete invariant that separate commits would violate;
- use deterministic lock and write order over a bounded data set;
- remain short and database-only, with no HTTP, S3, broker, e-mail, push or response serialization inside the transaction;
- persist and commit through `DomainWriteContext.CommitAsync(transaction, ...)`, so domain events publish only after database commit;
- include concurrency tests for conflicting operations and a later-page rollback test when paging is involved.

Use optimistic concurrency for read-check-write races. A concurrency token must fence the complete invariant, including related-set or phantom changes, and every command that changes the invariant must update the same aggregate stamp. Handle stale state as a retryable conflict or structural `409`.

Do not replace optimistic concurrency with a wider `FOR UPDATE`, advisory lock or long transaction. Adding an explicit transaction, row lock or transaction-scoped advisory lock is an architecture decision requiring explicit review. External side effects use an outbox or durable state machine with separate short claim and finalize commits.

For an atomic write spanning bounded keyset pages, keep one reviewed transaction, flush each page through `DomainWriteContext`, clear the EF change tracker after every flush and commit only after the final invariant check. `Take()` alone is not memory-bounded while the context continues tracking earlier pages.

`ExecuteDeleteAsync` and `ExecuteUpdateAsync` are allowed only for an entity with no lifecycle domain events, and that exception must be explicit. Event-bearing aggregates always use load-edit-commit so their events are not silently dropped.

## Migration review

Every schema change must be represented by the owning EF Core configuration and migration. Review migrations for:

- the intended PK, FK, UNIQUE, NOT NULL and index changes only;
- accidental duplicate or overlapping indexes, including indexes already created by constraints;
- safe PostgreSQL types, lengths, filters and delete behavior;
- absence of business CHECK constraints, triggers, stored functions and stored procedures;
- consistency between the migration and the current model snapshot.

Pre-production migration squashes remain exceptional and require explicit owner approval under the repository's migration-baseline rules.

## Pull request review checklist

For every changed database access path:

1. Identify the owning business requirement and module invariant.
2. Confirm the operation uses exactly one domain context (read for a query, write for the whole mutation), including its helpers, and keeps authorization and tenant filters database-side. Do not request a second read context merely because a mutation contains projections or existence checks.
3. Review projection, tracking, materialization, pagination and N+1 behavior.
4. Compare the query predicates and ordering with PK, UNIQUE and existing indexes before proposing a new one.
5. Require a distinct current access path for every added overlapping index; do not require an index without a credible scale problem.
6. Treat raw SQL, explicit transactions and locks as reviewed exceptions with a concrete reason and focused tests.
7. Confirm writes preserve domain methods, optimistic concurrency and domain-event dispatch.
