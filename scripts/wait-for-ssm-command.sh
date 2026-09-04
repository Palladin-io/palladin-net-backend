#!/usr/bin/env bash

set -Eeuo pipefail

readonly COMMAND_ID="${PALLADIN_SSM_COMMAND_ID:?PALLADIN_SSM_COMMAND_ID is required}"
readonly INSTANCE_ID="${PALLADIN_SSM_INSTANCE_ID:?PALLADIN_SSM_INSTANCE_ID is required}"
readonly AWS_REGION="${PALLADIN_SSM_AWS_REGION:?PALLADIN_SSM_AWS_REGION is required}"
readonly POLL_INTERVAL_SECONDS="${PALLADIN_SSM_POLL_INTERVAL_SECONDS:-5}"

[[ "$COMMAND_ID" =~ ^[0-9a-f-]{36}$ ]] || { echo "SSM command ID is malformed." >&2; exit 1; }
[[ "$INSTANCE_ID" =~ ^i-[0-9a-f]+$ ]] || { echo "EC2 instance ID is malformed." >&2; exit 1; }
[[ "$AWS_REGION" =~ ^[a-z]{2}-[a-z]+-[0-9]+$ ]] || { echo "AWS region is malformed." >&2; exit 1; }
[[ "$POLL_INTERVAL_SECONDS" =~ ^[0-9]+$ ]] || { echo "SSM poll interval is malformed." >&2; exit 1; }

for command_name in aws jq sleep; do
  command -v "$command_name" >/dev/null || {
    echo "Required command is unavailable: $command_name" >&2
    exit 1
  }
done

last_status=""
consecutive_errors=0
while :; do
  if ! invocation="$(aws ssm get-command-invocation \
    --region "$AWS_REGION" \
    --command-id "$COMMAND_ID" \
    --instance-id "$INSTANCE_ID" \
    --output json 2>/dev/null)"; then
    consecutive_errors=$((consecutive_errors + 1))
    if (( consecutive_errors >= 12 )); then
      echo "SSM invocation remained unavailable after repeated polling." >&2
      exit 1
    fi
    sleep "$POLL_INTERVAL_SECONDS"
    continue
  fi
  consecutive_errors=0

  status="$(jq -er '.Status' <<< "$invocation")"
  if [[ "$status" != "$last_status" ]]; then
    echo "SSM invocation status: $status" >&2
    last_status="$status"
  fi

  case "$status" in
    Pending|InProgress|Delayed|Cancelling)
      sleep "$POLL_INTERVAL_SECONDS"
      ;;
    Success|Cancelled|TimedOut|Failed)
      printf '%s\n' "$invocation"
      exit 0
      ;;
    *)
      echo "SSM returned an unknown invocation status." >&2
      exit 1
      ;;
  esac
done
