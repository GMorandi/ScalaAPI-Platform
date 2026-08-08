#!/usr/bin/env bash
set -Eeuo pipefail

stack_dir="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
compose_file="$stack_dir/docker-compose.yml"
project="${SMOKE_PROJECT_NAME:-scalaapi-smoke-$$}"

if [[ ! "$project" =~ ^[a-z0-9][a-z0-9_-]*$ ]]; then
    echo "SMOKE_PROJECT_NAME must contain only lowercase letters, numbers, dashes, and underscores" >&2
    exit 2
fi

container_cli="${CONTAINER_CLI:-}"
if [[ -z "$container_cli" ]]; then
    if command -v docker >/dev/null 2>&1 && docker compose version >/dev/null 2>&1; then
        container_cli=docker
    elif command -v podman >/dev/null 2>&1 && podman compose version >/dev/null 2>&1; then
        container_cli=podman
    else
        echo "Docker Compose or Podman Compose is required" >&2
        exit 2
    fi
fi
if ! command -v "$container_cli" >/dev/null 2>&1; then
    echo "Container CLI '$container_cli' was not found" >&2
    exit 2
fi
for command_name in curl jq; do
    if ! command -v "$command_name" >/dev/null 2>&1; then
        echo "$command_name is required" >&2
        exit 2
    fi
done

compose() {
    "$container_cli" compose --project-name "$project" --file "$compose_file" "$@"
}

service_container_id() {
    local service=$1
    local container_id
    container_id="$($container_cli ps --all \
        --filter "label=com.docker.compose.project=$project" \
        --filter "label=com.docker.compose.service=$service" \
        --format '{{.ID}}' | tr -d '\r')"
    if [[ ! "$container_id" =~ ^[a-f0-9]{12,64}$ ]]; then
        echo "Expected one container ID for service '$service', got '$container_id'" >&2
        return 1
    fi
    printf '%s\n' "$container_id"
}

recreate_service() {
    local service=$1
    local container_before
    local container_after
    container_before="$(service_container_id "$service")"
    compose up --detach --no-deps --force-recreate --no-build "$service" >/dev/null
    container_after="$(service_container_id "$service")"
    if [[ "$container_after" == "$container_before" ]]; then
        echo "Service '$service' did not receive a replacement container" >&2
        return 1
    fi
}

cleanup() {
    local status=$?
    set +e
    if (( status != 0 )); then
        echo "Smoke test failed; final container state:" >&2
        compose ps >&2
        compose logs --tail 200 >&2
    fi
    if [[ "${KEEP_STACK:-0}" == "1" ]]; then
        echo "Keeping Compose project '$project' (KEEP_STACK=1)" >&2
    else
        compose down --volumes --remove-orphans >/dev/null 2>&1
    fi
}
trap cleanup EXIT

suffix="${project//[^a-zA-Z0-9]/}"
export POSTGRES_DB="platform"
export POSTGRES_USER="platform"
export POSTGRES_PASSWORD="smoke-postgres-${suffix}-password"
export JWT_KEY="smoke-jwt-${suffix}-012345678901234567890123456789"
export ADMIN_USERNAME="admin@scalaapi.test"
export ADMIN_PASSWORD="smoke-admin-${suffix}-password"
export SECURITY_MASTER_KEY="MDEyMzQ1Njc4OWFiY2RlZjAxMjM0NTY3ODlhYmNkZWY="
export GARNET_PASSWORD="smoke-garnet-${suffix}-password"
export GARNET_TLS="false"
export OBJECT_STORAGE_ACCESS_KEY="smokeplatform"
export OBJECT_STORAGE_SECRET_KEY="smoke-object-${suffix}-password"
export OBJECT_STORAGE_BUCKET="scalaapi-smoke-media"
export OBJECT_STORAGE_PORT="${SMOKE_OBJECT_STORAGE_PORT:-29000}"
export OBJECT_STORAGE_CONSOLE_PORT="${SMOKE_OBJECT_STORAGE_CONSOLE_PORT:-29001}"
export OBJECT_STORAGE_PUBLIC_ENDPOINT="http://127.0.0.1:${OBJECT_STORAGE_PORT}"
export GATEWAY_PORT="${SMOKE_GATEWAY_PORT:-28080}"
export ADMIN_WEB_PORT="${SMOKE_ADMIN_WEB_PORT:-23000}"
export GATEWAY_CORES="${SMOKE_GATEWAY_CORES:-2}"

