#!/usr/bin/env bash

set -Eeuo pipefail

readonly REPOSITORY="${PALLADIN_GITHUB_REPOSITORY:?PALLADIN_GITHUB_REPOSITORY is required}"
readonly CURRENT_RUN_ID="${PALLADIN_CURRENT_RUN_ID:?PALLADIN_CURRENT_RUN_ID is required}"
readonly CURRENT_RUN_NUMBER="${PALLADIN_CURRENT_RUN_NUMBER:?PALLADIN_CURRENT_RUN_NUMBER is required}"
readonly WORKFLOW="${PALLADIN_MAIN_WORKFLOW:-test.yml}"
readonly POLL_INTERVAL_SECONDS="${PALLADIN_GITHUB_POLL_INTERVAL_SECONDS:-10}"

[[ "$REPOSITORY" =~ ^[A-Za-z0-9_.-]+/[A-Za-z0-9_.-]+$ ]] || {
  echo "GitHub repository name is malformed." >&2
  exit 1
}
[[ "$CURRENT_RUN_ID" =~ ^[0-9]+$ ]] || { echo "GitHub run ID is malformed." >&2; exit 1; }
[[ "$CURRENT_RUN_NUMBER" =~ ^[0-9]+$ ]] || { echo "GitHub run number is malformed." >&2; exit 1; }
[[ "$WORKFLOW" =~ ^[A-Za-z0-9_.-]+$ ]] || { echo "GitHub workflow name is malformed." >&2; exit 1; }
[[ "$POLL_INTERVAL_SECONDS" =~ ^[0-9]+$ ]] || { echo "GitHub poll interval is malformed." >&2; exit 1; }

for command_name in gh jq sleep; do
  command -v "$command_name" >/dev/null || {
    echo "Required command is unavailable: $command_name" >&2
    exit 1
  }
done

history_url="/repos/$REPOSITORY/actions/workflows/$WORKFLOW/runs?branch=main&event=push&per_page=100"
history_attempt=0
while :; do
  history="$(gh api --paginate --slurp --method GET "$history_url")"
  current_visible="$(jq -er --argjson current "$CURRENT_RUN_ID" \
    '[.[].workflow_runs[]? | select(.id == $current)] | length == 1' <<< "$history")"
  if [[ "$current_visible" == true ]]; then
    break
  fi

  history_attempt=$((history_attempt + 1))
  if (( history_attempt >= 12 )); then
    echo "Current main workflow did not become visible through the GitHub API." >&2
    exit 1
  fi
  sleep "$POLL_INTERVAL_SECONDS"
done

prior_run_id="$(jq -r --argjson current "$CURRENT_RUN_NUMBER" '
  [.[].workflow_runs[]? | select(.run_number < $current)]
  | if length == 0 then empty else max_by(.run_number).id end
' <<< "$history")"

if [[ -z "$prior_run_id" ]]; then
  echo "No earlier main workflow exists; deployment may proceed."
  exit 0
fi
[[ "$prior_run_id" =~ ^[0-9]+$ ]] || { echo "Previous GitHub run ID is malformed." >&2; exit 1; }

last_status=""
while :; do
  prior_status="$(gh api --method GET "/repos/$REPOSITORY/actions/runs/$prior_run_id" --jq '.status')"
  if [[ "$prior_status" != "$last_status" ]]; then
    echo "Previous main workflow status: $prior_status"
    last_status="$prior_status"
  fi
  [[ "$prior_status" != completed ]] || break
  sleep "$POLL_INTERVAL_SECONDS"
done

echo "Immediately preceding main workflow completed; deployment may proceed."
