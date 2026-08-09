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
for command_name in curl jq python3; do
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
export PROVIDER_CREDENTIALS_ALLOW_INSECURE="true"
export INTERNAL_RECONCILIATION_TOKEN="smoke-reconciliation-${suffix}-token"
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
export USER_WEB_PORT="${SMOKE_USER_WEB_PORT:-23001}"
export GATEWAY_CORES="${SMOKE_GATEWAY_CORES:-2}"
if [[ -n "${GATEWAY_FAULT_HOOK:-}${PLATFORM_FAULT_HOOK:-}" &&
      -z "${DISPATCH_LEASE_TTL_SECONDS:-}" ]]; then
    export DISPATCH_LEASE_TTL_SECONDS=15
fi
export DISPATCH_LEASE_TTL_SECONDS="${DISPATCH_LEASE_TTL_SECONDS:-360}"
if [[ -n "${PLATFORM_FAULT_HOOK:-}" ]]; then
    export ORLEANS_SINGLE_SILO_RECOVERY=true
fi

gateway_url="http://127.0.0.1:${GATEWAY_PORT}"
user_web_url="http://127.0.0.1:${USER_WEB_PORT}"
user_email="smoke-${suffix}@scalaapi.test"
user_password="smoke-user-${suffix}-password"
chat_request_id="smoke-chat-${suffix}"
chat_idempotency_key="smoke-chat-idem-${suffix}"
embedding_request_id="smoke-embeddings-${suffix}"
embedding_idempotency_key="smoke-embeddings-idem-${suffix}"
embedding_base64_request_id="smoke-embeddings-base64-${suffix}"
embedding_base64_idempotency_key="smoke-embeddings-base64-idem-${suffix}"
embedding_invalid_request_id="smoke-embeddings-invalid-${suffix}"
embedding_invalid_idempotency_key="smoke-embeddings-invalid-idem-${suffix}"
concurrent_request_id="smoke-chat-concurrent-${suffix}"
concurrent_idempotency_key="smoke-chat-concurrent-idem-${suffix}"
expired_key_request_id="smoke-expired-key-${suffix}"
platform_restart_request_id="smoke-platform-restart-${suffix}"
platform_restart_idempotency_key="smoke-platform-restart-idem-${suffix}"
gateway_restart_request_id="smoke-gateway-restart-${suffix}"
gateway_restart_idempotency_key="smoke-gateway-restart-idem-${suffix}"
gateway_fault_request_id="smoke-gateway-fault-${suffix}"
gateway_fault_idempotency_key="smoke-gateway-fault-idem-${suffix}"
platform_dispatch_fault_request_id="smoke-platform-dispatch-fault-${suffix}"
platform_dispatch_fault_idempotency_key="smoke-platform-dispatch-fault-idem-${suffix}"
platform_dispatch_retry_request_id="smoke-platform-dispatch-retry-${suffix}"
platform_dispatch_retry_idempotency_key="smoke-platform-dispatch-retry-idem-${suffix}"
realtime_request_id="smoke-realtime-${suffix}"
realtime_idempotency_key="smoke-realtime-idem-${suffix}"
fault_request_prefix="smoke-fault-${suffix}"
media_idempotency_key="smoke-media-idem-${suffix}"
chat_price_version="smoke-chat-${suffix}-v1"
embedding_price_version="smoke-embeddings-${suffix}-v1"
media_price_version="smoke-media-${suffix}-v1"
balance_idempotency_key="smoke-balance-${suffix}"
gateway_hook_unknown_incidents=0
gateway_hook_safe_expiry=0
platform_dispatch_fault_safe_expiry=0
platform_dispatch_retry=0
platform_worker_reclaim=0

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
    local idempotency_key=${5:-}
    local arguments=(-fsS -X "$method" -H "Content-Type: application/json")
    if [[ -n "$token" ]]; then
        arguments+=(-H "Authorization: Bearer $token")
    fi
    if [[ -n "$idempotency_key" ]]; then
        arguments+=(-H "Idempotency-Key: $idempotency_key")
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

assert_one_of() {
    local expected_values=$1
    local actual=$2
    local description=$3
    local expected
    IFS='|' read -r -a expected <<<"$expected_values"
    for value in "${expected[@]}"; do
        if [[ "$actual" == "$value" ]]; then
            return 0
        fi
    done
    echo "$description: expected one of '$expected_values', got '$actual'" >&2
    return 1
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
    local expected_terminal=$6
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
    assert_one_of "$expected_status" "$response_status" \
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
  (SELECT count(*) FROM target_leases WHERE status = 'reconciliation_needed') || '|' ||
  (SELECT count(*) FROM balance_holds h JOIN target_leases l USING (lease_token)) || '|' ||
  (SELECT count(*) FROM balance_holds h JOIN target_leases l USING (lease_token) WHERE h.status = 'released') || '|' ||
  (SELECT count(*) FROM balance_holds h JOIN target_leases l USING (lease_token) WHERE h.status = 'active') || '|' ||
  (SELECT count(*) FROM usage_events u JOIN target_leases l ON l.request_id = u.request_id) || '|' ||
  (SELECT count(*) FROM usage_logs u JOIN target_leases l ON l.request_id = u.request_id) || '|' ||
  (SELECT count(*) FROM balance_ledger b JOIN target_leases l USING (lease_token)) || '|' ||
  (SELECT count(*) FROM request_idempotency WHERE idempotency_key = '$idempotency_key' AND status = '$expected_terminal');")"
    lease_count="${fault_state%%|*}"
    if (( lease_count < 1 )); then
        echo "Provider $scenario did not create a lease" >&2
        return 1
    fi
    if [[ "$expected_terminal" == "aborted" ]]; then
        assert_equals "$lease_count|$lease_count|0|$lease_count|$lease_count|0|0|0|0|1" \
            "$fault_state" "Provider $scenario no-charge billing invariants"
        echo "PASS: Provider $scenario -> HTTP $response_status ($lease_count rejected leases, holds released)"
    else
        assert_equals "$lease_count|0|$lease_count|$lease_count|0|$lease_count|0|0|0|1" \
            "$fault_state" "Provider $scenario unknown-charge billing invariants"
        echo "PASS: Provider $scenario -> HTTP $response_status ($lease_count unknown-charge leases, holds retained)"
    fi
}

run_chat_stream_fault() {
    local scenario=$1
    local scenario_api_key=$2
    local curl_timeout=${3:-25}
    local request_id="${fault_request_prefix}-stream-${scenario}"
    local idempotency_key="${request_id}-idem"
    local request_body
    local response
    local response_body
    local response_status
    local fault_state
    local lease_count

    request_body="$(jq -cn --arg scenario "$scenario" \
        '{model:"gpt-4o",messages:[{role:"user",content:"greenfield streaming fault matrix"}],stream:true,user:("scalaapi-mock:" + $scenario)}')"
    set +e
    response="$(curl -sS --max-time "$curl_timeout" --write-out $'\n%{http_code}' \
        "$gateway_url/v1/chat/completions" \
        -H "Authorization: Bearer $scenario_api_key" \
        -H "Content-Type: application/json" \
        -H "X-Request-ID: $request_id" \
        -H "Idempotency-Key: $idempotency_key" \
        --data "$request_body")"
    set -e
    response_status="${response##*$'\n'}"
    response_body="${response%$'\n'*}"
    [[ -n "$response_status" ]] || response_status=000
    # Once SSE headers have reached the client, a truncated stream may retain
    # the original 200 status even though the lease is deliberately unknown.
    if [[ "$scenario" == "disconnect_before_output" ]]; then
        # The Gateway may expose a transport-level close (curl 000) while its
        # upstream client waits out the truncated body read; both outcomes
        # preserve the same unknown-charge lease semantics.
        assert_equals "503" "$response_status" \
            "Provider streaming $scenario availability response status"
    elif [[ "$scenario" == "disconnect" ]]; then
        # Once partial SSE bytes have reached the client, curl may observe a
        # clean 200 stream, a normalized 503/502, or a transport-level 000 close.
        assert_one_of "000|200|502|503" "$response_status" \
            "Provider streaming $scenario availability response status"
    else
        assert_one_of "000|200|499|502|503" "$response_status" \
            "Provider streaming $scenario response status"
    fi
    if [[ "$scenario" == "disconnect_before_output" ]]; then
        jq -e '.error.type == "provider_unavailable"' <<<"$response_body" >/dev/null
    elif [[ "$scenario" == "timeout" || "$scenario" == "invalid_content_type" ]]; then
        jq -e '.error.type == "provider_protocol_error"' <<<"$response_body" >/dev/null
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
  (SELECT count(*) FROM target_leases WHERE status = 'reconciliation_needed') || '|' ||
  (SELECT count(*) FROM balance_holds h JOIN target_leases l USING (lease_token)) || '|' ||
  (SELECT count(*) FROM balance_holds h JOIN target_leases l USING (lease_token) WHERE h.status = 'released') || '|' ||
  (SELECT count(*) FROM balance_holds h JOIN target_leases l USING (lease_token) WHERE h.status = 'active') || '|' ||
  (SELECT count(*) FROM usage_events u JOIN target_leases l ON l.request_id = u.request_id) || '|' ||
  (SELECT count(*) FROM usage_logs u JOIN target_leases l ON l.request_id = u.request_id) || '|' ||
  (SELECT count(*) FROM balance_ledger b JOIN target_leases l USING (lease_token)) || '|' ||
  (SELECT count(*) FROM request_idempotency WHERE idempotency_key = '$idempotency_key' AND status = 'reconciliation_needed');")"
    lease_count="${fault_state%%|*}"
    if (( lease_count < 1 )); then
        echo "Provider streaming $scenario did not create a lease" >&2
        return 1
    fi
    assert_equals "$lease_count|0|$lease_count|$lease_count|0|$lease_count|0|0|0|1" \
        "$fault_state" "Provider streaming $scenario unknown-charge billing invariants"
    echo "PASS: Provider streaming $scenario -> HTTP $response_status ($lease_count unknown-charge leases, holds retained)"
    : "$response_body"
}