gateway_url="http://127.0.0.1:${GATEWAY_PORT}"
user_email="smoke-${suffix}@scalaapi.test"
user_password="smoke-user-${suffix}-password"
chat_request_id="smoke-chat-${suffix}"
chat_idempotency_key="smoke-chat-idem-${suffix}"
platform_restart_request_id="smoke-platform-restart-${suffix}"
platform_restart_idempotency_key="smoke-platform-restart-idem-${suffix}"
gateway_restart_request_id="smoke-gateway-restart-${suffix}"
gateway_restart_idempotency_key="smoke-gateway-restart-idem-${suffix}"
fault_request_prefix="smoke-fault-${suffix}"
media_idempotency_key="smoke-media-idem-${suffix}"
chat_price_version="smoke-chat-${suffix}-v1"
media_price_version="smoke-media-${suffix}-v1"

wait_for() {
    local description=$1
    local attempts=$2
    shift 2
    for ((attempt = 1; attempt <= attempts; attempt++)); do
        if "$@"; then
            return 0
        fi
        sleep 1
    done
    echo "Timed out waiting for $description" >&2
    return 1
}

db_query() {
    compose exec -T postgres psql --no-psqlrc --tuples-only --no-align \
        --set ON_ERROR_STOP=1 --username "$POSTGRES_USER" --dbname "$POSTGRES_DB" \
        --command "$1" | tr -d '\r'
}

admin_request() {
    local method=$1
    local path=$2
    local body=${3:-}
    local token=${4:-}
    local arguments=(-fsS -X "$method" -H "Content-Type: application/json")
    if [[ -n "$token" ]]; then
        arguments+=(-H "Authorization: Bearer $token")
    fi
    if [[ -n "$body" ]]; then
        arguments+=(--data "$body")
    fi
    compose exec -T admin-api curl "${arguments[@]}" "http://127.0.0.1:5001$path"
}

assert_equals() {
    local expected=$1
    local actual=$2
    local description=$3
    if [[ "$actual" != "$expected" ]]; then
        echo "$description: expected '$expected', got '$actual'" >&2
        return 1
    fi
}

create_api_key() {
    local group_id=$1
    local response
    response="$(admin_request POST /admin/apikeys/ \
        "$(jq -cn --argjson user "$user_id" --argjson group "$group_id" \
            '{userId:$user,groupId:$group,quota:100,expiresAt:null,ipWhitelist:[],ipBlacklist:[],rateLimit5h:0,rateLimit1d:0,rateLimit7d:0}')" \
        "$admin_token")"
    jq -er '.key' <<<"$response"
}

