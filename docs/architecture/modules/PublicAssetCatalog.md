# PublicAssetCatalog module

PublicAssetCatalog owns globally shared, non-secret public assets. Its first supported type is
`websiteIcon`. It owns a separate PostgreSQL context and one public-assets object-storage namespace
per environment. No storage key or provider URL is persisted by another module.

## Model

- `PublicAsset`: logical identity, type, display name, status, current revision.
- `PublicAssetRevision`: immutable digest, validated media metadata, dimensions and private storage key.
- `PublicAssetAlias`: normalized hostname/name/tag lookup keys.
- `PublicAssetUploadSession`: uploader-bound, expiring staging upload with expected MIME, length and digest.

Search and lookup return only `Ready` assets. Website-icon ensure returns a per-host
`pending`/`ready`/`failed` status and includes `asset` only for `ready`. Completion identifies and decodes
the bounded raster, re-encodes it as PNG, and publishes exactly once under the reserved revision key.

## HTTP API

- `GET /api/public-assets/search?type=websiteIcon&q=...&limit=40`
- `POST /api/public-assets/website-icons/ensure` (authenticated; at most 500 normalized hostnames)
- `POST /api/public-assets/by-ids` (at most 200 unique IDs; list hydration without N+1)
- `GET /api/public-assets/{assetId}?v={revision}`
- `POST /api/public-assets/uploads`
- `POST /api/public-assets/uploads/{uploadSessionId}/complete`

Public read endpoints are anonymous and return metadata plus a render-ready delivery URL. Ensure requires
an email-verified Member with `VaultManage` and is rate-limited both to 30 requests and 500 newly reserved hostnames per Member per minute
because it may schedule outbound acquisition. Production
sets `PublicBaseUrl` to `https://assets.palladin.io`; non-production environments configure the direct
bucket endpoint. Upload endpoints use the dedicated service-to-service authentication scheme.

Missing website icons are first reserved under a unique hostname alias, then emitted as
`AcquireWebsiteIconV2Command(assetId, hostname)` integration commands to the durable
`public-asset-catalog.commands.acquire-website-icon-v2` RabbitMQ endpoint. The previous
pre-production command schema/queue is intentionally retired and may be purged during this
non-production cutover; no production messages or catalog data exist to migrate. The uniqueness
migration deterministically removes duplicate pre-production hostname assets before creating the index.
The broker provides backpressure and retains accepted work across API restarts;
consumer concurrency controls throughput only and is not a capacity limit. A filtered unique hostname
index prevents duplicate website assets. Expected acquisition exhaustion moves the asset to `Failed`;
failed assets retain their hostname alias, return `status: failed` with `asset: null`, and are not re-enqueued by readiness checks.
Anonymous catalog search exposes only ready website icons published through an explicit service upload.
Website hostnames learned from an authenticated Member `ensure` request are acquisition-only records and
never become anonymously enumerable through search, even after their image is ready.
Pending assets never produce a delivery URL. A pending asset that owns a live service upload session is
not treated as an acquisition reservation. Once every session for a still-pending upload expires,
the aggregate is marked deleted and releases its hostname aliases before ensure creates clean per-hostname
acquisition reservations. `PublicAsset.Status` is an optimistic concurrency token, so upload completion
cannot overwrite that transition. A service upload that loses
a hostname reservation race is translated to HTTP 409 instead of leaking a database uniqueness error.
Acquisition reservations persist a dispatch marker. Ensure serializes publication with a row lock
and sets the marker only after the broker accepts the command. A marked Pending reservation is never
re-enqueued merely because time elapsed; a transient acquisition failure re-locks the row, clears the
committed marker, and permits a later explicit ensure to retry without duplicating queued work.
The reserved revision key is written with S3 `If-None-Match: *`,
and every delivery probes that key before contacting the mutable upstream. A retry after an
object-store/database partial commit downloads and decodes the bounded existing object, then completes
the aggregate from those authoritative first-writer bytes even if upstream is unavailable, so duplicate
deliveries can finish the database transition without overwriting different bytes. During explicit
create/edit/import flows, clients may perform bounded batch readiness checks against ensure and persist
only returned `Ready` asset `id`, `revision`, and `url` values inside encrypted Vault presentation data.
Vault list/detail reads never call ensure, resolve, by-id, or get-by-id for Entry icons, and bucket/CDN
image failures fall back immediately to a local glyph without cache-busting retries.
