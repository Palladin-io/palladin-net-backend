# Agent Submission fixture provenance

`fixture-v1.json` is deterministic synthetic conformance data, not a deployed
credential. Every key, seed, nonce and secret in it is invalid outside tests.

| Consumer file | Protocol source | Verifier |
| --- | --- | --- |
| `fixture-v1.json` | `contracts/agent-submission/v1/fixture.json` | `node test/generate-fixture.mjs` |

Source: `Palladin-io/palladin-protocol` branch `feat/cvt-627-agent-submission-contract`
at commit `597535b` (fixture bytes unchanged since `de5ae396`; sha256
`4778ca29b0bee27e8d73c71838f9c51d51ecd59962f7f263272370e53dcf4101`). Backend-relevant
`runNegativeCases` of that revision are mirrored in `AgentSubmissionContractNegativeTests`. On 2026-10-29 the file was compared byte-for-byte with the protocol source and the protocol verifier ran
successfully. Re-vendor after the contract pull request merges and record the
merged commit here.

The backend tests use these vectors to prove that its RFC 8785 canonicalization,
Agent signature input, approver manifest self/continuity signatures, fingerprints
and approver set digest agree byte-for-byte with the reference implementation.
