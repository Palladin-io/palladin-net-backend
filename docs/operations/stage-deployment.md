# Stage deployment

Every push to `main` is deployed to `https://api.stage.palladin.io` only after the backend tests pass and the tested release artifact is built. Pull-request runs never deploy.

Main workflow runs do not share a GitHub concurrency group because GitHub retains only one pending run per group. Before promotion, each run discovers and waits for the immediately preceding `main` push workflow to complete. This forms a chain that preserves every run and deployment order even when several pushes arrive quickly. A host-level lock waits up to one hour and prevents overlapping SSM commands after manual workflow cancellation or infrastructure interruption.

## Trust and configuration boundaries

GitHub Actions authenticates to AWS with the repository's OIDC role. The role trust must remain limited to `repo:Palladin-io/palladin-net-backend:ref:refs/heads/main`; do not add static AWS credentials or a wildcard branch subject.

The deploy job requires these GitHub repository secrets:

- `AWS_REGION`
- `AWS_ROLE_ARN`
- `ECR_REPOSITORY`
- `STAGE_EC2_INSTANCE_ID`

`/palladin/stage/api/runtime-env` is one SSM `SecureString` containing the Docker env-file for stage. The EC2 instance role, not GitHub Actions, retrieves and decrypts it. The password-enumeration secret remains in its dedicated `SecureString` at `/palladin/stage/identity/password-auth/enumeration-secret`. Secret values must never be returned by diagnostics or written to `/opt/palladin/api.env`.

The required configuration names are versioned in `scripts/stage-required-env-keys.txt`. The remote deployment checks their presence without printing values before it starts a candidate.

## Promotion and rollback

The workflow downloads the release archive produced from the successful test run, verifies its checksums and source-revision label, and publishes only the full Git SHA tag to ECR. Runtime promotion uses the resolved immutable ECR digest, never `latest`.

The instance keeps two stable API slots, `palladin-api-blue` and `palladin-api-green`. Deployment starts the inactive slot with bounded local Docker logs, checks its health through the same Docker network as Caddy, and only then reloads Caddy with the candidate upstream. The previous slot stays running until public `/api/health` succeeds and the candidate survives a stability window. Any pre-completion failure restores the old Caddy configuration and removes the failed candidate.

GitHub polls the SSM invocation until it reaches a terminal state. The SSM command has its own one-hour execution timeout, so a GitHub waiter timeout cannot leave an unbounded background deployment racing a later run.

The successful source SHA, image digest, active slot and timestamp are recorded root-only in `/opt/palladin/deployments/current`.

## Database compatibility gate

The current migration baseline is incompatible with databases created from the retired migration history. Do not reset stage automatically. The owner must explicitly approve the stage database reset or a reconciliation plan before the first deployment of the current baseline. Preserve any required waitlist records separately before an approved reset.
