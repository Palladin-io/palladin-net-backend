#!/usr/bin/env bash

set -Eeuo pipefail

REPOSITORY_ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
readonly REPOSITORY_ROOT
readonly WAIT_SCRIPT="$REPOSITORY_ROOT/scripts/wait-for-ssm-command.sh"

test_root="$(mktemp -d)"
trap 'rm -rf -- "$test_root"' EXIT
mkdir -p "$test_root/bin" "$test_root/state"

printf '%s\n' '#!/usr/bin/env bash' 'exit 0' > "$test_root/bin/sleep"
chmod +x "$test_root/bin/sleep"

# shellcheck disable=SC2016
printf '%s\n' '#!/usr/bin/env bash' 'set -Eeuo pipefail' '
counter_file="$MOCK_STATE/ssm-count"
count=0
[[ ! -f "$counter_file" ]] || count="$(cat "$counter_file")"
count=$((count + 1))
printf "%s" "$count" > "$counter_file"
if (( count == 1 )); then
  printf "%s\n" '\''{"Status":"InProgress","StandardOutputContent":"","StandardErrorContent":""}'\''
else
  printf "%s\n" '\''{"Status":"Success","StandardOutputContent":"done","StandardErrorContent":""}'\''
fi' > "$test_root/bin/aws"
chmod +x "$test_root/bin/aws"

invocation="$(env \
  PATH="$test_root/bin:$PATH" \
  MOCK_STATE="$test_root/state" \
  PALLADIN_SSM_COMMAND_ID=11111111-2222-3333-4444-555555555555 \
  PALLADIN_SSM_INSTANCE_ID=i-0123456789abcdef0 \
  PALLADIN_SSM_AWS_REGION=eu-west-1 \
  PALLADIN_SSM_POLL_INTERVAL_SECONDS=0 \
  "$WAIT_SCRIPT")"

[[ "$(jq -r '.Status' <<< "$invocation")" == Success ]]
[[ "$(cat "$test_root/state/ssm-count")" == 2 ]]

printf 'SSM terminal-status polling scenario passed.\n'
