#!/bin/bash
set -euo pipefail

databases=(
    palladin-dev-public-assets
    palladin-dev-identity
    palladin-dev-vault
    palladin-dev-agents
    palladin-dev-notification
    palladin-dev-audit
    palladin-dev-search
    palladin-dev-hangfire
    palladin-test-public-assets
    palladin-test-identity
    palladin-test-vault
    palladin-test-agents
    palladin-test-notification
    palladin-test-audit
    palladin-test-search
)

for database in "${databases[@]}"; do
    psql -v ON_ERROR_STOP=1 --username "$POSTGRES_USER" --dbname postgres \
        --command "CREATE DATABASE \"$database\""
done
