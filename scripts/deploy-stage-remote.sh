#!/usr/bin/env bash

set -Eeuo pipefail
umask 077

readonly IMAGE_URI="${PALLADIN_STAGE_IMAGE_URI:?PALLADIN_STAGE_IMAGE_URI is required}"
readonly SOURCE_SHA="${PALLADIN_STAGE_SOURCE_SHA:?PALLADIN_STAGE_SOURCE_SHA is required}"
readonly AWS_REGION="${PALLADIN_STAGE_AWS_REGION:?PALLADIN_STAGE_AWS_REGION is required}"
readonly RUNTIME_ENV_PARAMETER="${PALLADIN_STAGE_RUNTIME_ENV_PARAMETER:?PALLADIN_STAGE_RUNTIME_ENV_PARAMETER is required}"
readonly ENUMERATION_SECRET_PARAMETER="${PALLADIN_STAGE_ENUMERATION_SECRET_PARAMETER:?PALLADIN_STAGE_ENUMERATION_SECRET_PARAMETER is required}"
readonly REQUIRED_ENV_KEYS_FILE="${PALLADIN_STAGE_REQUIRED_ENV_KEYS_FILE:?PALLADIN_STAGE_REQUIRED_ENV_KEYS_FILE is required}"

readonly CADDY_CONTAINER="${PALLADIN_STAGE_CADDY_CONTAINER:-palladin-caddy}"
readonly CADDYFILE="${PALLADIN_STAGE_CADDYFILE:-/opt/palladin/caddy/Caddyfile}"
readonly DOCKER_NETWORK="${PALLADIN_STAGE_DOCKER_NETWORK:-palladin-stage}"
readonly DEPLOYMENT_DIRECTORY="${PALLADIN_STAGE_DEPLOYMENT_DIRECTORY:-/opt/palladin/deployments}"
readonly PUBLIC_HEALTH_URL="${PALLADIN_STAGE_PUBLIC_HEALTH_URL:-https://api.stage.palladin.io/api/health}"
readonly DEPLOYMENT_LOCK_FILE="${PALLADIN_STAGE_LOCK_FILE:-/var/lock/palladin-stage-api-deploy.lock}"
readonly DEPLOYMENT_LOCK_WAIT_SECONDS="${PALLADIN_STAGE_LOCK_WAIT_SECONDS:-3600}"

workdir=""
candidate=""
old_upstream=""
candidate_started=false
caddy_switched=false
deployment_complete=false

log() {
  printf '[stage-deploy] %s\n' "$*"
}

fail() {
  printf '[stage-deploy] ERROR: %s\n' "$*" >&2
  return 1
}

