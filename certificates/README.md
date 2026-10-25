# RDS PostgreSQL trust bundle

Public AWS RDS regional root certificates, downloaded on 2026-10-25 from
https://truststore.pki.rds.amazonaws.com/eu-west-1/eu-west-1-bundle.pem.
SHA-256: `a11cf9a1d0aadd7db86f92cbaa496466daeb501bf1c5e429d8ce8914a01c15d6`.

The runtime image includes the bundle at `/app/certificates/rds-eu-west-1.pem`.
Deployment-owned Npgsql connection strings must specify `SSL Mode=VerifyFull`
and `Root Certificate=/app/certificates/rds-eu-west-1.pem`. This does not change
local PostgreSQL configuration or embed any deployment credentials/endpoints.
Update the bundle from AWS before a CA rotation requires a new trust root, verify
its certificates and checksum, and deploy the reviewed image before switching CA.