run_chat_fault() {
    local scenario=$1
    local scenario_api_key=$2
    local expected_status=$3
    local expected_error_type=$4
    local client_timeout=$5
    local request_id="${fault_request_prefix}-${scenario}"
    local idempotency_key="${request_id}-idem"
    local request_body
    local response
    local response_body
    local response_status
    local fault_state
    local lease_count

    request_body="$(jq -cn --arg scenario "$scenario" \
        '{model:"gpt-4o",messages:[{role:"user",content:"greenfield fault matrix"}],stream:false,user:("scalaapi-mock:" + $scenario)}')"
    response="$(curl -sS --max-time "$client_timeout" --write-out $'\n%{http_code}' \
        "$gateway_url/v1/chat/completions" \
        -H "Authorization: Bearer $scenario_api_key" \
        -H "Content-Type: application/json" \
        -H "X-Request-ID: $request_id" \
        -H "Idempotency-Key: $idempotency_key" \
        --data "$request_body")"
    response_status="${response##*$'\n'}"
    response_body="${response%$'\n'*}"
    assert_equals "$expected_status" "$response_status" \
        "Provider $scenario response status"
    if [[ "$expected_error_type" != "-" ]]; then
        jq -e --arg expected "$expected_error_type" '.error.type == $expected' \
            <<<"$response_body" >/dev/null
    fi

    fault_state="$(db_query "
WITH target_leases AS (
  SELECT lease_token, request_id, status
  FROM request_leases
  WHERE request_id = '$request_id' OR request_id LIKE '$request_id:retry:%'
)
SELECT
  (SELECT count(*) FROM target_leases) || '|' ||
  (SELECT count(*) FROM target_leases WHERE status = 'aborted') || '|' ||
  (SELECT count(*) FROM balance_holds h JOIN target_leases l USING (lease_token)) || '|' ||
  (SELECT count(*) FROM balance_holds h JOIN target_leases l USING (lease_token) WHERE h.status = 'released') || '|' ||
  (SELECT count(*) FROM usage_events u JOIN target_leases l ON l.request_id = u.request_id) || '|' ||
  (SELECT count(*) FROM usage_logs u JOIN target_leases l ON l.request_id = u.request_id) || '|' ||
  (SELECT count(*) FROM balance_ledger b JOIN target_leases l USING (lease_token)) || '|' ||
  (SELECT count(*) FROM request_idempotency WHERE idempotency_key = '$idempotency_key' AND status = 'aborted');")"
    lease_count="${fault_state%%|*}"
    if (( lease_count < 1 )); then
        echo "Provider $scenario did not create a lease" >&2
        return 1
    fi
    assert_equals "$lease_count|$lease_count|$lease_count|$lease_count|0|0|0|1" \
        "$fault_state" "Provider $scenario terminal billing invariants"
    echo "PASS: Provider $scenario -> HTTP $response_status ($lease_count aborted leases, all holds released)"
}

echo "Starting isolated Compose project '$project'"
up_arguments=(up -d)
if [[ "${SMOKE_SKIP_BUILD:-0}" != "1" ]]; then
    up_arguments+=(--build)
fi
compose "${up_arguments[@]}"

wait_for "Gateway readiness" 180 curl -fsS "$gateway_url/ready" >/dev/null
wait_for "Admin API readiness" 60 compose exec -T admin-api \
    curl -fsS http://127.0.0.1:5001/ready >/dev/null

migration_count="$(db_query "SELECT count(*) FROM schema_migrations;")"
assert_equals "17" "$migration_count" "Applied migration count"
second_migration_output="$(compose run --rm migrate 2>&1)"
second_skip_count="$(grep -cE 'skip .+\.sql' <<<"$second_migration_output" || true)"
assert_equals "17" "$second_skip_count" "Idempotent migrator skip count"

login_response="$(admin_request POST /admin/auth/login \
    "$(jq -cn --arg username "$ADMIN_USERNAME" --arg password "$ADMIN_PASSWORD" \
        '{username:$username,password:$password}')")"
admin_token="$(jq -er '.token' <<<"$login_response")"

seed_response="$(admin_request POST /admin/seed/provider-mock-suite '{}' "$admin_token")"
openai_group_id="$(jq -er '.providers[] | select(.provider == "openai") | .group_id' \
    <<<"$seed_response")"
assert_equals "3" "$(jq -er '.providers | length' <<<"$seed_response")" \
    "Seeded provider count"

fault_seed_response="$(admin_request POST /admin/seed/provider-mock-fault-matrix '{}' \
    "$admin_token")"
fault_429_group_id="$(jq -er '.scenarios[] | select(.scenario == "429") | .group_id' \
    <<<"$fault_seed_response")"
fault_500_group_id="$(jq -er '.scenarios[] | select(.scenario == "500") | .group_id' \
    <<<"$fault_seed_response")"
fault_timeout_group_id="$(jq -er '.scenarios[] | select(.scenario == "timeout") | .group_id' \
    <<<"$fault_seed_response")"