short_identifier() {
  local value="$1"
  if (( ${#value} <= 18 )); then
    printf '%s' "$value"
    return
  fi

  printf '%s…%s' "${value:0:8}" "${value: -6}"
}

container_exists() {
  docker inspect "$1" >/dev/null 2>&1
}

container_is_running() {
  [[ "$(docker inspect --format '{{.State.Running}}' "$1" 2>/dev/null)" == "true" ]]
}

restore_caddy() {
  local backup="$workdir/Caddyfile.before"
  [[ -f "$backup" ]] || return 0

  cp "$backup" "$CADDYFILE"
  docker exec "$CADDY_CONTAINER" caddy validate --config /etc/caddy/Caddyfile >/dev/null
  docker exec "$CADDY_CONTAINER" caddy reload --config /etc/caddy/Caddyfile >/dev/null
}

on_error() {
  local exit_code="$1"
  local rollback_succeeded=true
  trap - ERR
  set +e

  if [[ "$candidate_started" == true ]] && container_exists "$candidate"; then
    log "Candidate startup log tail follows."
    docker logs --tail 200 "$candidate" >&2
  fi

  if [[ "$caddy_switched" == true && "$deployment_complete" != true ]]; then
    log "Restoring the previous Caddy upstream."
    if ! restore_caddy; then
      rollback_succeeded=false
      log "Automatic Caddy rollback failed and needs operator attention; retaining the candidate."
    fi
  fi

  if [[ "$candidate_started" == true && "$rollback_succeeded" == true ]] \
    && container_exists "$candidate"; then
    docker rm --force "$candidate" >/dev/null 2>&1 || true
  fi

  exit "$exit_code"
}

cleanup() {
  local exit_code="$?"
  [[ -z "$workdir" ]] || rm -rf -- "$workdir"
  exit "$exit_code"
}

trap 'on_error "$?"' ERR
trap cleanup EXIT

for command_name in aws chmod cp curl date docker flock grep install mktemp mv sed seq sleep; do
  command -v "$command_name" >/dev/null || fail "Required command is unavailable: $command_name"
done

[[ "$SOURCE_SHA" =~ ^[0-9a-f]{40}$ ]] || fail "Source revision must be a full lowercase Git SHA."
[[ "$IMAGE_URI" =~ ^[^/]+/[^@]+@sha256:[0-9a-f]{64}$ ]] \
  || fail "Image must be addressed by an ECR digest, never by a mutable tag."
[[ "$AWS_REGION" =~ ^[a-z]{2}-[a-z]+-[0-9]+$ ]] || fail "AWS region is malformed."
[[ "$RUNTIME_ENV_PARAMETER" =~ ^/[-A-Za-z0-9_./]+$ ]] || fail "Runtime parameter name is malformed."
[[ "$ENUMERATION_SECRET_PARAMETER" =~ ^/[-A-Za-z0-9_./]+$ ]] \
  || fail "Enumeration-secret parameter name is malformed."
[[ "$DEPLOYMENT_LOCK_WAIT_SECONDS" =~ ^[0-9]+$ ]] || fail "Deployment lock timeout is malformed."
[[ -r "$REQUIRED_ENV_KEYS_FILE" ]] || fail "Required environment-key manifest is unavailable."
[[ -f "$CADDYFILE" ]] || fail "Caddy configuration is unavailable."

exec 9> "$DEPLOYMENT_LOCK_FILE"
flock --exclusive --wait "$DEPLOYMENT_LOCK_WAIT_SECONDS" 9 \
  || fail "Another stage deployment still owns the host lock."

docker network inspect "$DOCKER_NETWORK" >/dev/null
container_is_running "$CADDY_CONTAINER" || fail "Caddy is not running."

old_upstream="$(sed -nE 's/^[[:space:]]*reverse_proxy[[:space:]]+([^[:space:]:]+):8080.*$/\1/p' "$CADDYFILE")"
[[ "$old_upstream" =~ ^palladin-api(-blue|-green)?$ ]] \
  || fail "Caddy must contain exactly one supported backend upstream."
[[ "$(grep -Ec '^[[:space:]]*reverse_proxy[[:space:]]+' "$CADDYFILE")" == "1" ]] \
  || fail "Caddy must contain exactly one reverse_proxy directive."
container_is_running "$old_upstream" || fail "The current backend upstream is not running."

case "$old_upstream" in
  palladin-api-blue) candidate="palladin-api-green" ;;
  *) candidate="palladin-api-blue" ;;
esac

workdir="$(mktemp -d)"
readonly runtime_env_file="$workdir/runtime.env"
readonly enumeration_env_file="$workdir/enumeration.env"
readonly docker_config="$workdir/docker-config"
cp "$CADDYFILE" "$workdir/Caddyfile.before"
install -d -m 700 "$docker_config"
export DOCKER_CONFIG="$docker_config"

log "Preflighting stage configuration without exposing values."
aws ssm get-parameter \
  --region "$AWS_REGION" \
  --name "$RUNTIME_ENV_PARAMETER" \
  --with-decryption \
  --query 'Parameter.Value' \
  --output text > "$runtime_env_file"
chmod 600 "$runtime_env_file"
[[ -s "$runtime_env_file" ]] || fail "Runtime environment parameter is empty."

if grep -Eq '^[[:space:]]*(export[[:space:]]+)?Modules__Identity__PasswordAuth__EnumerationSecret=' "$runtime_env_file"; then
  fail "EnumerationSecret must come only from its dedicated SecureString parameter."
fi

while IFS= read -r required_key || [[ -n "$required_key" ]]; do
  [[ -z "$required_key" || "$required_key" == \#* ]] && continue
  [[ "$required_key" =~ ^[A-Za-z_][A-Za-z0-9_]*$ ]] || fail "Required environment-key manifest is malformed."
  if ! grep -Eq "^${required_key}=.+$" "$runtime_env_file"; then
    fail "Required runtime configuration is absent or empty: $required_key"
  fi
done < "$REQUIRED_ENV_KEYS_FILE"

environment_name="$(sed -nE 's/^ASPNETCORE_ENVIRONMENT=(.*)$/\1/p' "$runtime_env_file")"
[[ "$environment_name" == "Staging" ]] || fail "ASPNETCORE_ENVIRONMENT must be Staging."
waitlist_enabled="$(sed -nE 's/^Modules__Identity__Waitlist__Enabled=(.*)$/\1/p' "$runtime_env_file")"
[[ "$waitlist_enabled" =~ ^[Tt][Rr][Uu][Ee]$ ]] || fail "The stage waitlist must be enabled."
forwarded_headers_enabled="$(sed -nE 's/^Networking__TrustForwardedHeaders=(.*)$/\1/p' "$runtime_env_file")"
[[ "$forwarded_headers_enabled" =~ ^[Tt][Rr][Uu][Ee]$ ]] \
  || fail "Trusted forwarded headers must be enabled behind Caddy."

enumeration_secret="$(aws ssm get-parameter \
  --region "$AWS_REGION" \
  --name "$ENUMERATION_SECRET_PARAMETER" \
  --with-decryption \
  --query 'Parameter.Value' \
  --output text)"
(( ${#enumeration_secret} >= 32 )) || fail "Enumeration secret is missing or too short."
[[ "$enumeration_secret" != *$'\n'* && "$enumeration_secret" != *$'\r'* ]] \
  || fail "Enumeration secret contains a line break."
printf 'Modules__Identity__PasswordAuth__EnumerationSecret=%s\n' "$enumeration_secret" \
  > "$enumeration_env_file"
unset enumeration_secret
chmod 600 "$enumeration_env_file"

registry="${IMAGE_URI%%/*}"
aws ecr get-login-password --region "$AWS_REGION" \
  | docker login --username AWS --password-stdin "$registry" >/dev/null
docker pull "$IMAGE_URI" >/dev/null

image_revision="$(docker image inspect "$IMAGE_URI" \
  --format '{{ index .Config.Labels "org.opencontainers.image.revision" }}')"
[[ "$image_revision" == "$SOURCE_SHA" ]] || fail "Image revision label does not match the requested source revision."

if container_exists "$candidate"; then
  docker rm --force "$candidate" >/dev/null
fi

log "Starting the candidate in the inactive slot."
docker run --detach \
  --name "$candidate" \
  --restart unless-stopped \
  --network "$DOCKER_NETWORK" \
  --env-file "$runtime_env_file" \
  --env-file "$enumeration_env_file" \
  --log-driver local \
  --log-opt max-size=10m \
  --log-opt max-file=3 \
  --label "io.palladin.stage.source-sha=$SOURCE_SHA" \
  "$IMAGE_URI" >/dev/null
candidate_started=true

candidate_healthy=false
for _ in $(seq 1 60); do
  if ! container_is_running "$candidate"; then
    fail "Candidate exited before becoming healthy."
  fi

  if docker exec "$CADDY_CONTAINER" wget -q -T 3 -O - \
    --header 'Host: api.stage.palladin.io' \
    --header 'X-Forwarded-Proto: https' \
    "http://$candidate:8080/api/health" 2>/dev/null \
    | grep -q '"status":"Healthy"'; then
    candidate_healthy=true
    break
  fi
  sleep 2
done
[[ "$candidate_healthy" == true ]] || fail "Candidate did not become healthy within two minutes."

log "Candidate is healthy; switching the Caddy upstream."
sed -E "s#^([[:space:]]*reverse_proxy[[:space:]]+)[^[:space:]]+#\\1${candidate}:8080#" \
  "$workdir/Caddyfile.before" > "$workdir/Caddyfile.candidate"
cp "$workdir/Caddyfile.candidate" "$CADDYFILE"
caddy_switched=true
docker exec "$CADDY_CONTAINER" caddy validate --config /etc/caddy/Caddyfile >/dev/null
docker exec "$CADDY_CONTAINER" caddy reload --config /etc/caddy/Caddyfile >/dev/null

public_healthy=false
for _ in $(seq 1 30); do
  if curl --fail --silent --show-error --max-time 5 "$PUBLIC_HEALTH_URL" \
    | grep -q '"status":"Healthy"'; then
    public_healthy=true
    break
  fi
  sleep 2
done
[[ "$public_healthy" == true ]] || fail "Public stage health did not recover after the switch."

# Catch immediate crash loops after a superficially successful first response.
sleep 10
container_is_running "$candidate" || fail "Candidate stopped during the post-switch stability window."
curl --fail --silent --show-error --max-time 5 "$PUBLIC_HEALTH_URL" \
  | grep -q '"status":"Healthy"' \
  || fail "Public stage health failed during the post-switch stability window."

install -d -m 700 "$DEPLOYMENT_DIRECTORY"
deployment_record="$workdir/current"
printf 'source_sha=%s\nimage_uri=%s\nupstream=%s\ndeployed_at=%s\n' \
  "$SOURCE_SHA" "$IMAGE_URI" "$candidate" "$(date -u +%Y-%m-%dT%H:%M:%SZ)" \
  > "$deployment_record"
chmod 600 "$deployment_record"
mv "$deployment_record" "$DEPLOYMENT_DIRECTORY/current"

deployment_complete=true
candidate_started=false
if ! docker stop --time 30 "$old_upstream" >/dev/null; then
  log "Deployment is healthy, but the previous slot could not be stopped."
fi
if ! docker image prune --force >/dev/null; then
  log "Deployment is healthy, but unused image cleanup failed."
fi

log "Deployment completed: source $(short_identifier "$SOURCE_SHA"), upstream $candidate."
