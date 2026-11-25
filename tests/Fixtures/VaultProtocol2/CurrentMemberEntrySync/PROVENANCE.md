# Current Member Entry Sync v2 fixture provenance

- Repository: `Palladin-io/palladin-protocol`
- Default-branch commit: `f0d1563cf04eb790b5b8e3b28f974a6301338100`
- Policy source: `contracts/vault-v2/v2/current-member-entry-sync-policy.json`
- Fixture source: `contracts/vault-v2/fixtures/current-member-entry-sync-v2/`

The vendored files are byte-for-byte copies from the pinned `origin/main` commit. Update this pin and the conformance tests together; do not edit individual vectors locally.

## Backend wire migration (CVT-690, 2026-11-04)

The backend copy no longer matches the frozen upstream bytes: `recipientKeyKind` `memberX25519` was renamed to
the algorithm-neutral `memberEncryption` (key-wrapper v1 registry name) in the vectors, negative cases and
`build-fixtures.mjs`, and `fixtures/manifest.json` hashes were recomputed for those files. The upstream
`palladin-protocol` vault-v2 directory stays historical and unchanged.