fault_disconnect_group_id="$(jq -er '.scenarios[] | select(.scenario == "disconnect") | .group_id' \
    <<<"$fault_seed_response")"
fault_malformed_group_id="$(jq -er '.scenarios[] | select(.scenario == "malformed_usage") | .group_id' \
    <<<"$fault_seed_response")"
assert_equals "5" "$(jq -er '.scenarios | length' <<<"$fault_seed_response")" \
    "Seeded fault scenario count"

register_response="$(admin_request POST /auth/register \
    "$(jq -cn --arg email "$user_email" --arg password "$user_password" \
        '{email:$email,password:$password,displayName:"Compose smoke"}')")"
user_id="$(jq -er '.id' <<<"$register_response")"

allowed_groups="$(jq -cn \
    --argjson openai "$openai_group_id" \
    --argjson fault429 "$fault_429_group_id" \
    --argjson fault500 "$fault_500_group_id" \
    --argjson timeout "$fault_timeout_group_id" \
    --argjson disconnect "$fault_disconnect_group_id" \
    --argjson malformed "$fault_malformed_group_id" \
    '[$openai,$fault429,$fault500,$timeout,$disconnect,$malformed]')"
admin_request PUT "/admin/users/$user_id" \
    "$(jq -cn --argjson groups "$allowed_groups" \
        '{role:"user",balance:100,concurrency:4,rpmLimit:0,allowedGroups:$groups}')" \
    "$admin_token" >/dev/null

api_key="$(create_api_key "$openai_group_id")"
fault_429_api_key="$(create_api_key "$fault_429_group_id")"
fault_500_api_key="$(create_api_key "$fault_500_group_id")"
fault_timeout_api_key="$(create_api_key "$fault_timeout_group_id")"
fault_disconnect_api_key="$(create_api_key "$fault_disconnect_group_id")"
fault_malformed_api_key="$(create_api_key "$fault_malformed_group_id")"

effective_from="1970-01-01T00:00:00Z"
admin_request POST /admin/pricing/versions \
    "$(jq -cn --arg version "$chat_price_version" --arg model gpt-4o \
        --arg from "$effective_from" \
        '{version:$version,model:$model,inputUsdPerMillion:2.5,outputUsdPerMillion:10,cacheReadUsdPerMillion:0,cacheWriteUsdPerMillion:1.25,effectiveFrom:$from,effectiveUntil:null}')" \
    "$admin_token" >/dev/null
admin_request POST /admin/pricing/versions \
    "$(jq -cn --arg version "$media_price_version" --arg model mock-image-1 \
        --arg from "$effective_from" \
        '{version:$version,model:$model,inputUsdPerMillion:0,outputUsdPerMillion:0,cacheReadUsdPerMillion:0,cacheWriteUsdPerMillion:0,effectiveFrom:$from,effectiveUntil:null}')" \
    "$admin_token" >/dev/null

sleep 6
chat_body='{"model":"gpt-4o","messages":[{"role":"user","content":"greenfield compose smoke"}],"stream":false}'
chat_response="$(curl -fsS "$gateway_url/v1/chat/completions" \
    -H "Authorization: Bearer $api_key" -H "Content-Type: application/json" \
    -H "X-Request-ID: $chat_request_id" -H "Idempotency-Key: $chat_idempotency_key" \
    --data "$chat_body")"
jq -e '(.choices | length > 0) and (.usage.total_tokens > 0)' \
    <<<"$chat_response" >/dev/null

chat_settled() {
    [[ "$(db_query "SELECT count(*) FROM request_leases WHERE request_id = '$chat_request_id' AND status = 'completed' AND final_cost_usd > 0 AND pricing_version = '$chat_price_version';")" == "1" ]]
}
wait_for "chat settlement" 30 chat_settled

chat_replay="$(curl -fsS "$gateway_url/v1/chat/completions" \
    -H "Authorization: Bearer $api_key" -H "Content-Type: application/json" \
    -H "X-Request-ID: ${chat_request_id}-replay" -H "Idempotency-Key: $chat_idempotency_key" \
    --data "$chat_body")"
