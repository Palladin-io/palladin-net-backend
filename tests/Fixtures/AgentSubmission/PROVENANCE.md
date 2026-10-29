# Agent Submission fixture provenance

`fixture-v1.json` is deterministic synthetic conformance data, not a deployed
credential. Every key, seed, nonce and secret in it is invalid outside tests.

| Consumer file | Protocol source | Verifier |
| --- | --- | --- |
| `fixture-v1.json` | `contracts/agent-submission/v1/fixture.json` | `node test/generate-fixture.mjs` |
| `contract-v1.json` | `contracts/agent-submission/v1/contract.json` | `node test/validate.mjs` |

Source: `Palladin-io/palladin-protocol` branch `feat/cvt-627-agent-submission-contract`
at commit `e6e7195`: fixture sha256 `4778ca29b0bee27e8d73c71838f9c51d51ecd59962f7f263272370e53dcf4101`
(unchanged since `de5ae396`), contract sha256 `21a40ebb4366d3559deaeccd4675470e1b07ab384b9ca45d3004c509a97a246a`. Backend-relevant
`runNegativeCases` of `597535b`/`e6e7195` are mirrored in `AgentSubmissionContractNegativeTests`. On 2026-10-29 the file was compared byte-for-byte with the protocol source and the protocol verifier ran
successfully. Re-vendor after the contract pull request merges and record the
merged commit here.

The backend tests use these vectors to prove that its RFC 8785 canonicalization,
Agent signature input, approver manifest self/continuity signatures, fingerprints
and approver set digest agree byte-for-byte with the reference implementation.
