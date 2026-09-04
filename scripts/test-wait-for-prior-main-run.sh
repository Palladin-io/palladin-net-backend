#!/usr/bin/env bash

set -Eeuo pipefail

REPOSITORY_ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
readonly REPOSITORY_ROOT
readonly WAIT_SCRIPT="$REPOSITORY_ROOT/scripts/wait-for-prior-main-run.sh"

test_root="$(mktemp -d)"
trap 'rm -rf -- "$test_root"' EXIT
mkdir -p "$test_root/bin" "$test_root/state"

printf '%s\n' '#!/usr/bin/env bash' 'exit 0' > "$test_root/bin/sleep"
chmod +x "$test_root/bin/sleep"

# The mock expands state only when the generated program executes.
# shellcheck disable=SC2016
printf '%s\n' '#!/usr/bin/env bash' 'set -Eeuo pipefail' '
if [[ "$*" == *"actions/workflows/test.yml/runs"* ]]; then
  printf "%s\n" '\''[{"workflow_runs":[{"id":1000,"run_number":100,"status":"in_progress"},{"id":900,"run_number":99,"status":"in_progress"}]}]'\''
  exit 0
fi
counter_file="$MOCK_STATE/prior-count"
count=0
[[ ! -f "$counter_file" ]] || count="$(cat "$counter_file")"
count=$((count + 1))
printf "%s" "$count" > "$counter_file"
if (( count == 1 )); then printf "in_progress\n"; else printf "completed\n"; fi' \
  > "$test_root/bin/gh"
chmod +x "$test_root/bin/gh"

env \
  PATH="$test_root/bin:$PATH" \
  MOCK_STATE="$test_root/state" \
  PALLADIN_GITHUB_REPOSITORY="Palladin-io/palladin-net-backend" \
  PALLADIN_CURRENT_RUN_ID=1000 \
  PALLADIN_CURRENT_RUN_NUMBER=100 \
  PALLADIN_MAIN_WORKFLOW=test.yml \
  PALLADIN_GITHUB_POLL_INTERVAL_SECONDS=0 \
  "$WAIT_SCRIPT" > "$test_root/output"

grep -q 'Immediately preceding main workflow completed' "$test_root/output"
[[ "$(cat "$test_root/state/prior-count")" == 2 ]]

printf 'Prior-main-run ordering scenario passed.\n'
