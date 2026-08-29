# Current Member Entry sync fixtures

This immutable fixture directory freezes sync policy `2` for CVT-555 while Vault wire protocol remains `2`. It is additive to `fixtures/v2`; neither the protocol registry nor sync policy `1` is rewritten.

The wire fixtures cover a complete `MemberIndex` + `MemberSecret` + `EntryKey` head, the current Member Vault-key wrapper and finite offline-access context, a MemberSecret at the 256 KiB ciphertext boundary, tombstone/reset controls, a response-byte page boundary, a snapshot race and all required mutation transitions. Negative fixtures substitute one independently authoritative binding at a time, test exact lease expiry, corrupt ciphertext and replay an old snapshot boundary.

All credentials, keys, IDs, nonces and plaintext are deterministic synthetic test data. They are invalid for production use. Production implementations must not reuse fixture nonces or keys.

Run `npm run generate:fixtures` and `npm test` from `contracts/vault-v2`. Generated JSON must never be edited by hand.
