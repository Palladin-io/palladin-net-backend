# Shared Unlock scanner exceptions

These fixtures contain synthetic public protocol data, not application credentials.
Never deploy their keys or tokens. The two files below are byte-for-byte copies
from `Palladin-io/palladin-protocol` commit
`0a70644fe3fbdeba49d77a8509ec27c449a1d4fa`:

| Local file | Upstream file in `contracts/shared-unlock/v1/` | Git blob SHA-1 |
| --- | --- | --- |
| `transport-v1.json` | [fixtures.json](https://github.com/Palladin-io/palladin-protocol/blob/0a70644fe3fbdeba49d77a8509ec27c449a1d4fa/contracts/shared-unlock/v1/fixtures.json) | `5be6ed8838a3f5e01efa1df3b35e482a269492c9` |
| `session-api-v1.json` | [session-api-fixtures.json](https://github.com/Palladin-io/palladin-protocol/blob/0a70644fe3fbdeba49d77a8509ec27c449a1d4fa/contracts/shared-unlock/v1/session-api-fixtures.json) | `f118a0e836ef2c812d42641b2d3995af1be86388` |

Gitleaks 8.30.1 flags two fixed `derivedKey` outputs in the transport vectors and
36 repetitions of one fixed `keyContextDigest` in the API fixture. These values
are independently published test vectors; the digest is not a secret. The
repository configuration exempts only `generic-api-key` findings matching both
the exact relative file path and one of those exact three values. Other rules,
changed values and copies in other paths remain scanned. Existing exceptions
for unrelated fixtures are unchanged.

Run the regression with Bash, Git, jq and Gitleaks available (validated with
Gitleaks 8.30.1). From the repository root:

```bash
bash scripts/test-gitleaks-fixtures.sh
```

It checks the independently pinned blob IDs before scanning isolated temporary
cases. It accepts the reviewed fixtures and proves detection of a changed value,
an unchanged value at another path and another rule's synthetic token in each
exempted file. Test probes and scanner reports are local and removed on exit;
failure output never includes detected values. It neither changes the canonical
fixtures nor invokes CI.

For a full current-tree scan, export tracked files with `git checkout-index`
and run `gitleaks dir . --config .gitleaks.toml --redact --no-banner` **from inside
the export directory**. Scanning an absolute directory path from another working
directory can prevent anchored relative-path exceptions from matching. A
current-tree scan is not a scan of complete Git history.

Any fixture or exception change requires renewed provenance review and these
regression checks; do not exempt the entire directory or arbitrary key fields.
