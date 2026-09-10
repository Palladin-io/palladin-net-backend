#!/usr/bin/env bash

set -Eeuo pipefail

REPOSITORY_ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
readonly REPOSITORY_ROOT
readonly DEPLOY_SCRIPT="$REPOSITORY_ROOT/scripts/deploy-stage-remote.sh"
readonly REQUIRED_KEYS="$REPOSITORY_ROOT/scripts/stage-required-env-keys.txt"
readonly SOURCE_SHA="0123456789abcdef0123456789abcdef01234567"
IMAGE_DIGEST="sha256:$(printf 'a%.0s' {1..64})"
readonly IMAGE_DIGEST
readonly IMAGE_URI="example.dkr.ecr.eu-west-1.amazonaws.com/palladin-api-stage@$IMAGE_DIGEST"

test_root="$(mktemp -d)"
trap 'rm -rf -- "$test_root"' EXIT

create_mock() {
  local path="$1"
  shift
  printf '%s\n' '#!/usr/bin/env bash' 'set -Eeuo pipefail' "$@" > "$path"
  chmod +x "$path"
}

prepare_case() {
  local case_name="$1"
  local case_dir="$test_root/$case_name"
  mkdir -p "$case_dir/bin" "$case_dir/state" "$case_dir/deployments"
  printf 'api.stage.palladin.io {\n  reverse_proxy palladin-api:8080\n}\n' > "$case_dir/Caddyfile"

  while IFS= read -r required_key || [[ -n "$required_key" ]]; do
    [[ -z "$required_key" || "$required_key" == \#* ]] && continue
    printf '%s=value\n' "$required_key"
  done < "$REQUIRED_KEYS" > "$case_dir/runtime.env"
  sed -i.bak 's/^ASPNETCORE_ENVIRONMENT=.*/ASPNETCORE_ENVIRONMENT=Staging/' "$case_dir/runtime.env"
  sed -i.bak 's/^Modules__Identity__Waitlist__Enabled=.*/Modules__Identity__Waitlist__Enabled=true/' "$case_dir/runtime.env"
  sed -i.bak 's/^Networking__TrustForwardedHeaders=.*/Networking__TrustForwardedHeaders=true/' "$case_dir/runtime.env"
  rm "$case_dir/runtime.env.bak"

  # The mock programs intentionally expand these variables only when they are executed.
  # shellcheck disable=SC2016
  create_mock "$case_dir/bin/aws" '
case "$*" in
  *"get-parameter"*"runtime-env"*) cat "$MOCK_CASE_DIR/runtime.env" ;;
  *"get-parameter"*"enumeration-secret"*) printf "%064d\n" 0 ;;
  *"get-login-password"*) printf "mock-password\n" ;;
  *) exit 1 ;;
esac'

  # shellcheck disable=SC2016
  create_mock "$case_dir/bin/docker" '
printf "%s\n" "$*" >> "$MOCK_CASE_DIR/state/docker-calls"
case "${1:-}" in
  network) exit 0 ;;
  login)
    cat >/dev/null
    exit 0
    ;;
  pull|run|rm|stop)
    [[ "${1:-}" == "stop" ]] && printf "%s\n" "${@: -1}" >> "$MOCK_CASE_DIR/state/stopped"
    exit 0
    ;;
  image)
    if [[ "${2:-}" == "inspect" ]]; then printf "%s\n" "$PALLADIN_STAGE_SOURCE_SHA"; fi
    exit 0
    ;;
  inspect)
    if [[ "$*" == *"--format"* ]]; then printf "true\n"; fi
    exit 0
    ;;
  exec)
    if [[ "$*" == *" wget "* ]]; then printf "%s\n" "{\"status\":\"Healthy\"}"; fi
    exit 0
    ;;
  logs) printf "bounded mock candidate logs\n"; exit 0 ;;
  *) exit 0 ;;
esac'

  # shellcheck disable=SC2016
  create_mock "$case_dir/bin/curl" '
count_file="$MOCK_CASE_DIR/state/curl-count"
count=0
[[ ! -f "$count_file" ]] || count="$(cat "$count_file")"
count=$((count + 1))
printf "%s" "$count" > "$count_file"
if [[ "${MOCK_FAIL_AFTER_SWITCH:-false}" == "true" ]]; then
  exit 22
fi
printf "%s\n" "{\"status\":\"Healthy\"}"'

  create_mock "$case_dir/bin/sleep" 'exit 0'
  create_mock "$case_dir/bin/flock" 'exit 0'
  printf '%s\n' "$case_dir"
}

run_case() {
  local case_dir="$1"
  shift
  env \
    PATH="$case_dir/bin:$PATH" \
    MOCK_CASE_DIR="$case_dir" \
    PALLADIN_STAGE_IMAGE_URI="$IMAGE_URI" \
    PALLADIN_STAGE_SOURCE_SHA="$SOURCE_SHA" \
    PALLADIN_STAGE_AWS_REGION="eu-west-1" \
    PALLADIN_STAGE_RUNTIME_ENV_PARAMETER="/palladin/stage/api/runtime-env" \
    PALLADIN_STAGE_ENUMERATION_SECRET_PARAMETER="/palladin/stage/identity/password-auth/enumeration-secret" \
    PALLADIN_STAGE_REQUIRED_ENV_KEYS_FILE="$REQUIRED_KEYS" \
    PALLADIN_STAGE_CADDYFILE="$case_dir/Caddyfile" \
    PALLADIN_STAGE_DEPLOYMENT_DIRECTORY="$case_dir/deployments" \
    PALLADIN_STAGE_LOCK_FILE="$case_dir/deploy.lock" \
    "$@" \
    "$DEPLOY_SCRIPT"
}

bash -n "$DEPLOY_SCRIPT"

success_case="$(prepare_case success)"
run_case "$success_case" >/dev/null
grep -q 'reverse_proxy palladin-api-blue:8080' "$success_case/Caddyfile"
grep -q '^palladin-api$' "$success_case/state/stopped"
grep -q "^source_sha=$SOURCE_SHA$" "$success_case/deployments/current"

rollback_case="$(prepare_case rollback)"
if run_case "$rollback_case" MOCK_FAIL_AFTER_SWITCH=true >/dev/null 2>&1; then
  echo "Expected the simulated post-switch health failure to fail the deployment." >&2
  exit 1
fi
grep -q 'reverse_proxy palladin-api:8080' "$rollback_case/Caddyfile"
if [[ -f "$rollback_case/state/stopped" ]]; then
  echo "The previous backend must not be stopped during rollback." >&2
  exit 1
fi
grep -q 'rm --force palladin-api-blue' "$rollback_case/state/docker-calls"

printf 'Stage deployment success and rollback scenarios passed.\n'
