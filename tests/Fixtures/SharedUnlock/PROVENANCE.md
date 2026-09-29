# Shared unlock fixture provenance

These files are deterministic synthetic fixtures, not deployed credentials.
The source is `Palladin-io/palladin-protocol`, under
`contracts/shared-unlock/v1/`:

| Consumer file | Protocol source | Verifier |
| --- | --- | --- |
| `transport-v1.json` | `fixtures.json` | `node test/fixtures.mjs` |
| `session-api-v1.json` | `session-api-fixtures.json` | `node test/session-api-fixtures.mjs` |

Verification on 2026-09-24 compared both files byte-for-byte with the protocol
[source revision](https://github.com/Palladin-io/palladin-protocol/tree/5783fc4f5578981ba22326c3b28de13e6a5839bc/contracts/shared-unlock) and ran both verifiers successfully. The transport generator uses
constant byte buffers for synthetic keys; session tokens are deliberately invalid
placeholders. Run the verifiers from `contracts/shared-unlock` after `npm ci`.

The Gitleaks exception combines the exact two paths, the generic-key rule and
exact synthetic values. A different value in either file must still be detected.
Never add operational credentials to these fixtures or broaden the exception to
the entire fixture directory.
