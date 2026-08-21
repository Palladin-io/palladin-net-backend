# Palladin .NET Backend

Palladin is a zero-knowledge password manager for AI agents. This repository contains the .NET 10 backend: a modular monolith that coordinates identities, encrypted vault data, agents, grants, notifications, search, and audit records without receiving plaintext credentials or client-held encryption keys.

The backend is pre-production. Its current migration baseline intentionally assumes empty databases and may be incompatible with earlier development data.

## Security model

Security is an architectural constraint, not an optional feature:

- Vault secrets cross the API only as authenticated encrypted envelopes. The backend must never receive or log plaintext credentials.
- Master keys, vault keys, private keys, and decrypted values belong on clients and must never be added to server DTOs, persistence models, telemetry, or logs.
- The service is zero-knowledge for vault contents, not zero-data. Account, organization, agent, authorization, delivery-state, and audit metadata remain visible to the backend and must be protected accordingly.
- Every FastEndpoints endpoint explicitly declares an authorization policy or `AllowAnonymous()`.
- Mutations load tracked aggregates through a module `DomainWriteContext`, invoke domain behavior, and commit through the domain context so invariants and events cannot be bypassed.
- Runtime secrets must come from environment-specific configuration or a secret manager. The committed development and testing credentials are local-only fixtures and must never be deployed.
- Forwarded headers are ignored unless trusted proxies or networks are explicitly configured. Production deployments must terminate TLS and configure that trust boundary deliberately.

The canonical vault wire contract and cryptographic boundaries are documented in [Vault protocol 2](docs/architecture/vault-protocol-2-canonical-wire.md). Start an architecture review at [docs/architecture/README.md](docs/architecture/README.md).

Every running build exposes its corresponding source revision and license through the anonymous `GET /api/health` response. Official container images also carry OCI source, revision, and license labels.

Do not report suspected vulnerabilities in a public issue. Use GitHub private vulnerability reporting from the repository's Security tab, or contact the maintainers privately through the Palladin organization.

## Architecture

The application is one deployable API with isolated bounded contexts:

| Area | Responsibility |
|---|---|
| `src/Palladin.Api` | Process entry point, HTTP pipeline, health checks, and module composition |
| `src/core` | Shared security, persistence, messaging, API, analytics, and infrastructure building blocks |
| Identity | Accounts, authentication, organizations, membership, recovery, and key-directory metadata |
| Vault | Encrypted vaults and entries, grants, delivery envelopes, synchronization, and key rotation |
| Agents | Agent enrollment, API keys, proof-of-possession authentication, and lifecycle |
| Audit | Append-only audit records and asynchronous export |
| Notification | Inbox, SignalR, email, and push orchestration |
| Search | Event-fed, access-scoped read models |
| PublicAssetCatalog | Safely acquired and published public icons and presentation assets |

Modules communicate asynchronously through MassTransit contracts. Read and write persistence are split per module, and cross-module read concerns use event-fed OpenHost modules instead of live database queries.

Key technologies include FastEndpoints, Entity Framework Core with PostgreSQL, MassTransit with RabbitMQ, Hangfire, SignalR, NodaTime, Serilog, xUnit, Shouldly, NSubstitute, and Testcontainers.

## Local development

Prerequisites:

- .NET 10 SDK
- Docker Engine with Docker Compose

Start PostgreSQL, RabbitMQ, and the local S3-compatible storage used by presentation assets:

```bash
docker compose up -d --wait
```

The compose stack binds every service to loopback only and initializes isolated development and test databases. The committed development configuration points storage-dependent features at the LocalStack S3 endpoint on `http://localhost:4566`; the API creates the required public-asset bucket when it starts.

Restore, build, and run the API:

```bash
dotnet restore Palladin.sln
dotnet build Palladin.sln --no-restore
dotnet run --project src/Palladin.Api/Palladin.Api.csproj
```

The development API listens on `http://localhost:5000`. Check `GET /api/health`; Scalar API documentation is available at `/scalar` only in Development.

For local overrides, use ASP.NET's double-underscore environment syntax or the ignored local settings file. For example:

```bash
Modules__Identity__Google__ClientId=your-local-client-id \
dotnet run --project src/Palladin.Api/Palladin.Api.csproj
```

Never commit `appsettings.Local.json`, `.env` files, service-account JSON, signing keys, tokens, or production connection strings.

## Tests

Run the same pipeline as CI:

```bash
dotnet restore Palladin.sln
dotnet build Palladin.sln --no-restore
dotnet test Palladin.sln --no-build --verbosity normal
```

The unit project can be run independently:

```bash
dotnet test tests/Palladin.Tests.Unit/Palladin.Tests.Unit.csproj
```

Integration tests require the local PostgreSQL test databases. A fresh `docker compose up -d postgres` initializes them; external delivery and storage boundaries are replaced with test doubles where the suite requires isolation.

## Contributing

Read [AGENTS.md](AGENTS.md) before changing code and the relevant module document before implementing a feature. Keep changes within a vertical slice, preserve zero-knowledge boundaries, add behavior-focused tests, and run the full build and test pipeline before opening a pull request.

All changes are reviewed through pull requests to `main`. Security boundary changes, integration-contract changes, and schema changes should be called out explicitly in the pull request description.

## License and notices

Copyright 2026 Patryk Roguszewski, trading as Patryk Roguszewski IT Solutions.

The backend is licensed under the [GNU Affero General Public License v3.0 only](LICENSE), identified as `AGPL-3.0-only`. See [NOTICE](NOTICE), [TRADEMARKS.md](TRADEMARKS.md), [THIRD_PARTY_NOTICES.md](THIRD_PARTY_NOTICES.md), and the [CycloneDX SBOM](sbom.cdx.json) for additional information.