assert_equals "$(jq -cS . <<<"$chat_response")" "$(jq -cS . <<<"$chat_replay")" \
    "Idempotent chat replay body"
assert_equals "1" "$(db_query "SELECT count(*) FROM request_idempotency WHERE idempotency_key = '$chat_idempotency_key';")" \
    "Idempotent chat lease count"

echo "Restarting Platform and verifying a new billable request"
recreate_service platform-silo
wait_for "Platform readiness after restart" 90 compose exec -T platform-silo \
    curl -fsS http://127.0.0.1:5000/ready >/dev/null

platform_restart_response="$(curl -fsS "$gateway_url/v1/chat/completions" \
    -H "Authorization: Bearer $api_key" -H "Content-Type: application/json" \
    -H "X-Request-ID: $platform_restart_request_id" \
    -H "Idempotency-Key: $platform_restart_idempotency_key" \
    --data "$chat_body")"
jq -e '(.choices | length > 0) and (.usage.total_tokens > 0)' \
    <<<"$platform_restart_response" >/dev/null

platform_restart_settled() {
    [[ "$(db_query "SELECT count(*) FROM request_leases WHERE request_id = '$platform_restart_request_id' AND status = 'completed' AND final_cost_usd > 0 AND pricing_version = '$chat_price_version';")" == "1" ]]
}
wait_for "post-Platform-restart settlement" 30 platform_restart_settled

echo "Restarting Gateway and verifying a new billable request"
recreate_service gateway
wait_for "Gateway readiness after restart" 90 curl -fsS "$gateway_url/ready" >/dev/null

gateway_restart_response="$(curl -fsS "$gateway_url/v1/chat/completions" \
    -H "Authorization: Bearer $api_key" -H "Content-Type: application/json" \
    -H "X-Request-ID: $gateway_restart_request_id" \
    -H "Idempotency-Key: $gateway_restart_idempotency_key" \
    --data "$chat_body")"
jq -e '(.choices | length > 0) and (.usage.total_tokens > 0)' \
    <<<"$gateway_restart_response" >/dev/null

gateway_restart_settled() {
    [[ "$(db_query "SELECT count(*) FROM request_leases WHERE request_id = '$gateway_restart_request_id' AND status = 'completed' AND final_cost_usd > 0 AND pricing_version = '$chat_price_version';")" == "1" ]]
}
wait_for "post-Gateway-restart settlement" 30 gateway_restart_settled

echo "Running isolated Provider failure matrix"
run_chat_fault "500" "$fault_500_api_key" "503" "provider_unavailable" "20"
run_chat_fault "429" "$fault_429_api_key" "503" "provider_unavailable" "20"
run_chat_fault "malformed_usage" "$fault_malformed_api_key" "502" "provider_error" "20"
run_chat_fault "disconnect" "$fault_disconnect_api_key" "503" "provider_unavailable" "20"
run_chat_fault "timeout" "$fault_timeout_api_key" "502" "-" "40"

media_response="$(curl -fsS "$gateway_url/v1/images/generations/async" \
    -H "Authorization: Bearer $api_key" -H "Content-Type: application/json" \
    -H "Idempotency-Key: $media_idempotency_key" \
    --data '{"model":"mock-image-1","prompt":"greenfield object storage smoke","size":"1024x1024"}')"
media_id="$(jq -er '.id' <<<"$media_response")"

