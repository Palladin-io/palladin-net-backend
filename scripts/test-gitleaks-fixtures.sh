#!/usr/bin/env bash

set -Eeuo pipefail

sharing_repo_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
readonly sharing_repo_root
command -v gitleaks >/dev/null
command -v jq >/dev/null

verify_fixture() {
  local fixture="$1" expected_blob="$2"
  if [[ "$(git -C "$sharing_repo_root" hash-object "tests/Fixtures/SharedUnlock/$fixture")" != "$expected_blob" ]]; then
    printf 'FAIL: %s differs from the independently pinned public protocol fixture.\n' "$fixture" >&2
    exit 1
  fi
}

verify_fixture transport-v1.json 5be6ed8838a3f5e01efa1df3b35e482a269492c9
verify_fixture session-api-v1.json f118a0e836ef2c812d42641b2d3995af1be86388

sharing_probe_root="$(mktemp -d "${TMPDIR:-/tmp}/palladin-gitleaks-fixtures.XXXXXX")"
readonly sharing_probe_root
trap 'rm -rf -- "$sharing_probe_root"' EXIT

prepare_case() {
  local case_name="$1"
  mkdir -p "$sharing_probe_root/$case_name/tests/Fixtures/SharedUnlock"
  cp "$sharing_repo_root/.gitleaks.toml" "$sharing_probe_root/$case_name/.gitleaks.toml"
}

scan_case() {
  local case_name="$1" expected_rule="${2:-}" scan_status=0
  (
    cd "$sharing_probe_root/$case_name"
    gitleaks dir . --config .gitleaks.toml --no-banner --redact \
      --report-format json --report-path "$sharing_probe_root/$case_name.json" \
      > "$sharing_probe_root/$case_name.log" 2>&1
  ) || scan_status=$?
  if [[ -z "$expected_rule" ]]; then
    if [[ "$scan_status" != 0 ]] || ! jq -e 'length == 0' "$sharing_probe_root/$case_name.json" >/dev/null; then
      printf 'FAIL: %s must contain no findings.\n' "$case_name" >&2
      exit 1
    fi
  elif [[ "$scan_status" != 1 ]] || ! jq -e --arg rule "$expected_rule" \
    'any(.[]; .RuleID == $rule)' "$sharing_probe_root/$case_name.json" >/dev/null; then
    printf 'FAIL: %s must retain detection by %s.\n' "$case_name" "$expected_rule" >&2
    exit 1
  fi
  printf 'PASS: %s\n' "$case_name"
}

prepare_case pinned-public-fixtures
cp "$sharing_repo_root/tests/Fixtures/SharedUnlock/transport-v1.json" \
  "$sharing_repo_root/tests/Fixtures/SharedUnlock/session-api-v1.json" \
  "$sharing_probe_root/pinned-public-fixtures/tests/Fixtures/SharedUnlock/"
scan_case pinned-public-fixtures

for fixture in transport-v1.json session-api-v1.json; do
  case_name="changed-value-$fixture"
  prepare_case "$case_name"
  jq 'walk(if type == "object" then with_entries(
    if .key == "derivedKey" or .key == "keyContextDigest"
    then .value = (.value[1:] + .value[:1]) else . end) else . end)' \
    "$sharing_repo_root/tests/Fixtures/SharedUnlock/$fixture" \
    > "$sharing_probe_root/$case_name/tests/Fixtures/SharedUnlock/$fixture"
  scan_case "$case_name" generic-api-key

  case_name="different-path-$fixture"
  prepare_case "$case_name"
  cp "$sharing_repo_root/tests/Fixtures/SharedUnlock/$fixture" \
    "$sharing_probe_root/$case_name/$fixture"
  scan_case "$case_name" generic-api-key

  case_name="different-rule-$fixture"
  prepare_case "$case_name"
  jq '. + {syntheticScannerProbe: ("glpat-" + "7K9m2N5p8R4t6V3w1X0z")}' \
    "$sharing_repo_root/tests/Fixtures/SharedUnlock/$fixture" \
    > "$sharing_probe_root/$case_name/tests/Fixtures/SharedUnlock/$fixture"
  scan_case "$case_name" gitlab-pat
done

printf 'All 7 fixture allowlist cases passed; fixture blobs are unchanged.\n'
