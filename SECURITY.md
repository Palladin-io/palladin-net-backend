# Security Policy

Palladin is a password manager. Please handle suspected vulnerabilities carefully and avoid exposing users, credentials, cryptographic material, or exploit details.

## Reporting a vulnerability

Do not open a public issue. Use GitHub private vulnerability reporting from this repository's Security tab. If that channel is unavailable, email [patryk.roguszewski@palladin.io](mailto:patryk.roguszewski@palladin.io) with the subject `Palladin backend security report`.

Include the affected revision, impact, prerequisites, and minimal reproduction steps. Do not include real passwords, keys, tokens, personal data, or production database contents. Use synthetic test data and identify any material that needs a safer transfer channel before sending it.

You should receive an acknowledgement as soon as practical. Maintainers will validate the report, coordinate remediation and disclosure, and credit reporters who request attribution when doing so is safe. This policy does not promise a bounty or a fixed response deadline.

## Supported versions

Palladin is pre-production. Security fixes are made on the current `main` branch; older revisions are not supported unless a release explicitly says otherwise.

## Security boundaries

Reports are especially valuable when they involve:

- plaintext credentials, master keys, vault keys, private keys, or decrypted data reaching the backend, logs, analytics, or persistence;
- broken authentication, authorization, tenant isolation, proof-of-possession, or grant enforcement;
- cryptographic envelope substitution, replay, downgrade, or key-version confusion;
- server-side request forgery, unsafe asset acquisition, secret disclosure, or supply-chain compromise;
- audit-log tampering, destructive data loss, or bypass of domain invariants and event dispatch.

The backend is zero-knowledge for vault contents, not zero-data. Account, organization, authorization, agent, delivery-state, and audit metadata are still sensitive and in scope.