run_chat_stream_late_usage() {
    local scenario=$1
    local scenario_api_key=$2
    local curl_timeout=${3:-25}
    local request_id="${fault_request_prefix}-stream-${scenario}"
    local idempotency_key="${request_id}-idem"
    local request_body
    local response
    local response_status
    local fault_state
    local lease_count

    request_body="$(jq -cn --arg scenario "$scenario" \
        '{model:"gpt-4o",messages:[{role:"user",content:"greenfield late usage matrix"}],stream:true,user:("scalaapi-mock:" + $scenario)}')"
    set +e
    response="$(curl -sS --max-time "$curl_timeout" --write-out $'\n%{http_code}' \
        "$gateway_url/v1/chat/completions" \
        -H "Authorization: Bearer $scenario_api_key" \
        -H "Content-Type: application/json" \
        -H "X-Request-ID: $request_id" \
        -H "Idempotency-Key: $idempotency_key" \
        --data "$request_body")"
    set -e
    response_status="${response##*$'\n'}"
    [[ -n "$response_status" ]] || response_status=000
    assert_one_of "000|200|503" "$response_status" \
        "Provider streaming $scenario truncated response status"

    late_usage_settled() {
        [[ "$(db_query "SELECT count(*) FROM request_leases WHERE request_id = '$request_id' AND status = 'completed';")" == "1" ]]
    }
    wait_for "Provider $scenario late usage settlement" 30 late_usage_settled
    fault_state="$(db_query "
WITH target_leases AS (
  SELECT lease_token, request_id, status
  FROM request_leases
  WHERE request_id = '$request_id'
)
SELECT
  (SELECT count(*) FROM target_leases) || '|' ||
  (SELECT count(*) FROM target_leases WHERE status = 'completed') || '|' ||
  (SELECT count(*) FROM target_leases WHERE status = 'reconciliation_needed') || '|' ||
  (SELECT count(*) FROM balance_holds h JOIN target_leases l USING (lease_token)) || '|' ||
  (SELECT count(*) FROM balance_holds h JOIN target_leases l USING (lease_token) WHERE h.status = 'committed') || '|' ||
  (SELECT count(*) FROM balance_holds h JOIN target_leases l USING (lease_token) WHERE h.status = 'active') || '|' ||
  (SELECT count(*) FROM usage_events u JOIN target_leases l ON l.request_id = u.request_id) || '|' ||
  (SELECT count(*) FROM usage_logs u JOIN target_leases l ON l.request_id = u.request_id) || '|' ||
  (SELECT count(*) FROM balance_ledger b JOIN target_leases l USING (lease_token)) || '|' ||
  (SELECT count(*) FROM request_idempotency WHERE idempotency_key = '$idempotency_key' AND status = 'completed');")"
    lease_count="${fault_state%%|*}"
    if (( lease_count < 1 )); then
        echo "Provider $scenario did not create a lease" >&2
        return 1
    fi
    assert_equals "$lease_count|$lease_count|0|$lease_count|$lease_count|0|$lease_count|$lease_count|$lease_count|1" \
        "$fault_state" "Provider $scenario late usage billing invariants"
    echo "PASS: Provider $scenario -> HTTP $response_status (usage settled after stream truncation)"
}

