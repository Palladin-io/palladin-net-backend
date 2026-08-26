## Summary

<!-- What changed and why? Link a public issue when applicable. -->

## Requirements

<!-- Link the issue/specification and list or quote every acceptance criterion. Keep this section self-contained when the source is not accessible to repository reviewers. -->

- Issue/specification:
- Acceptance criteria covered:

## Security review

- [ ] I assessed the zero-knowledge boundary and client-side key handling.
- [ ] This change does not persist keys or plaintext credentials.
- [ ] This change does not log secrets, tokens, mnemonics, or vault contents.
- [ ] Cryptographic operations remain in the canonical crypto layer, or this is not applicable.
- [ ] Endpoint authorization was reviewed, or this change has no backend endpoint.
- [ ] New dependencies and generated artifacts were reviewed.

## Verification

<!-- List the exact local checks and relevant manual scenarios. -->

- [ ] Every stated acceptance criterion maps to an implementation path and a focused test or documented manual check.
- [ ] Changed database access paths were reviewed against `docs/architecture/database-guidelines.md`, or this PR does not access the database.

<!-- For a new/changed index, raw SQL, explicit transaction or lock, document the concrete access path/invariant, why the normal EF/domain-commit path is insufficient, and the relevant evidence/tests. -->

## Contributor certification

- [ ] Every commit includes a `Signed-off-by` line as required by the DCO.
