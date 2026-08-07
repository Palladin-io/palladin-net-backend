# Module: Search

## Projects
- `Palladin.Module.Search/` — command-fed search read model, authorization and HTTP API.
- `Palladin.Module.Search.Contracts/` — indexing/removal commands and stable search item types.

## Responsibility

Search owns a command-fed administrative catalog. It searches only server-visible Agent and
organization Member attributes. Vault and Entry presentation and matching belong to the unlocked
client and must never enter this module.

The endpoint is `POST /api/search`; its body query is bounded to 128 characters, escaped for
literal matching, used only in a parameterized database query, and never logged, persisted,
cached, published, audited, or included in analytics. Legacy `GET` requests are rejected.

## OpenHost contracts

- `IndexSearchItemCommand(OrganizationId, ItemId, Type, Name, SearchTerms, UpdatedAt)`
- `RemoveSearchItemCommand(OrganizationId, ItemId, Type, OccurredAt)`
- accepted types: `agent`, `member`; every other type is rejected fail-closed

Owning modules translate their events into these commands. Search does not subscribe directly to
foreign domain events and does not query another module at request time.

## Authorization and persistence

- every query and mutation is tenant-first (`OrganizationId`)
- Member results are visible to authenticated organization members
- Agent results require `AgentManage`; lacking it filters Agent hits rather than returning `403`
- results expose only `Type`, `Id`, and `Name`, are capped at 25, and use no-tracking reads
- `SearchItem` has composite key `(OrganizationId, Id)` and bounded text columns
- removals persist a structural watermark tombstone; stale index commands cannot resurrect a
  removed Agent or Member, and tombstones are excluded from query results; `UpdatedAt` is an
  optimistic concurrency token, and write conflicts propagate to the configured MassTransit retry policy
- the irreversible cutover deletes non-administrative rows and removes legacy metadata, icon,
  permission, Vault scope, and user-scope storage