run_chat_stream_rejection() {
    local scenario=$1
    local scenario_api_key=$2
    local request_id="${fault_request_prefix}-stream-${scenario}"
    local idempotency_key="${request_id}-idem"
    local request_body
    local response
    local response_body
    local response_status
    local fault_state
    local lease_count

    request_body="$(jq -cn --arg scenario "$scenario" \
        '{model:"gpt-4o",messages:[{role:"user",content:"greenfield streaming rejection matrix"}],stream:true,user:("scalaapi-mock:" + $scenario)}')"
    response="$(curl -sS --max-time 40 --write-out $'\n%{http_code}' \
        "$gateway_url/v1/chat/completions" \
        -H "Authorization: Bearer $scenario_api_key" \
        -H "Content-Type: application/json" \
        -H "X-Request-ID: $request_id" \
        -H "Idempotency-Key: $idempotency_key" \
        --data "$request_body")"
    response_status="${response##*$'\n'}"
    response_body="${response%$'\n'*}"
    assert_equals "503" "$response_status" \
        "Provider streaming $scenario rejection response status"
    jq -e '.error.type == "provider_unavailable"' <<<"$response_body" >/dev/null

    fault_state="$(db_query "
WITH target_leases AS (
  SELECT lease_token, request_id, status
  FROM request_leases
  WHERE request_id = '$request_id' OR request_id LIKE '$request_id:retry:%'
)
SELECT
  (SELECT count(*) FROM target_leases) || '|' ||
  (SELECT count(*) FROM target_leases WHERE status = 'aborted') || '|' ||
  (SELECT count(*) FROM target_leases WHERE status = 'reconciliation_needed') || '|' ||
  (SELECT count(*) FROM balance_holds h JOIN target_leases l USING (lease_token)) || '|' ||
  (SELECT count(*) FROM balance_holds h JOIN target_leases l USING (lease_token) WHERE h.status = 'released') || '|' ||
  (SELECT count(*) FROM balance_holds h JOIN target_leases l USING (lease_token) WHERE h.status = 'active') || '|' ||
  (SELECT count(*) FROM usage_events u JOIN target_leases l ON l.request_id = u.request_id) || '|' ||
  (SELECT count(*) FROM usage_logs u JOIN target_leases l ON l.request_id = u.request_id) || '|' ||
  (SELECT count(*) FROM balance_ledger b JOIN target_leases l USING (lease_token)) || '|' ||
  (SELECT count(*) FROM request_idempotency WHERE idempotency_key = '$idempotency_key' AND status = 'aborted');")"
    lease_count="${fault_state%%|*}"
    if (( lease_count < 1 )); then
        echo "Provider streaming $scenario did not create a lease" >&2
        return 1
    fi
    assert_equals "$lease_count|$lease_count|0|$lease_count|$lease_count|0|0|0|0|1" \
        "$fault_state" "Provider streaming $scenario no-charge billing invariants"
    echo "PASS: Provider streaming $scenario -> HTTP $response_status ($lease_count rejected leases, holds released)"
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
wait_for "User Web readiness" 60 curl -fsS "$user_web_url/" >/dev/null

migration_count="$(db_query "SELECT count(*) FROM schema_migrations;")"
assert_equals "29" "$migration_count" "Applied migration count"
second_migration_output="$(compose run --rm migrate 2>&1)"
second_skip_count="$(grep -cE 'skip .+\.sql' <<<"$second_migration_output" || true)"
assert_equals "29" "$second_skip_count" "Idempotent migrator skip count"

login_response="$(admin_request POST /admin/auth/login \
    "$(jq -cn --arg username "$ADMIN_USERNAME" --arg password "$ADMIN_PASSWORD" \
        '{username:$username,password:$password}')")"
admin_token="$(jq -er '.token' <<<"$login_response")"

seed_response="$(admin_request POST /admin/seed/provider-mock-suite '{}' "$admin_token")"
openai_group_id="$(jq -er '.providers[] | select(.provider == "openai") | .group_id' \
    <<<"$seed_response")"
openai_account_id="$(jq -er '.providers[] | select(.provider == "openai") | .account_id' \
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
fault_disconnect_stream_group_id="$(jq -er '.scenarios[] | select(.scenario == "disconnect_stream") | .group_id' \
    <<<"$fault_seed_response")"
fault_disconnect_after_usage_group_id="$(jq -er '.scenarios[] | select(.scenario == "disconnect_after_usage") | .group_id' \
    <<<"$fault_seed_response")"
fault_client_disconnect_group_id="$(jq -er '.scenarios[] | select(.scenario == "client_disconnect") | .group_id' \
    <<<"$fault_seed_response")"
fault_malformed_group_id="$(jq -er '.scenarios[] | select(.scenario == "malformed_usage") | .group_id' \
    <<<"$fault_seed_response")"
fault_invalid_content_type_group_id="$(jq -er '.scenarios[] | select(.scenario == "invalid_content_type") | .group_id' \
    <<<"$fault_seed_response")"
assert_equals "9" "$(jq -er '.scenarios | length' <<<"$fault_seed_response")" \
    "Seeded fault scenario count"

invalid_register_status="$(compose exec -T admin-api curl -sS -o /dev/null -w '%{http_code}' \
    -X POST -H 'Content-Type: application/json' \
    --data '{"email":"not-an-email","password":"long-enough-password"}' \
    http://127.0.0.1:5001/auth/register)"
assert_equals "400" "$invalid_register_status" "Invalid registration input rejection"
abuse_email="auth-abuse-${suffix}@scalaapi.test"
for attempt in $(seq 1 5); do
    failed_login_status="$(compose exec -T admin-api curl -sS -o /dev/null -w '%{http_code}' \
        -X POST -H 'Content-Type: application/json' \
        --data "$(jq -cn --arg email "$abuse_email" \
            '{email:$email,password:"wrong-password-for-throttle"}')" \
        http://127.0.0.1:5001/auth/login)"
    assert_equals "401" "$failed_login_status" "Failed login attempt $attempt"
done
login_throttled_status="$(compose exec -T admin-api curl -sS -o /dev/null -w '%{http_code}' \
    -X POST -H 'Content-Type: application/json' \
    --data "$(jq -cn --arg email "$abuse_email" \
        '{email:$email,password:"wrong-password-for-throttle"}')" \
    http://127.0.0.1:5001/auth/login)"
assert_equals "429" "$login_throttled_status" "Login abuse throttle"

register_response="$(admin_request POST /auth/register \
    "$(jq -cn --arg email "$user_email" --arg password "$user_password" \
        '{email:$email,password:$password,displayName:"Compose smoke"}')")"
user_id="$(jq -er '.id' <<<"$register_response")"

user_login_response="$(admin_request POST /auth/login \
    "$(jq -cn --arg email "$user_email" --arg password "$user_password" \
        '{email:$email,password:$password}')")"
user_access_token="$(jq -er '.token' <<<"$user_login_response")"
user_refresh_token="$(jq -er '.refresh_token' <<<"$user_login_response")"

oauth_redirect_uri="http://localhost:3000/oauth/callback"
oauth_start="$(admin_request GET "/auth/oauth/github/start?redirectUri=$(jq -rn --arg value "$oauth_redirect_uri" '$value|@uri')")"
oauth_authorization_url="$(jq -er '.authorizationUrl' <<<"$oauth_start")"
if [[ "$oauth_authorization_url" != http://provider-mock:8081/oauth/authorize\?* ]]; then
    echo "OAuth start did not use the configured Provider mock authorization endpoint" >&2
    exit 1
fi
oauth_location="$(compose exec -T admin-api curl -fsS -o /dev/null -w '%{redirect_url}' \
    "$oauth_authorization_url")"
oauth_callback="$(python3 - "$oauth_location" "$(jq -r '.codeVerifier' <<<"$oauth_start")" <<'PY'
import json
import sys
from urllib.parse import parse_qs, urlparse

query = parse_qs(urlparse(sys.argv[1]).query)
print(json.dumps({
    "provider": "github",
    "code": query["code"][0],
    "redirectUri": "http://localhost:3000/oauth/callback",
    "state": query["state"][0],
    "codeVerifier": sys.argv[2],
}))
PY
)"
oauth_callback_response="$(admin_request POST /auth/oauth/callback "$oauth_callback")"
assert_equals "oauth-user@example.test|github|mock-oauth-user" \
    "$(jq -r '.email' <<<"$oauth_callback_response")|$(db_query \
      "SELECT oauth_provider || '|' || oauth_id FROM user_accounts WHERE email = 'oauth-user@example.test';")" \
    "External OAuth mock exchange and account binding"
oauth_replay="$(compose exec -T admin-api curl -sS -X POST \
    -H 'Content-Type: application/json' --data "$oauth_callback" \
    -w $'\n%{http_code}' http://127.0.0.1:5001/auth/oauth/callback)"
assert_equals "oauth_state_replayed|400" \
    "$(jq -r '.error' <<<"${oauth_replay%$'\n'*}")|${oauth_replay##*$'\n'}" \
    "External OAuth state replay rejection"
echo "PASS: External OAuth mock authorization-code exchange, PKCE binding, account creation, and replay rejection"

user_refresh_response="$(admin_request POST /auth/refresh \
    "$(jq -cn --arg refresh "$user_refresh_token" '{refreshToken:$refresh}')")"
rotated_access_token="$(jq -er '.token' <<<"$user_refresh_response")"
rotated_refresh_token="$(jq -er '.refresh_token' <<<"$user_refresh_response")"
if [[ "$rotated_refresh_token" == "$user_refresh_token" ]]; then
    echo "Refresh rotation returned the original refresh token" >&2
    exit 1
fi
refresh_replay_status="$(compose exec -T admin-api curl -sS -o /dev/null -w '%{http_code}' \
    -X POST -H 'Content-Type: application/json' \
    --data "$(jq -cn --arg refresh "$user_refresh_token" '{refreshToken:$refresh}')" \
    http://127.0.0.1:5001/auth/refresh)"
assert_equals "401" "$refresh_replay_status" "Refresh-token replay rejection"
old_access_status="$(compose exec -T admin-api curl -sS -o /dev/null -w '%{http_code}' \
    -H "Authorization: Bearer $user_access_token" \
    http://127.0.0.1:5001/user/sessions)"
assert_equals "401" "$old_access_status" "Replaced access-token rejection"
new_access_status="$(compose exec -T admin-api curl -sS -o /dev/null -w '%{http_code}' \
    -H "Authorization: Bearer $rotated_access_token" \
    http://127.0.0.1:5001/user/sessions)"
assert_equals "200" "$new_access_status" "Rotated access-token acceptance"
logout_status="$(compose exec -T admin-api curl -sS -o /dev/null -w '%{http_code}' \
    -X POST -H "Authorization: Bearer $rotated_access_token" \
    http://127.0.0.1:5001/user/logout)"
assert_equals "204" "$logout_status" "User session logout"
logout_access_status="$(compose exec -T admin-api curl -sS -o /dev/null -w '%{http_code}' \
    -H "Authorization: Bearer $rotated_access_token" \
    http://127.0.0.1:5001/user/sessions)"
assert_equals "401" "$logout_access_status" "Logged-out access-token rejection"

allowed_groups="$(jq -cn \
    --argjson openai "$openai_group_id" \
    --argjson fault429 "$fault_429_group_id" \
    --argjson fault500 "$fault_500_group_id" \
    --argjson timeout "$fault_timeout_group_id" \
    --argjson disconnect "$fault_disconnect_group_id" \
    --argjson disconnectStream "$fault_disconnect_stream_group_id" \
    --argjson disconnectAfterUsage "$fault_disconnect_after_usage_group_id" \
    --argjson clientDisconnect "$fault_client_disconnect_group_id" \
    --argjson malformed "$fault_malformed_group_id" \
    --argjson invalidContentType "$fault_invalid_content_type_group_id" \
    '[$openai,$fault429,$fault500,$timeout,$disconnect,$disconnectStream,$disconnectAfterUsage,$clientDisconnect,$malformed,$invalidContentType]')"
admin_request PUT "/admin/users/$user_id" \
    "$(jq -cn --argjson groups "$allowed_groups" \
        '{role:"user",concurrency:4,rpmLimit:0,allowedGroups:$groups}')" \
    "$admin_token" >/dev/null

balance_body='{"delta":1000,"reason":"Initial smoke-test funding"}'
balance_response="$(admin_request POST "/admin/users/$user_id/balance" \
    "$balance_body" "$admin_token" "$balance_idempotency_key")"
assert_equals "true" "$(jq -er '.balance == 1000' <<<"$balance_response")" \
    "Administrative balance result"
assert_equals "false" "$(jq -er '.duplicate' <<<"$balance_response")" \
    "Administrative balance first-write marker"
assert_equals "1" "$(jq -er '.ledger_version' <<<"$balance_response")" \
    "Administrative balance ledger version"
balance_replay="$(admin_request POST "/admin/users/$user_id/balance" \
    "$balance_body" "$admin_token" "$balance_idempotency_key")"
assert_equals "true" "$(jq -er '.duplicate' <<<"$balance_replay")" \
    "Administrative balance replay marker"

balance_conflict_status="$(compose exec -T admin-api curl -sS -o /dev/null -w '%{http_code}' \
    -X POST -H 'Content-Type: application/json' \
    -H "Authorization: Bearer $admin_token" \
    -H "Idempotency-Key: $balance_idempotency_key" \
    --data '{"delta":1001,"reason":"Changed smoke-test funding"}' \
    "http://127.0.0.1:5001/admin/users/$user_id/balance")"
assert_equals "409" "$balance_conflict_status" "Administrative balance replay conflict"
balance_overdraft_status="$(compose exec -T admin-api curl -sS -o /dev/null -w '%{http_code}' \
    -X POST -H 'Content-Type: application/json' \
    -H "Authorization: Bearer $admin_token" \
    -H "Idempotency-Key: ${balance_idempotency_key}-overdraft" \
    --data '{"delta":-1001,"reason":"Rejected smoke-test overdraft"}' \
    "http://127.0.0.1:5001/admin/users/$user_id/balance")"
assert_equals "409" "$balance_overdraft_status" "Administrative balance overdraft"
assert_equals "1|1|1000.00000000" "$(db_query "
SELECT
  (SELECT count(*) FROM balance_ledger WHERE user_id = $user_id AND entry_type = 'admin_adjustment') || '|' ||
  (SELECT count(*) FROM audit_logs WHERE action = 'balance.adjust' AND resource_id = '$user_id') || '|' ||
  (SELECT sum(amount) FROM balance_ledger WHERE user_id = $user_id);")" \
    "Administrative balance ledger invariants"

api_key="$(create_api_key "$openai_group_id")"
content_policy_pattern="greenfield-policy-${suffix}"
content_policy_request_id="smoke-content-policy-${suffix}"
content_policy_rule="$(admin_request POST /admin/content-audit/rules \
    "$(jq -cn --arg pattern "$content_policy_pattern" \
        '{pattern:$pattern,actionType:"block",scope:"chat_completions",status:"active",stage:"request"}')" \
    "$admin_token")"
content_policy_rule_id="$(jq -er '.id' <<<"$content_policy_rule")"
content_policy_response="$(curl -sS --max-time 20 --write-out $'\n%{http_code}' \
    "$gateway_url/v1/chat/completions" \
    -H "Authorization: Bearer $api_key" \
    -H "Content-Type: application/json" \
    -H "X-Request-ID: $content_policy_request_id" \
    -H "Idempotency-Key: ${content_policy_request_id}-idem" \
    --data "$(jq -cn --arg pattern "$content_policy_pattern" \
        '{model:"gpt-4o",messages:[{role:"user",content:("contains " + $pattern)}],stream:false}')")"
assert_equals "400" "${content_policy_response##*$'\n'}" \
    "Content policy block response status"
jq -e '.error.type == "content_policy_violation"' \
    <<<"${content_policy_response%$'\n'*}" >/dev/null
assert_equals "1|0|1" "$(db_query "
SELECT
  (SELECT count(*) FROM content_audit_logs WHERE request_id = '$content_policy_request_id') || '|' ||
  (SELECT count(*) FROM request_leases WHERE request_id = '$content_policy_request_id') || '|' ||
  (SELECT count(*) FROM content_audit_logs WHERE request_id = '$content_policy_request_id' AND action = 'block');")" \
    "Content policy audit and no-lease invariant"
admin_request DELETE "/admin/content-audit/rules/$content_policy_rule_id" "" "$admin_token" >/dev/null
echo "PASS: pre-dispatch content policy block, audit, and no-lease invariant"
scoped_key_response="$(admin_request POST /admin/apikeys/ \
    "$(jq -cn --argjson user "$user_id" --argjson group "$openai_group_id" \
        '{userId:$user,groupId:$group,quota:100,expiresAt:null,scopes:["models"],ipWhitelist:[],ipBlacklist:[],rateLimit5h:0,rateLimit1d:0,rateLimit7d:0}')" \
    "$admin_token")"
scoped_api_key="$(jq -er '.key' <<<"$scoped_key_response")"
scoped_api_key_id="$(jq -er '.id' <<<"$scoped_key_response")"
scoped_request_id="smoke-scoped-denial-${suffix}"
scoped_denial="$(curl -sS --max-time 20 --write-out $'\n%{http_code}' \
    "$gateway_url/v1/chat/completions" \
    -H "Authorization: Bearer $scoped_api_key" \
    -H "Content-Type: application/json" \
    -H "X-Request-ID: $scoped_request_id" \
    -H "Idempotency-Key: ${scoped_request_id}-idem" \
    --data '{"model":"gpt-4o","messages":[{"role":"user","content":"scope denial"}],"stream":false}')"
scoped_denial_status="${scoped_denial##*$'\n'}"
scoped_denial_body="${scoped_denial%$'\n'*}"
assert_equals "403" "$scoped_denial_status" "Scoped API key denied capability status"
jq -e '.error.type == "permission_error"' <<<"$scoped_denial_body" >/dev/null
assert_equals "1|0" "$(db_query "
SELECT
  (SELECT count(*) FROM api_key_audit_events WHERE api_key_id = $scoped_api_key_id AND action = 'denied' AND capability = 'chat_completions') || '|' ||
  (SELECT count(*) FROM request_leases WHERE request_id = '$scoped_request_id');")" \
    "Scoped API key denial audit and lease invariants"
scoped_api_key_hash="$(db_query "SELECT key_hash FROM user_api_keys WHERE api_key_id = $scoped_api_key_id;")"
scoped_audit_response="$(admin_request GET "/admin/apikeys/${scoped_api_key_hash}/audit?action=denied&page=1&size=10" "" "$admin_token")"
assert_equals "1" "$(jq -r '.total' <<<"$scoped_audit_response")" \
    "Scoped API key authenticated audit total"
assert_equals "denied" "$(jq -r '.items[0].action' <<<"$scoped_audit_response")" \
    "Scoped API key authenticated audit action"
if jq -e 'has("key") or (.items[0] | has("key"))' <<<"$scoped_audit_response" >/dev/null; then
    echo "API-key audit response exposed key material" >&2
    exit 1
fi

scoped_update_body="$(jq -cn --argjson user "$user_id" --argjson group "$openai_group_id" \
    '{userId:$user,groupId:$group,quota:100,expiresAt:null,ipWhitelist:[],ipBlacklist:[],rateLimit5h:0,rateLimit1d:0,rateLimit7d:0,scopes:["chat_completions"]}')"
ownership_guard_status="$(compose exec -T admin-api curl -sS -o /dev/null -w '%{http_code}' \
    -X PUT -H 'Content-Type: application/json' -H "Authorization: Bearer $admin_token" \
    --data "$(jq --argjson other_user "$((user_id + 1))" '.userId = $other_user' <<<"$scoped_update_body")" \
    "http://127.0.0.1:5001/admin/apikeys/$scoped_api_key_hash")"
assert_equals "400" "$ownership_guard_status" "API-key ownership update guard"
scoped_update_status="$(compose exec -T admin-api curl -sS -o /dev/null -w '%{http_code}' \
    -X PUT -H 'Content-Type: application/json' -H "Authorization: Bearer $admin_token" \
    --data "$scoped_update_body" \
    "http://127.0.0.1:5001/admin/apikeys/$scoped_api_key_hash")"
assert_equals "204" "$scoped_update_status" "Admin API-key policy update"
scoped_updated_request_id="smoke-scoped-updated-${suffix}"
scoped_updated_response="$(curl -fsS --max-time 20 "$gateway_url/v1/chat/completions" \
    -H "Authorization: Bearer $scoped_api_key" -H "Content-Type: application/json" \
    -H "X-Request-ID: $scoped_updated_request_id" \
    -H "Idempotency-Key: ${scoped_updated_request_id}-idem" \
    --data '{"model":"gpt-4o","messages":[{"role":"user","content":"updated scope"}],"stream":false}')"
jq -e '.choices[0].message.content == "mock response"' <<<"$scoped_updated_response" >/dev/null
scoped_revoke_status="$(compose exec -T admin-api curl -sS -o /dev/null -w '%{http_code}' \
    -X DELETE -H "Authorization: Bearer $admin_token" \
    "http://127.0.0.1:5001/admin/apikeys/$scoped_api_key_hash")"
assert_equals "204" "$scoped_revoke_status" "Admin API-key revoke"
scoped_revoked_result="$(curl -sS --max-time 20 --write-out $'\n%{http_code}' \
    "$gateway_url/v1/chat/completions" \
    -H "Authorization: Bearer $scoped_api_key" -H "Content-Type: application/json" \
    -H "X-Request-ID: smoke-scoped-revoked-${suffix}" \
    -H "Idempotency-Key: smoke-scoped-revoked-idem-${suffix}" \
    --data '{"model":"gpt-4o","messages":[{"role":"user","content":"revoked"}],"stream":false}')"
assert_equals "401" "${scoped_revoked_result##*$'\n'}" "Revoked API key HTTP status"
assert_equals "1|1|1" "$(db_query "
SELECT
  (SELECT count(*) FROM api_key_audit_events WHERE api_key_id = $scoped_api_key_id AND action = 'updated') || '|' ||
  (SELECT count(*) FROM api_key_audit_events WHERE api_key_id = $scoped_api_key_id AND action = 'revoked') || '|' ||
  (SELECT count(*) FROM request_leases WHERE request_id = '$scoped_updated_request_id' AND status = 'completed');")" \
    "Admin API-key update/revoke audit and lease invariants"

user_relogin_response="$(admin_request POST /auth/login \
    "$(jq -cn --arg email "$user_email" --arg password "$user_password" \
        '{email:$email,password:$password}')")"
self_service_token="$(jq -er '.token' <<<"$user_relogin_response")"
self_key_response="$(admin_request POST /user/apikeys/ \
    "$(jq -cn --argjson group "$openai_group_id" \
        '{name:"self-rotation-smoke",groupId:$group,quota:100,scopes:["chat_completions"]}')" \
    "$self_service_token")"
self_key_id="$(jq -er '.id' <<<"$self_key_response")"
self_rotated_response="$(admin_request POST "/user/apikeys/$self_key_id/rotate" '' "$self_service_token")"
self_rotated_id="$(jq -er '.id' <<<"$self_rotated_response")"
self_rotated_key="$(jq -er '.key' <<<"$self_rotated_response")"
if [[ "$self_rotated_key" != sk-* || "$self_rotated_id" == "$self_key_id" ]]; then
    echo "User API-key rotation did not issue a distinct key" >&2
    exit 1
fi
assert_equals "revoked|active|1|1" "$(db_query "
SELECT
  (SELECT status FROM user_api_keys WHERE id = $self_key_id) || '|' ||
  (SELECT status FROM user_api_keys WHERE id = $self_rotated_id) || '|' ||
  (SELECT count(*) FROM api_key_audit_events WHERE api_key_id = (SELECT api_key_id FROM user_api_keys WHERE id = $self_rotated_id) AND action = 'rotated') || '|' ||
  (SELECT count(*) FROM api_key_audit_events WHERE api_key_id = $self_key_id AND action = 'revoked');")" \
    "User API-key rotation state and audit invariants"
echo "PASS: authenticated API-key audit, Admin update/revoke, and user rotation"
echo "PASS: scoped API key denies chat capability with audited 403 and no lease"
fault_429_api_key="$(create_api_key "$fault_429_group_id")"
fault_500_api_key="$(create_api_key "$fault_500_group_id")"
fault_timeout_api_key="$(create_api_key "$fault_timeout_group_id")"
fault_disconnect_api_key="$(create_api_key "$fault_disconnect_group_id")"
fault_disconnect_stream_api_key="$(create_api_key "$fault_disconnect_stream_group_id")"
fault_disconnect_after_usage_api_key="$(create_api_key "$fault_disconnect_after_usage_group_id")"
fault_client_disconnect_api_key="$(create_api_key "$fault_client_disconnect_group_id")"
fault_malformed_api_key="$(create_api_key "$fault_malformed_group_id")"
fault_invalid_content_type_api_key="$(create_api_key "$fault_invalid_content_type_group_id")"

effective_from="1970-01-01T00:00:00Z"
admin_request POST /admin/pricing/versions \
    "$(jq -cn --arg version "$chat_price_version" --arg model gpt-4o \
        --arg from "$effective_from" \
        '{version:$version,model:$model,inputUsdPerMillion:2.5,outputUsdPerMillion:10,cacheReadUsdPerMillion:0,cacheWriteUsdPerMillion:1.25,effectiveFrom:$from,effectiveUntil:null}')" \
    "$admin_token" >/dev/null
admin_request POST /admin/pricing/versions \
    "$(jq -cn --arg version "$embedding_price_version" --arg model text-embedding-3-small \
        --arg from "$effective_from" \
        '{version:$version,model:$model,inputUsdPerMillion:0.1,outputUsdPerMillion:0,cacheReadUsdPerMillion:0,cacheWriteUsdPerMillion:0,effectiveFrom:$from,effectiveUntil:null}')" \
    "$admin_token" >/dev/null
admin_request POST /admin/pricing/versions \
    "$(jq -cn --arg version "$media_price_version" --arg model mock-image-1 \
        --arg from "$effective_from" \
        '{version:$version,model:$model,inputUsdPerMillion:0,outputUsdPerMillion:0,cacheReadUsdPerMillion:0,cacheWriteUsdPerMillion:0,effectiveFrom:$from,effectiveUntil:null}')" \
    "$admin_token" >/dev/null

sleep 6
chat_body='{"model":"gpt-4o","messages":[{"role":"user","content":"greenfield compose smoke"}],"stream":false}'
response_policy_pattern="mock response"
response_policy_request_id="smoke-response-policy-${suffix}"
response_policy_idempotency_key="${response_policy_request_id}-idem"
response_policy_rule="$(admin_request POST /admin/content-audit/rules \
    "$(jq -cn --arg pattern "$response_policy_pattern" \
        '{pattern:$pattern,actionType:"block",scope:"chat_completions",status:"active",stage:"response"}')" \
    "$admin_token")"
response_policy_rule_id="$(jq -er '.id' <<<"$response_policy_rule")"
response_policy_body="$(jq -cn --arg marker "$suffix" \
    '{model:"gpt-4o",messages:[{role:"user",content:("response policy " + $marker)}],stream:false}')"
response_policy_response="$(curl -sS --max-time 20 --write-out $'\n%{http_code}' \
    "$gateway_url/v1/chat/completions" \
    -H "Authorization: Bearer $api_key" \
    -H "Content-Type: application/json" \
    -H "X-Request-ID: $response_policy_request_id" \
    -H "Idempotency-Key: $response_policy_idempotency_key" \
    --data "$response_policy_body")"
assert_equals "400" "${response_policy_response##*$'\n'}" \
    "Response content policy block status"
jq -e '.error.type == "content_policy_violation"' \
    <<<"${response_policy_response%$'\n'*}" >/dev/null

response_policy_state=""
for attempt in $(seq 1 30); do
    response_policy_state="$(db_query "
SELECT
  (SELECT count(*) FROM content_audit_logs WHERE request_id = '$response_policy_request_id' AND stage = 'response' AND action = 'block') || '|' ||
  (SELECT count(*) FROM request_leases WHERE request_id = '$response_policy_request_id' AND status = 'completed') || '|' ||
  (SELECT count(*) FROM balance_holds h JOIN request_leases l USING (lease_token) WHERE l.request_id = '$response_policy_request_id' AND h.status = 'committed') || '|' ||
  (SELECT count(*) FROM usage_events u WHERE u.request_id = '$response_policy_request_id') || '|' ||
  (SELECT count(*) FROM usage_logs u WHERE u.request_id = '$response_policy_request_id') || '|' ||
  (SELECT count(*) FROM balance_ledger b JOIN request_leases l USING (lease_token) WHERE l.request_id = '$response_policy_request_id' AND b.entry_type = 'usage_debit') || '|' ||
  (SELECT count(*) FROM request_idempotency WHERE idempotency_key = '$response_policy_idempotency_key' AND status = 'completed' AND response_status_code = 400);")"
    [[ "$response_policy_state" == "1|1|1|1|1|1|1" ]] && break
    sleep 1
done
assert_equals "1|1|1|1|1|1|1" "$response_policy_state" \
    "Response content policy settlement invariants"
response_policy_replay="$(curl -sS --max-time 20 --write-out $'\n%{http_code}' \
    "$gateway_url/v1/chat/completions" \
    -H "Authorization: Bearer $api_key" \
    -H "Content-Type: application/json" \
    -H "X-Request-ID: ${response_policy_request_id}-replay" \
    -H "Idempotency-Key: $response_policy_idempotency_key" \
    --data "$response_policy_body")"
assert_equals "400" "${response_policy_replay##*$'\n'}" \
    "Response content policy replay status"
jq -e '.error.type == "content_policy_violation"' \
    <<<"${response_policy_replay%$'\n'*}" >/dev/null
assert_equals "1|1|1" "$(db_query "
SELECT
  (SELECT count(*) FROM content_audit_logs WHERE request_id = '$response_policy_request_id' AND stage = 'response') || '|' ||
  (SELECT count(*) FROM request_leases WHERE request_id = '$response_policy_request_id') || '|' ||
  (SELECT count(*) FROM balance_ledger b JOIN request_leases l USING (lease_token) WHERE l.request_id = '$response_policy_request_id' AND b.entry_type = 'usage_debit');")" \
    "Response content policy replay idempotency"
admin_request DELETE "/admin/content-audit/rules/$response_policy_rule_id" "" "$admin_token" >/dev/null
echo "PASS: response content policy with hidden output, normal settlement, and exact replay"
start_platform_after_fault() {
    # Podman Compose may leave an exited container stopped even with
    # restart: on-failure. Start the same container so the SQL lease and
    # fault marker survive the recovery boundary.
    compose start platform-silo >/dev/null
    wait_for "Platform recovery after fault hook" 90 compose exec -T platform-silo \
        curl -fsS http://127.0.0.1:5000/ready >/dev/null
}

platform_dispatch_fault_claimed() {
    local marker_name=platform-before-provider-dispatch.claimed
    if [[ "${PLATFORM_FAULT_HOOK:-}" == "platform.before_provider_dispatch_retry" ]]; then
        marker_name=platform-before-provider-dispatch-retry.claimed
    fi
    compose exec -T platform-silo test -f \
        "/var/run/scalaapi/fault-hooks/$marker_name"
}

platform_dispatch_fault_lease_safely_expired() {
    [[ "$(db_query "
        SELECT count(*) FROM request_leases l
        WHERE l.request_id = '$platform_dispatch_fault_request_id'
          AND l.status = 'expired'
          AND NOT EXISTS (SELECT 1 FROM usage_events u WHERE u.lease_token = l.lease_token)
          AND NOT EXISTS (SELECT 1 FROM usage_logs u WHERE u.lease_token = l.lease_token)
          AND EXISTS (SELECT 1 FROM balance_holds h
                      WHERE h.lease_token = l.lease_token AND h.status = 'released')
          AND EXISTS (SELECT 1 FROM request_idempotency i
                      WHERE i.idempotency_key = '$platform_dispatch_fault_idempotency_key'
                        AND i.status = 'expired');")" == "1" ]]
}

platform_worker_fault_claimed() {
    compose exec -T platform-silo test -f \
        /var/run/scalaapi/fault-hooks/platform-after-outbox-claim.claimed
}

platform_worker_outbox_reclaimed() {
    [[ "$(db_query "
        SELECT count(*) FROM usage_outbox o
        JOIN request_leases l ON l.lease_token = o.lease_token
        WHERE l.request_id = '$chat_request_id'
          AND o.event_type = 'complete'
          AND o.processed_at IS NOT NULL
          AND o.claimed_by IS NULL
          AND o.claimed_until IS NULL;")" == "1" ]]
}

if [[ "${PLATFORM_FAULT_HOOK:-}" == "platform.before_provider_dispatch" ]]; then
    set +e
    curl -fsS --max-time 30 "$gateway_url/v1/chat/completions" \
        -H "Authorization: Bearer $api_key" -H "Content-Type: application/json" \
        -H "X-Request-ID: $platform_dispatch_fault_request_id" \
        -H "Idempotency-Key: $platform_dispatch_fault_idempotency_key" \
        --data "$chat_body" >/dev/null
    platform_dispatch_fault_exit=$?
    set -e
    if (( platform_dispatch_fault_exit == 0 )); then
        echo "Platform before-provider-dispatch hook did not fail the request" >&2
        exit 1
    fi
    start_platform_after_fault
    wait_for "Platform before-provider-dispatch marker" 30 platform_dispatch_fault_claimed
    wait_for "Platform before-provider-dispatch safe expiry" 60 \
        platform_dispatch_fault_lease_safely_expired
    platform_dispatch_fault_safe_expiry=1
    echo "PASS: Platform before-provider-dispatch crash safely expired one held lease"
fi

if [[ "${PLATFORM_FAULT_HOOK:-}" == "platform.before_provider_dispatch_retry" ]]; then
    platform_retry_recovery_pid=""
    (
        wait_for "Platform dispatch retry marker" 30 platform_dispatch_fault_claimed
        start_platform_after_fault
    ) &
    platform_retry_recovery_pid=$!
    retry_response="$(curl -fsS --max-time 45 "$gateway_url/v1/chat/completions" \
        -H "Authorization: Bearer $api_key" -H "Content-Type: application/json" \
        -H "X-Request-ID: $platform_dispatch_retry_request_id" \
        -H "Idempotency-Key: $platform_dispatch_retry_idempotency_key" \
        --data "$chat_body")"
    wait "$platform_retry_recovery_pid"
    jq -e '(.choices | length > 0) and (.usage.total_tokens > 0)' \
        <<<"$retry_response" >/dev/null
    platform_dispatch_retry_settled() {
        [[ "$(db_query "SELECT count(*) FROM request_leases WHERE request_id = '$platform_dispatch_retry_request_id' AND status = 'completed' AND final_cost_usd > 0;")" == "1" ]]
    }
    wait_for "Platform dispatch retry settlement" 30 platform_dispatch_retry_settled
    assert_equals "1|1|1|1" "$(db_query "
SELECT
  (SELECT count(*) FROM request_leases WHERE request_id = '$platform_dispatch_retry_request_id') || '|' ||
  (SELECT count(*) FROM usage_events WHERE request_id = '$platform_dispatch_retry_request_id') || '|' ||
  (SELECT count(*) FROM usage_logs WHERE request_id = '$platform_dispatch_retry_request_id') || '|' ||
  (SELECT count(*) FROM balance_ledger l JOIN request_leases r ON r.lease_token = l.lease_token
   WHERE r.request_id = '$platform_dispatch_retry_request_id' AND l.entry_type = 'usage_debit');")" \
        "Platform dispatch retry uses one lease and one settlement"
    platform_dispatch_retry=1
    echo "PASS: Platform dispatch retry recovered an active lease after process loss"
fi

start_gateway_after_fault() {
    # Podman Compose may leave an exited container stopped even with
    # restart: on-failure. Start the same container so its durable marker and
    # Gateway usage outbox volume are preserved across the recovery boundary.
    compose start gateway >/dev/null
    wait_for "Gateway recovery after fault hook" 90 curl -fsS "$gateway_url/ready" >/dev/null
}

gateway_fault_claimed() {
    local marker_name
    case "${GATEWAY_FAULT_HOOK:-}" in
        gateway.after_provider_completion)
            marker_name=gateway-after-provider-completion.claimed ;;
        gateway.before_provider_dispatch)
            marker_name=gateway-before-provider-dispatch.claimed ;;
        *)
            return 1 ;;
    esac
    compose exec -T gateway test -f "/var/lib/scalaapi/fault-hooks/$marker_name"
}

