# Contributing

Thank you for your interest in Palladin. Security, provenance, and contributor rights need a formal acceptance workflow before external code can be incorporated.

## Current contribution status

External code contributions are not accepted at this time. Do not open a pull request containing code, tests, generated artifacts, or documentation changes unless a maintainer has given written approval for that specific contribution. Unsolicited pull requests will be closed without review.

This restriction remains in place until counsel approves and the repository publishes a contributor-license or equivalent acceptance workflow. No contributor license agreement is currently offered or implied.

Bug reports, security reports, and design discussion remain welcome. Use public issues for non-sensitive reports and follow [SECURITY.md](SECURITY.md) for anything security-sensitive. Do not include proprietary code or confidential information in an issue.

## Approved contributors

Maintainers and specifically approved contributors must:

1. Read [AGENTS.md](AGENTS.md) and the relevant architecture documentation.
2. Preserve the zero-knowledge and authorization boundaries described in [README.md](README.md).
3. Base work on `main`, keep commits focused, and use the repository's Conventional Commit format.
4. Add behavior-focused tests and run the full restore, build, and test pipeline.
5. Certify every commit under the [Developer Certificate of Origin 1.1](DCO) by adding a sign-off with `git commit --signoff`.

A sign-off states that the contributor has the right to submit the work under the repository's license. It is not a contributor license agreement and does not replace any separate written approval required by this contribution policy.

## Pull request checklist

- The change has explicit maintainer approval under the current contribution policy.
- Every commit has a valid `Signed-off-by:` trailer matching the contributor's identity.
- Security, API, schema, integration-contract, and operational effects are described.
- Documentation is updated when architecture or behavior changes.
- `dotnet restore`, `dotnet build --no-restore`, and `dotnet test --no-build` pass.
- No secret, private key, token, plaintext credential, personal data, or internal deployment identifier is included.
