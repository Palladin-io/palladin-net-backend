# Agent Submission fixture provenance

`fixture-v1.json` is deterministic synthetic conformance data, not a deployed
credential. Every key, seed, nonce and secret in it is invalid outside tests.

| Consumer file | Protocol source | Verifier |
| --- | --- | --- |
| `fixture-v1.json` | `contracts/agent-submission/v1/fixture.json` | `node test/generate-fixture.mjs` |
| `contract-v1.json` | `contracts/agent-submission/v1/contract.json` | `node test/validate.mjs` |

Source: `Palladin-io/palladin-protocol` `main` at commit
`b99b95a6cddbb5bb03eaab74a7d877318f5f29b2`: fixture sha256
`4778ca29b0bee27e8d73c71838f9c51d51ecd59962f7f263272370e53dcf4101`, contract sha256
`b769046407c05f33da6070c3a65af590864d524c342f63d89b84badc2d04916b`. Backend-relevant
`runNegativeCases` of that revision are mirrored in `AgentSubmissionContractNegativeTests`.
On 2026-10-30 both files were compared byte-for-byte with the protocol source.

The backend tests use these vectors to prove that its RFC 8785 canonicalization,
Agent signature input, approver manifest self/continuity signatures, fingerprints
and approver set digest agree byte-for-byte with the reference implementation.