gateway_fault_lease_reconciled() {
    [[ "$(db_query "
        SELECT count(*) FROM request_leases l
        WHERE l.request_id = '$gateway_fault_request_id'
          AND l.status = 'reconciliation_needed'
          AND NOT EXISTS (SELECT 1 FROM usage_events u WHERE u.lease_token = l.lease_token)
          AND NOT EXISTS (SELECT 1 FROM usage_logs u WHERE u.lease_token = l.lease_token)
          AND EXISTS (SELECT 1 FROM balance_holds h
                      WHERE h.lease_token = l.lease_token AND h.status = 'active');")" == "1" ]]
}

gateway_fault_lease_safely_expired() {
    [[ "$(db_query "
        SELECT count(*) FROM request_leases l
        WHERE l.request_id = '$gateway_fault_request_id'
          AND l.status = 'expired'
          AND NOT EXISTS (SELECT 1 FROM usage_events u WHERE u.lease_token = l.lease_token)
          AND NOT EXISTS (SELECT 1 FROM usage_logs u WHERE u.lease_token = l.lease_token)
          AND EXISTS (SELECT 1 FROM balance_holds h
                      WHERE h.lease_token = l.lease_token AND h.status = 'released')
          AND EXISTS (SELECT 1 FROM request_idempotency i
                      WHERE i.idempotency_key = '$gateway_fault_idempotency_key'
                        AND i.status = 'expired');")" == "1" ]]
}