media_result=""
media_stored() {
    media_result="$(curl -fsS "$gateway_url/v1/images/tasks/$media_id" \
        -H "Authorization: Bearer $api_key")" || return 1
    [[ "$(jq -r '.status' <<<"$media_result")" == "succeeded" ]] \
        && [[ "$(jq -r '.url // empty' <<<"$media_result")" == http://* ]]
}
wait_for "media object persistence" 45 media_stored
media_url="$(jq -er '.url' <<<"$media_result")"
media_size="$(curl -fsSL "$media_url" | wc -c | tr -d ' ')"
if (( media_size <= 0 )); then
    echo "Downloaded media object was empty" >&2
    exit 1
fi
assert_equals "stored" \
    "$(db_query "SELECT object_status FROM media_operations WHERE operation_id = '$media_id';")" \
    "Media object status"

terminal_state="$(db_query "
SELECT
  (SELECT count(*) FROM request_leases WHERE status = 'active') || '|' ||
  (SELECT count(*) FROM balance_holds WHERE status = 'active') || '|' ||
  (SELECT count(*) FROM usage_outbox WHERE processed_at IS NULL) || '|' ||
  (SELECT count(*) FROM usage_outbox WHERE dead_lettered_at IS NOT NULL) || '|' ||
  (SELECT count(*) FROM balance_holds h JOIN request_leases l ON l.lease_token = h.lease_token WHERE l.request_id = '$chat_request_id' AND h.status = 'committed') || '|' ||
  (SELECT count(*) FROM usage_events WHERE request_id = '$chat_request_id') || '|' ||
  (SELECT count(*) FROM usage_logs WHERE request_id = '$chat_request_id') || '|' ||
  (SELECT count(*) FROM balance_ledger l JOIN request_leases r ON r.lease_token = l.lease_token WHERE r.request_id = '$chat_request_id' AND l.entry_type = 'usage_debit' AND l.amount = -r.final_cost_usd);")"
assert_equals "0|0|0|0|1|1|1|1" "$terminal_state" "Terminal billing invariants"

restart_state="$(db_query "
SELECT
  (SELECT count(*) FROM request_leases WHERE request_id IN ('$platform_restart_request_id', '$gateway_restart_request_id') AND status = 'completed') || '|' ||
  (SELECT count(*) FROM balance_holds h JOIN request_leases l ON l.lease_token = h.lease_token WHERE l.request_id IN ('$platform_restart_request_id', '$gateway_restart_request_id') AND h.status = 'committed') || '|' ||
  (SELECT count(*) FROM usage_events WHERE request_id IN ('$platform_restart_request_id', '$gateway_restart_request_id')) || '|' ||
  (SELECT count(*) FROM usage_logs WHERE request_id IN ('$platform_restart_request_id', '$gateway_restart_request_id')) || '|' ||
  (SELECT count(*) FROM balance_ledger l JOIN request_leases r ON r.lease_token = l.lease_token WHERE r.request_id IN ('$platform_restart_request_id', '$gateway_restart_request_id') AND l.entry_type = 'usage_debit' AND l.amount = -r.final_cost_usd);")"
assert_equals "2|2|2|2|2" "$restart_state" \
    "Platform/Gateway restart billing invariants"

gateway_backlog() {
    [[ "$(curl -fsS "$gateway_url/metrics" | awk '$1 == "gateway_usage_outbox_backlog" {print $2}')" == "0" ]]
}
wait_for "Gateway usage outbox drain" 30 gateway_backlog

garnet_probe="$(compose exec -T garnet-health sh -c '
pass="$GARNET_PASSWORD"; len=$(printf %s "$pass" | wc -c)
{ printf "*2\r\n\$4\r\nAUTH\r\n\$%s\r\n%s\r\n" "$len" "$pass"; printf "*1\r\n\$4\r\nPING\r\n"; } | nc -w 2 garnet 6379
' | tr -d '\r')"
if [[ "$garnet_probe" != *PONG* ]]; then
    echo "Authenticated Garnet PING did not return PONG" >&2
    exit 1
fi

echo "PASS: 17 empty-volume migrations and second-run idempotency"
echo "PASS: Garnet-authenticated Gateway -> Platform -> Provider mock request"
echo "PASS: terminal lease, hold, usage, ledger, and outbox invariants"
echo "PASS: idempotent response replay without duplicate billing"
echo "PASS: new billable requests after Platform and Gateway restarts"
echo "PASS: isolated 429, 500, malformed-usage, disconnect, and timeout billing failures"
echo "PASS: S3-compatible bucket bootstrap, object persistence, and signed download ($media_size bytes)"
