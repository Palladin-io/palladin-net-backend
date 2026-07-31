# PublicAssetCatalog module

PublicAssetCatalog owns globally shared, non-secret public assets. Its first supported type is
`websiteIcon`. It owns a separate PostgreSQL context and one public-assets object-storage namespace
per environment. No storage key or provider URL is persisted by another module.

## Model

- `PublicAsset`: logical identity, type, display name, status, current revision.
- `PublicAssetRevision`: immutable digest, validated media metadata, dimensions and private storage key.
- `PublicAssetAlias`: normalized hostname/name/tag lookup keys.
- `PublicAssetUploadSession`: uploader-bound, expiring staging upload with expected MIME, length and digest.

Only `Ready` assets are returned. Completion checks exact staging metadata and SHA-256, identifies and
decodes the raster with bounded dimensions, then re-encodes it as PNG before publishing under an
immutable content-addressed key.

## HTTP API

- `GET /api/public-assets/search?type=websiteIcon&q=...&limit=40`
- `POST /api/public-assets/resolve` (at most 500 normalized hostnames per page)
- `POST /api/public-assets/by-ids` (at most 200 unique IDs; list hydration without N+1)
- `GET /api/public-assets/{assetId}?v={revision}`
- `POST /api/public-assets/uploads`
- `POST /api/public-assets/uploads/{uploadSessionId}/complete`

Public read endpoints are anonymous and return metadata plus a render-ready delivery URL. Production
sets `PublicBaseUrl` to `https://assets.palladin.io`; non-production environments configure the direct
bucket endpoint. Upload endpoints require authenticated Agent management permission and verified email.

Missing website icons are emitted as `AcquireWebsiteIconCommand` integration commands to a durable
RabbitMQ endpoint. The broker provides backpressure and retains accepted work across API restarts;
consumer concurrency controls throughput only and is not a capacity limit. The resolver is idempotent:
redelivery or duplicate client reconciliation first checks the unique hostname alias and cannot create
duplicate catalog entries. Consequently an arbitrarily large import is paged at the HTTP/client
boundary, while every accepted hostname remains durable until its command is consumed.
The first resolve request sets `acquireMissing`; subsequent UI polls are read-only, preventing
polling frequency from multiplying durable commands while acquisition is still in progress.