if [[ "${GATEWAY_FAULT_HOOK:-}" == "gateway.after_provider_completion" ||
      "${GATEWAY_FAULT_HOOK:-}" == "gateway.before_provider_dispatch" ]]; then
    set +e
    curl -sS --max-time 30 "$gateway_url/v1/chat/completions" \
        -H "Authorization: Bearer $api_key" -H "Content-Type: application/json" \
        -H "X-Request-ID: $gateway_fault_request_id" \
        -H "Idempotency-Key: $gateway_fault_idempotency_key" \
        --data "$chat_body" >/dev/null
    gateway_fault_exit=$?
    set -e
    if (( gateway_fault_exit == 0 )); then
        echo "Gateway fault hook did not fail the request" >&2
        exit 1
    fi
    start_gateway_after_fault
    wait_for "Gateway fault hook marker" 30 gateway_fault_claimed
    if [[ "${GATEWAY_FAULT_HOOK}" == "gateway.before_provider_dispatch" ]]; then
        wait_for "Gateway before-provider-dispatch safe expiry" 60 gateway_fault_lease_safely_expired
        gateway_hook_safe_expiry=1
        echo "PASS: Gateway before-provider-dispatch crash safely expired one held lease"
    else
        wait_for "Gateway hook lease reconciliation" 60 gateway_fault_lease_reconciled
        gateway_hook_unknown_incidents=1
        echo "PASS: Gateway after-provider-completion crash retained one reconciliable lease"
    fi
