# Architecture Reference

This directory is the cross-cutting architecture reference for the Palladin .NET backend. It complements `CLAUDE.MD` (conventions and rules) with shared building blocks and protocol-level decisions. Module-specific architecture is colocated with each domain module in `src/modules/{ModuleName}/README.md` or each OpenHost module in `src/modules/OpenHost/{ModuleName}/README.md`.

## Pre-production database reset

The current migration baseline is intentionally destructive and contains one `Initial` migration for each active module context: Agents, Audit, Identity, Notification, Search and Vault. Existing local or staging databases created from the retired migration histories are incompatible with this baseline and must be dropped and recreated before deploying it. Startup migration then applies the six Initial migrations to empty module databases.

This reset is permitted only because Palladin is pre-production. After recreating the databases, schema changes use ordinary incremental EF Core migrations; another history squash requires explicit owner approval. Migration schemas preserve primary keys, foreign keys, unique constraints and required columns. Business-value validation remains in domain methods and application validators, not PostgreSQL CHECK constraints.

## Core philosophy: reuse `src/core`, never re-implement

The backend is a modular monolith. Everything cross-cutting — base entity/event types, persistence base classes, cursor pagination, JWT claim access, GUID generation, analytics, CDN, error formatting — lives once in `src/core/` as a `Palladin.Core.*` project. Modules consume those building blocks; they do not re-create them.

**Rule:** before writing a helper, a pagination clamp, a claim reader, an error-key formatter, an event-publish loop, or a GUID, check `docs/architecture/building-blocks.md`. If a building block exists, use it. If two or more modules need the same new thing, it belongs in `src/core`, not copied per module.

## Before working on a module

**Before implementing a feature in a module, read that module group's README first.** It lists the aggregates, feature endpoints, the integration events it publishes and consumes, its DbContexts, its Hangfire jobs, its shared `[PublicAPI]` contracts, and its invariants. Skipping this is how cross-module events get missed and invariants get violated.

## Index

| Doc | Contents |
|-----|----------|
| [vault-protocol-2-canonical-wire.md](vault-protocol-2-canonical-wire.md) | Canonical envelope, X25519 wrapper, public trust-anchor, and zero-knowledge delivery contract |
| [building-blocks.md](building-blocks.md) | Full catalog of `Palladin.Core.*` shared building blocks, missing abstractions to add, and dead code to remove |
| [Identity README](../../src/modules/Identity/README.md) | Auth, sessions, organization & user lifecycle |
| [Vault README](../../src/modules/Vault/README.md) | Vaults, entries, grants, agent credential delivery |
| [Agents README](../../src/modules/Agents/README.md) | Agent enrollment, API keys, agent authentication |
| [Audit README](../../src/modules/OpenHost/Audit/README.md) | Append-only audit trail, CSV export |
| [Notification README](../../src/modules/OpenHost/Notification/README.md) | Inbox, SignalR real-time, Firebase push, preferences |
| [Search README](../../src/modules/OpenHost/Search/README.md) | Command-fed administrative search OpenHost |
| [PublicAssetCatalog README](../../src/modules/OpenHost/PublicAssetCatalog/README.md) | Shared non-secret public assets and website icons |

> Note: `CLAUDE.MD` and older docs mention a Billing module. There is no Billing module under `src/modules/` today. Each active module group owns sibling implementation and contracts projects.
