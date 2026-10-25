# PostgreSQL cutover using the existing deployment gate

Use PostgreSQL17 clients and a verified logical restore rehearsal first. Preserve
all current and archived databases, migration history and original recovery data.
The source and target must never have API/background writers at the same time.

1. Suspend automatic deployment and drain/cancel all outstanding CI and SSM deployment
   executions before maintenance. Old queued or rerun source revisions do not understand
   the marker. Verify no old deployment process remains and prevent manual reruns until
   acceptance; re-enable deployment only with the reviewed marker-aware script.
2. Acquire the same host lock as `deploy-stage-remote.sh`. Under that lock create
   `/opt/palladin/deployments/database-cutover` (root-owned,0600). Ordinary deployments
   using the new script fail while the marker exists after lock release.
3. Save the existing Caddyfile. Insert `respond "Database maintenance" 503` before
   its sole reverse_proxy; validate/reload Caddy. Stop every API container and other
   discovered database writers, disable their automatic restart for the maintenance
   period, and verify no write sessions remain. Keep the lock during dumps/restores.
4. Capture final consistent per-database logical dumps while all writes remain
   frozen. Restore every database into the target with deliberate ownership and
   extension compatibility; compare schemas, counts and deterministic data digests.
   Do not mutate or delete source databases. Abort on a mismatch.
5. Prepare the target runtime SecureString on the EC2 through its IAM role. Use a
   non-admin application role and VerifyFull with the image's RDS CA bundle. Keep
   secret values out of commands/logs/CI/local files; use restricted ephemeral files
   on the host only. Remove temporary IAM permissions after completion.
6. Release the lock and invoke the reviewed deployment script with its normal
   immutable image/source and secret-parameter inputs plus
   `PALLADIN_STAGE_DATABASE_CUTOVER=true`. The marker prevents an ordinary deployment
   from starting between these operations. Cutover rejects any running API writer.
7. The script durably appends `target-started=` **before** starting the target-backed
   container: migrations/jobs can write before health passes. It removes the exact
   maintenance response only after candidate health and preserves normal public
   health/container-stability checks. Failed deployment never restarts the old API.
8. After authenticated read/write verification, confirm backup/PITR availability and
   restore to an isolated instance without workers. Verify recovered data and timing.
   Remove the maintenance marker under the host lock only after acceptance; retain
   the original database/container, dumps and recovery points pending cleanup approval.

Before `target-started`, an operator may roll back runtime configuration/routing and
restart the unchanged source after proving the target was never writable. Once that
marker has been written, do **not** restart the stale source: freeze target writers,
reconcile/copy all target changes back or fix forward. Absence of successful health
is not evidence of absence of writes. A marker surviving a host restart still blocks
ordinary deployment; inspect both databases and writers before recovery.