fi

chat_response="$(curl -fsS "$gateway_url/v1/chat/completions" \
    -H "Authorization: Bearer $api_key" -H "Content-Type: application/json" \
    -H "X-Request-ID: $chat_request_id" -H "Idempotency-Key: $chat_idempotency_key" \
    --data "$chat_body")"
jq -e '(.choices | length > 0) and (.usage.total_tokens > 0)' \
    <<<"$chat_response" >/dev/null

embedding_response="$(curl -fsS "$gateway_url/v1/embeddings" \
    -H "Authorization: Bearer $api_key" -H "Content-Type: application/json" \
    -H "X-Request-ID: $embedding_request_id" -H "Idempotency-Key: $embedding_idempotency_key" \
    --data '{"model":"text-embedding-3-small","input":["hello","world"],"dimensions":3,"encoding_format":"float"}')"
jq -e '(.data | length == 2) and all(.data[]; (.embedding | length == 3)) and (.usage.prompt_tokens > 0) and (.usage.total_tokens > 0)' \
    <<<"$embedding_response" >/dev/null

embedding_base64_response="$(curl -fsS "$gateway_url/v1/embeddings" \
    -H "Authorization: Bearer $api_key" -H "Content-Type: application/json" \
    -H "X-Request-ID: $embedding_base64_request_id" -H "Idempotency-Key: $embedding_base64_idempotency_key" \
    --data '{"model":"text-embedding-3-small","input":"hello","dimensions":2,"encoding_format":"base64"}')"
jq -e '(.data | length == 1) and (.data[0].embedding | type == "string" and length == 12) and (.usage.total_tokens > 0)' \
    <<<"$embedding_base64_response" >/dev/null

embedding_settled() {
    [[ "$(db_query "SELECT count(*) FROM request_leases WHERE request_id IN ('$embedding_request_id', '$embedding_base64_request_id') AND status = 'completed' AND final_cost_usd > 0 AND pricing_version = '$embedding_price_version';")" == "2" ]]
}
wait_for "embedding settlement" 30 embedding_settled
echo "PASS: Embeddings input count, dimensions, float/base64 encoding, and usage settlement"

embedding_invalid_response="$(curl -sS --max-time 30 --write-out $'\n%{http_code}' "$gateway_url/v1/embeddings" \
    -H "Authorization: Bearer $api_key" -H "Content-Type: application/json" \
    -H "X-Request-ID: $embedding_invalid_request_id" -H "Idempotency-Key: $embedding_invalid_idempotency_key" \
    --data '{"model":"text-embedding-3-small","input":"hello","dimensions":3,"mock_scenario":"invalid_response"}')"
assert_equals "502" "${embedding_invalid_response##*$'\n'}" "Malformed embeddings provider response status"
jq -e '.error.type == "provider_protocol_error"' <<<"${embedding_invalid_response%$'\n'*}" >/dev/null
embedding_invalid_reconciled() {
    [[ "$(db_query "SELECT count(*) FROM request_leases WHERE request_id = '$embedding_invalid_request_id' AND status = 'reconciliation_needed';")" == "1" ]]
}
wait_for "malformed embeddings reconciliation hold" 30 embedding_invalid_reconciled
echo "PASS: Malformed embeddings provider response retained an unknown-charge lease"
oauth_account="$(admin_request GET "/admin/accounts/$openai_account_id" '' "$admin_token")"
assert_equals "2|true" "$(jq -r '.oAuth.version|tostring' <<<"$oauth_account")|$(jq -r '.oAuth.expiresAtUnixSeconds > now' <<<"$oauth_account")" \
    "Expired Provider OAuth credential refreshed before dispatch"
if grep -Eq 'mock-(access|refresh)|mock-secret' <<<"$oauth_account"; then
    echo "Provider account details exposed OAuth secret material" >&2
    exit 1
fi
echo "PASS: Expired Provider OAuth credential refreshed before dispatch and secrets stayed private"
oauth_audit="$(admin_request GET "/admin/accounts/$openai_account_id/credential-refresh-attempts?outcome=succeeded&source=dispatch" '' "$admin_token")"
assert_equals "1|1|2|succeeded" "$(jq -r '[.total, (.items | length), .items[0].versionAfter, .items[0].outcome] | @tsv' <<<"$oauth_audit" | tr '\t' '|')" \
    "Provider OAuth refresh audit persisted without secret material"
if grep -Eq 'mock-(access|refresh)|mock-secret' <<<"$oauth_audit"; then
    echo "Provider OAuth refresh audit exposed secret material" >&2
    exit 1
fi

chat_settled() {
    [[ "$(db_query "SELECT count(*) FROM request_leases WHERE request_id = '$chat_request_id' AND status = 'completed' AND final_cost_usd > 0 AND pricing_version = '$chat_price_version';")" == "1" ]]
}

# Settlement and outbox hooks terminate the Platform process after the request
# has reached a durable boundary. Observe that exit before waiting for the
# terminal row, then start the same container so the persisted outbox can run.
platform_hook_exits_after_durable_write() {
    [[ "${PLATFORM_FAULT_HOOK:-}" == "platform.after_settlement_commit" ||
       "${PLATFORM_FAULT_HOOK:-}" == "platform.after_outbox_claim" ||
       "${PLATFORM_FAULT_HOOK:-}" == "platform.before_outbox_ack" ]]
}

if [[ "${PLATFORM_FAULT_HOOK:-}" == "platform.before_settlement_commit" ]] ||
   platform_hook_exits_after_durable_write; then
    platform_container_id="$(service_container_id platform-silo)"
    platform_faulted() {
        [[ "$("$container_cli" inspect --format '{{.State.Status}}' \
            "$platform_container_id" 2>/dev/null)" != "running" ]]
    }
    wait_for "Platform fault hook termination" 30 platform_faulted
    if [[ "${PLATFORM_FAULT_HOOK}" == "platform.before_settlement_commit" ]]; then
        start_platform_after_fault
    fi
fi

wait_for "chat settlement" 30 chat_settled

if [[ -n "${PLATFORM_FAULT_HOOK:-}" && \
      "${PLATFORM_FAULT_HOOK}" != "platform.before_settlement_commit" && \
      "${PLATFORM_FAULT_HOOK}" != "platform.before_provider_dispatch" && \
      "${PLATFORM_FAULT_HOOK}" != "platform.before_provider_dispatch_retry" ]]; then
    start_platform_after_fault
    if [[ "${PLATFORM_FAULT_HOOK}" == "platform.after_outbox_claim" ]]; then
        wait_for "Platform after-outbox-claim marker" 30 platform_worker_fault_claimed
        wait_for "Platform outbox claim recovery" 60 platform_worker_outbox_reclaimed
        platform_worker_reclaim=1
        echo "PASS: Platform outbox claim was reclaimed and applied once after worker crash"
    fi
fi

chat_replay="$(curl -fsS "$gateway_url/v1/chat/completions" \
    -H "Authorization: Bearer $api_key" -H "Content-Type: application/json" \
    -H "X-Request-ID: ${chat_request_id}-replay" -H "Idempotency-Key: $chat_idempotency_key" \
    --data "$chat_body")"
assert_equals "$(jq -cS . <<<"$chat_response")" "$(jq -cS . <<<"$chat_replay")" \
    "Idempotent chat replay body"
assert_equals "1" "$(db_query "SELECT count(*) FROM request_idempotency WHERE idempotency_key = '$chat_idempotency_key';")" \
    "Idempotent chat lease count"

concurrent_tmp_dir="$(mktemp -d)"
concurrent_chat_body='{"model":"gpt-4o","messages":[{"role":"user","content":"concurrent idempotency"}],"stream":false}'
curl -sS --max-time 30 -o "$concurrent_tmp_dir/first.body" -w '%{http_code}' \
    "$gateway_url/v1/chat/completions" \
    -H "Authorization: Bearer $api_key" -H "Content-Type: application/json" \
    -H "X-Request-ID: $concurrent_request_id-a" \
    -H "Idempotency-Key: $concurrent_idempotency_key" \
    --data "$concurrent_chat_body" >"$concurrent_tmp_dir/first.status" &
concurrent_first_pid=$!
curl -sS --max-time 30 -o "$concurrent_tmp_dir/second.body" -w '%{http_code}' \
    "$gateway_url/v1/chat/completions" \
    -H "Authorization: Bearer $api_key" -H "Content-Type: application/json" \
    -H "X-Request-ID: $concurrent_request_id-b" \
    -H "Idempotency-Key: $concurrent_idempotency_key" \
    --data "$concurrent_chat_body" >"$concurrent_tmp_dir/second.status" &
concurrent_second_pid=$!
set +e
wait "$concurrent_first_pid"
concurrent_first_exit=$?
wait "$concurrent_second_pid"
concurrent_second_exit=$?
set -e
assert_equals "0" "$concurrent_first_exit" "Concurrent first request transport"
assert_equals "0" "$concurrent_second_exit" "Concurrent second request transport"
concurrent_first_status="$(tr -d '\r\n' <"$concurrent_tmp_dir/first.status")"
concurrent_second_status="$(tr -d '\r\n' <"$concurrent_tmp_dir/second.status")"
assert_one_of "200|409" "$concurrent_first_status" "Concurrent first request status"
assert_one_of "200|409" "$concurrent_second_status" "Concurrent second request status"
if [[ "$concurrent_first_status" == "409" && "$concurrent_second_status" == "409" ]]; then
    echo "Concurrent idempotency requests both rejected" >&2
    exit 1
fi
concurrent_idempotency_settled() {
    [[ "$(db_query "SELECT count(*) FROM request_leases WHERE request_id LIKE '$concurrent_request_id-%' AND status = 'completed';")" == "1" ]]
}
wait_for "concurrent idempotency settlement" 30 concurrent_idempotency_settled
assert_equals "1" "$(db_query "SELECT count(*) FROM request_idempotency WHERE idempotency_key = '$concurrent_idempotency_key';")" \
    "Concurrent idempotency lease uniqueness"
rm -rf "$concurrent_tmp_dir"
echo "PASS: concurrent API-key idempotency serialized without duplicate lease"

expired_key_expires_at="$(jq -nr '((now * 1000) | floor) + 2000')"
expired_key_response="$(admin_request POST /admin/apikeys/ \
    "$(jq -cn --argjson user "$user_id" --argjson group "$openai_group_id" \
        --argjson expires "$expired_key_expires_at" \
        '{userId:$user,groupId:$group,quota:100,expiresAt:$expires,ipWhitelist:[],ipBlacklist:[],rateLimit5h:0,rateLimit1d:0,rateLimit7d:0}')" \
    "$admin_token")"
expired_api_key="$(jq -er '.key' <<<"$expired_key_response")"
sleep 3
expired_key_result="$(curl -sS --max-time 20 --write-out $'\n%{http_code}' \
    "$gateway_url/v1/chat/completions" \
    -H "Authorization: Bearer $expired_api_key" -H "Content-Type: application/json" \
    -H "X-Request-ID: $expired_key_request_id" \
    -H "Idempotency-Key: $expired_key_request_id-idem" \
    --data "$chat_body")"
expired_key_status="$(printf '%s\n' "$expired_key_result" | tail -n 1)"
expired_key_body="$(printf '%s\n' "$expired_key_result" | sed '$d')"
assert_equals "401" "$expired_key_status" "Expired API key HTTP status"
jq -e '.error.type == "authentication_error"' <<<"$expired_key_body" >/dev/null
assert_equals "0" "$(db_query "SELECT count(*) FROM request_leases WHERE request_id = '$expired_key_request_id';")" \
    "Expired API key creates no lease"
echo "PASS: expired API key rejected before scheduling with no lease"

python3 "$stack_dir/realtime_smoke.py" "$gateway_url" "$api_key" \
    "$realtime_request_id" "$realtime_idempotency_key"
realtime_lease_settled() {
    [[ "$(db_query "SELECT count(*) FROM request_leases WHERE request_id = '$realtime_request_id' AND status = 'completed';")" == "1" ]]
}
wait_for "realtime lease settlement" 30 realtime_lease_settled
realtime_lease_summary="$(db_query "
WITH target_lease AS (
    SELECT lease_token, request_id, status, final_cost_usd
    FROM request_leases
    WHERE request_id = '$realtime_request_id'
)
SELECT
  (SELECT count(*) FROM target_lease) || '|' ||
  (SELECT count(*) FROM target_lease WHERE status = 'completed') || '|' ||
  (SELECT count(*) FROM usage_events u JOIN target_lease l USING (lease_token)) || '|' ||
  (SELECT count(*) FROM usage_logs u JOIN target_lease l USING (lease_token)) || '|' ||
  (SELECT count(*) FROM balance_holds h JOIN target_lease l USING (lease_token) WHERE h.status = 'committed') || '|' ||
  (SELECT count(*) FROM balance_ledger b JOIN target_lease l USING (lease_token)
   WHERE b.entry_type = 'usage_debit' AND b.amount = -l.final_cost_usd);")"
assert_equals "1|1|1|1|1|1" "$realtime_lease_summary" \
    "Realtime lease settlement"

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
run_chat_fault "500" "$fault_500_api_key" "503" "provider_unavailable" "20" "aborted"
run_chat_fault "429" "$fault_429_api_key" "503" "provider_unavailable" "20" "aborted"
run_chat_fault "malformed_usage" "$fault_malformed_api_key" "502" "provider_error" "20" "reconciliation_needed"
# Provider connection resets use one public availability error whether they are
# observed directly or after dispatch exhausts the account cooldown.
run_chat_fault "disconnect" "$fault_disconnect_api_key" "503" "provider_unavailable" "40" "reconciliation_needed"
run_chat_fault "timeout" "$fault_timeout_api_key" "502" "-" "40" "reconciliation_needed"
run_chat_stream_rejection "500" "$fault_500_api_key"
run_chat_stream_rejection "429" "$fault_429_api_key"
run_chat_stream_fault "disconnect" "$fault_disconnect_stream_api_key" "70"
run_chat_stream_fault "disconnect_before_output" "$fault_disconnect_api_key"
run_chat_stream_late_usage "disconnect_after_usage" "$fault_disconnect_after_usage_api_key"
run_chat_stream_fault "client_disconnect" "$fault_client_disconnect_api_key" "2"
run_chat_stream_fault "malformed_usage" "$fault_malformed_api_key"
run_chat_stream_fault "invalid_content_type" "$fault_invalid_content_type_api_key"
run_chat_stream_fault "timeout" "$fault_timeout_api_key" "70"

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
  (SELECT count(*) FROM request_leases WHERE status IN ('held', 'forwarded', 'output_started')) || '|' ||
  (SELECT count(*) FROM request_leases WHERE status = 'reconciliation_needed') || '|' ||
  (SELECT count(*) FROM balance_holds WHERE status = 'active') || '|' ||
  (SELECT count(*) FROM usage_outbox WHERE processed_at IS NULL) || '|' ||
  (SELECT count(*) FROM usage_outbox WHERE dead_lettered_at IS NOT NULL) || '|' ||
  (SELECT count(*) FROM balance_holds h JOIN request_leases l ON l.lease_token = h.lease_token WHERE l.request_id = '$chat_request_id' AND h.status = 'committed') || '|' ||
  (SELECT count(*) FROM usage_events WHERE request_id = '$chat_request_id') || '|' ||
  (SELECT count(*) FROM usage_logs WHERE request_id = '$chat_request_id') || '|' ||
  (SELECT count(*) FROM balance_ledger l JOIN request_leases r ON r.lease_token = l.lease_token WHERE r.request_id = '$chat_request_id' AND l.entry_type = 'usage_debit' AND l.amount = -r.final_cost_usd);")"
expected_unknown_incidents=$((10 + gateway_hook_unknown_incidents))
expected_open_after_resolution=$((expected_unknown_incidents - 1))
assert_equals "0|${expected_unknown_incidents}|${expected_unknown_incidents}|0|0|1|1|1|1" \
    "$terminal_state" "Terminal billing invariants"

accounting_projection_drained() {
    [[ "$(db_query "SELECT count(*) FROM accounting_projection_outbox WHERE user_id = $user_id;")" == "0" ]]
}
wait_for "accounting projection drain" 30 accounting_projection_drained

accounting_state="$(db_query "
SELECT
  ((SELECT posted_balance FROM accounting_accounts WHERE user_id = $user_id) =
   (SELECT sum(amount) FROM balance_ledger WHERE user_id = $user_id)) || '|' ||
  ((SELECT ledger_version FROM accounting_accounts WHERE user_id = $user_id) =
   (SELECT max(ledger_version) FROM balance_ledger WHERE user_id = $user_id)
   AND (SELECT count(*) FROM balance_ledger WHERE user_id = $user_id) =
       (SELECT max(ledger_version) FROM balance_ledger WHERE user_id = $user_id)
   AND (SELECT count(DISTINCT ledger_version) FROM balance_ledger WHERE user_id = $user_id) =
       (SELECT count(*) FROM balance_ledger WHERE user_id = $user_id)) || '|' ||
  (SELECT count(*) FROM accounting_projection_outbox WHERE user_id = $user_id);")"
assert_equals "true|true|0" "$accounting_state" \
    "Authoritative account, contiguous ledger versions, and projection drain"

reconciliation_response="$(admin_request POST /admin/reconciliation/run '{}' "$admin_token")"
assert_equals "true" "$(jq -er '.started' <<<"$reconciliation_response")" \
    "Accounting reconciliation started"
assert_equals "failed|${expected_unknown_incidents}" \
    "$(jq -r '.status + "|" + (.openIncidents | tostring)' <<<"$reconciliation_response")" \
    "Accounting reconciliation result"
open_incidents="$(admin_request GET '/admin/reconciliation/incidents?status=open' '' "$admin_token")"
assert_equals "$expected_unknown_incidents" "$(jq -er '.total' <<<"$open_incidents")" \
    "Accounting reconciliation open incident count"

operator_incident_id="$(jq -er '[.items[] | select(.kind == "unknown_provider_charge")][0].id' \
    <<<"$open_incidents")"
operator_resolution_key="smoke-resolution-${suffix}"
operator_resolution_body="$(jq -cn \
    '{action:"settle",evidenceType:"operator_usage_review",
      evidence:"Operator matched the Provider usage export for this request",
      reason:"Resolve the retained smoke fault with reviewed usage",inputTokens:10,
      outputTokens:5,statusCode:200}')"
operator_resolution="$(admin_request POST \
    "/admin/reconciliation/incidents/${operator_incident_id}/resolve" \
    "$operator_resolution_body" "$admin_token" "$operator_resolution_key")"
assert_equals "applied" "$(jq -er '.status' <<<"$operator_resolution")" \
    "Audited operator settlement"
operator_replay="$(admin_request POST \
    "/admin/reconciliation/incidents/${operator_incident_id}/resolve" \
    "$operator_resolution_body" "$admin_token" "$operator_resolution_key")"
assert_equals "duplicate" "$(jq -er '.status' <<<"$operator_replay")" \
    "Idempotent operator settlement replay"
assert_equals "1" "$(db_query "SELECT count(*) FROM accounting_reconciliation_resolutions WHERE incident_id = ${operator_incident_id};")" \
    "Operator resolution audit row"

operator_resolution_visible() {
    local visible
    visible="$(admin_request GET '/admin/reconciliation/incidents?status=open' '' "$admin_token")" \
        || return 1
    [[ "$(jq -er '.total' <<<"$visible")" == "$expected_open_after_resolution" ]]
}
wait_for "operator resolution visibility" 30 operator_resolution_visible
open_after_resolution="$(admin_request GET '/admin/reconciliation/incidents?status=open' '' "$admin_token")"
assert_equals "$expected_open_after_resolution" "$(jq -er '.total' <<<"$open_after_resolution")" \
    "Remaining unknown-charge incidents after operator settlement"

reconciliation_after_resolution="$(admin_request POST /admin/reconciliation/run '{}' "$admin_token")"
assert_equals "failed|${expected_open_after_resolution}" \
    "$(jq -r '.status + "|" + (.openIncidents | tostring)' <<<"$reconciliation_after_resolution")" \
    "Reconciliation after operator settlement"

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

echo "PASS: 29 empty-volume migrations and second-run idempotency"
echo "PASS: idempotent administrative funding, audit, conflict, and overdraft guards"
echo "PASS: Garnet-authenticated Gateway -> Platform -> Provider mock request"
echo "PASS: terminal lease, hold, usage, ledger, and outbox invariants"
echo "PASS: account/ledger/hold/Grain reconciliation with audited operator resolution"
echo "PASS: idempotent response replay without duplicate billing"
echo "PASS: new billable requests after Platform and Gateway restarts"
echo "PASS: isolated 429/500 no-charge, truncated-stream late usage settlement, and unknown-charge failures"
if (( gateway_hook_unknown_incidents > 0 )); then
    echo "PASS: Gateway fault hook recovery and retained reconciliation evidence"
fi
if (( gateway_hook_safe_expiry > 0 )); then
    echo "PASS: Gateway fault hook recovery and safe held-lease expiry"
fi
if (( platform_dispatch_fault_safe_expiry > 0 )); then
    echo "PASS: Platform fault hook recovery and safe held-lease expiry"
fi
if (( platform_dispatch_retry > 0 )); then
    echo "PASS: Platform dispatch retry recovery without duplicate lease or billing"
fi
if (( platform_worker_reclaim > 0 )); then
    echo "PASS: Platform worker claim recovery without duplicate settlement"
fi
echo "PASS: S3-compatible bucket bootstrap, object persistence, and signed download ($media_size bytes)"
