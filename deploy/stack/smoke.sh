#!/usr/bin/env bash
set -Eeuo pipefail

stack_dir="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
repo_root="$(cd "$stack_dir/../.." && pwd)"
compose_file="$stack_dir/docker-compose.yml"
compose_files=("$compose_file")
garnet_tls_enabled="${GARNET_TLS:-false}"
garnet_tls_rotation_enabled="${GARNET_TLS_ROTATION:-false}"
project="${SMOKE_PROJECT_NAME:-scalaapi-smoke-$$}"
secondary_platform_container=""
secondary_gateway_container=""

if [[ ! "$project" =~ ^[a-z0-9][a-z0-9_-]*$ ]]; then
    echo "SMOKE_PROJECT_NAME must contain only lowercase letters, numbers, dashes, and underscores" >&2
    exit 2
fi

if [[ "$garnet_tls_enabled" == "true" || "$garnet_tls_enabled" == "1" ]]; then
    tls_compose_file="$stack_dir/docker-compose.tls.yml"
    if [[ ! -f "$tls_compose_file" ]]; then
        echo "Garnet TLS Compose override is missing: $tls_compose_file" >&2
        exit 2
    fi
    : "${GARNET_CA_CERT_FILE:?GARNET_CA_CERT_FILE is required when GARNET_TLS=true}"
    : "${GARNET_SERVER_CERT_FILE:?GARNET_SERVER_CERT_FILE is required when GARNET_TLS=true}"
    : "${GARNET_SERVER_CERT_PASSWORD:?GARNET_SERVER_CERT_PASSWORD is required when GARNET_TLS=true}"
    if [[ "$garnet_tls_rotation_enabled" == "true" || "$garnet_tls_rotation_enabled" == "1" ]]; then
        : "${GARNET_SERVER_CERT_ROTATED_FILE:?GARNET_SERVER_CERT_ROTATED_FILE is required when GARNET_TLS_ROTATION=true}"
        : "${GARNET_SERVER_CERT_WRONG_NAME_FILE:?GARNET_SERVER_CERT_WRONG_NAME_FILE is required when GARNET_TLS_ROTATION=true}"
        : "${GARNET_SERVER_CERT_EXPIRED_FILE:?GARNET_SERVER_CERT_EXPIRED_FILE is required when GARNET_TLS_ROTATION=true}"
        : "${GARNET_CERT_REFRESH_SECONDS:?GARNET_CERT_REFRESH_SECONDS is required when GARNET_TLS_ROTATION=true}"
        if ! [[ "$GARNET_CERT_REFRESH_SECONDS" =~ ^[1-9][0-9]*$ ]]; then
            echo "GARNET_CERT_REFRESH_SECONDS must be a positive integer for rotation smoke" >&2
            exit 2
        fi
    fi
    compose_files+=("$tls_compose_file")
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
if [[ "${PUBLIC_UI_SMOKE_ONLY:-0}" == "1" ||
      "${AUTHENTICATED_UI_SMOKE_ONLY:-0}" == "1" ]] && ! command -v npm >/dev/null 2>&1; then
    echo "npm is required for PUBLIC_UI_SMOKE_ONLY" >&2
    exit 2
fi

compose() {
    local compose_arguments=(--project-name "$project")
    for file in "${compose_files[@]}"; do
        compose_arguments+=(--file "$file")
    done
    "$container_cli" compose "${compose_arguments[@]}" "$@"
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

start_platform_after_fault() {
    # Podman Compose may leave an exited container stopped even with
    # restart: on-failure. Start the same container so the SQL claim and
    # fault marker survive the recovery boundary.
    compose start platform-silo >/dev/null
    wait_for "Platform recovery after fault hook" 90 compose exec -T platform-silo \
        curl -fsS http://127.0.0.1:5000/ready >/dev/null
}

cleanup() {
    local status=$?
    set +e
    if (( status != 0 )); then
        echo "Smoke test failed; final container state:" >&2
        compose ps >&2
        compose logs --tail 200 >&2
    fi
    for extra_container in "$secondary_platform_container" "$secondary_gateway_container"; do
        if [[ -n "$extra_container" ]]; then
            "$container_cli" rm -f "$extra_container" >/dev/null 2>&1
        fi
    done
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
export GARNET_TLS="${GARNET_TLS:-false}"
export CONTENT_CLASSIFIER_OPENAI_ENDPOINT="${CONTENT_CLASSIFIER_OPENAI_ENDPOINT:-http://provider-mock:8081/v1/moderations}"
export CONTENT_CLASSIFIER_OPENAI_API_KEY="${CONTENT_CLASSIFIER_OPENAI_API_KEY:-mock-openai-moderation-key}"
export CONTENT_CLASSIFIER_OPENAI_ALLOW_INSECURE="${CONTENT_CLASSIFIER_OPENAI_ALLOW_INSECURE:-true}"
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
backup_restore_db="platform_restore"
if [[ "${BACKUP_RESTORE_SMOKE_ONLY:-0}" == "1" ]]; then
    export BACKUP_RESTORE_TARGET_CONNECTION="Host=postgres;Database=${backup_restore_db};Username=${POSTGRES_USER};Password=${POSTGRES_PASSWORD}"
fi
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
embedding_jina_request_id="smoke-embeddings-jina-${suffix}"
embedding_jina_idempotency_key="smoke-embeddings-jina-idem-${suffix}"
embedding_gemini_request_id="smoke-embeddings-gemini-${suffix}"
embedding_gemini_idempotency_key="smoke-embeddings-gemini-idem-${suffix}"
embedding_invalid_request_id="smoke-embeddings-invalid-${suffix}"
embedding_invalid_idempotency_key="smoke-embeddings-invalid-idem-${suffix}"
responses_request_id="smoke-responses-${suffix}"
responses_idempotency_key="smoke-responses-idem-${suffix}"
responses_get_request_id="smoke-responses-get-${suffix}"
responses_input_items_request_id="smoke-responses-input-items-${suffix}"
responses_cancel_request_id="smoke-responses-cancel-${suffix}"
responses_delete_request_id="smoke-responses-delete-${suffix}"
responses_malformed_request_id="smoke-responses-malformed-${suffix}"
responses_malformed_idempotency_key="smoke-responses-malformed-idem-${suffix}"
responses_stream_request_id="smoke-responses-stream-${suffix}"
responses_stream_idempotency_key="smoke-responses-stream-idem-${suffix}"
responses_compact_request_id="smoke-responses-compact-${suffix}"
responses_compact_idempotency_key="smoke-responses-compact-idem-${suffix}"
responses_compact_stream_request_id="smoke-responses-compact-stream-${suffix}"
responses_compact_stream_idempotency_key="smoke-responses-compact-stream-idem-${suffix}"
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
realtime_soak_prefix="smoke-realtime-soak-${suffix}"
realtime_soak_count=4
realtime_soak_hold_seconds=3
anthropic_request_id="smoke-anthropic-${suffix}"
anthropic_count_request_id="smoke-anthropic-count-${suffix}"
anthropic_stream_request_id="smoke-anthropic-stream-${suffix}"
gemini_models_request_id="smoke-gemini-models-${suffix}"
gemini_request_id="smoke-gemini-${suffix}"
gemini_stream_request_id="smoke-gemini-stream-${suffix}"
fault_request_prefix="smoke-fault-${suffix}"
media_idempotency_key="smoke-media-idem-${suffix}"
media_restart_idempotency_key="smoke-media-restart-idem-${suffix}"
media_batch_idempotency_key="smoke-media-batch-idem-${suffix}"
media_batch_cancel_idempotency_key="smoke-media-batch-cancel-idem-${suffix}"
chat_price_version="smoke-chat-${suffix}-v1"
embedding_price_version="smoke-embeddings-${suffix}-v1"
embedding_jina_price_version="smoke-embeddings-jina-${suffix}-v1"
embedding_gemini_price_version="smoke-embeddings-gemini-${suffix}-v1"
media_price_version="smoke-media-${suffix}-v1"
balance_idempotency_key="smoke-balance-${suffix}"
gateway_hook_unknown_incidents=0
gateway_hook_safe_expiry=0
platform_dispatch_fault_safe_expiry=0
platform_dispatch_retry=0
platform_worker_reclaim=0
platform_fault_handled=0
garnet_tls_rotation_passed=0

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

db_target_query() {
    compose exec -T postgres psql --no-psqlrc --tuples-only --no-align \
        --set ON_ERROR_STOP=1 --username "$POSTGRES_USER" --dbname "$backup_restore_db" \
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

if [[ "${PUBLIC_UI_SMOKE_ONLY:-0}" == "1" ]]; then
    PUBLIC_UI_BASE_URL="$user_web_url" npm --prefix "$repo_root/user-web" run test:e2e -- \
        tests/public-pages.live.spec.ts
    echo "PASS: source-built User Web public catalog, readiness, terms, and privacy routes"
    exit 0
fi

expected_migrations="$((1 + $(find "$repo_root/deploy/migrations" -maxdepth 1 -type f -name '*.sql' | wc -l)))"
migration_count="$(db_query "SELECT count(*) FROM schema_migrations;")"
assert_equals "$expected_migrations" "$migration_count" "Applied migration count"
second_migration_output="$(compose run --rm migrate 2>&1)"
second_skip_count="$(grep -cE 'skip .+\.sql' <<<"$second_migration_output" || true)"
assert_equals "$expected_migrations" "$second_skip_count" "Idempotent migrator skip count"

login_response="$(admin_request POST /admin/auth/login \
    "$(jq -cn --arg username "$ADMIN_USERNAME" --arg password "$ADMIN_PASSWORD" \
        '{username:$username,password:$password}')")"
admin_token="$(jq -er '.token' <<<"$login_response")"

seed_response="$(admin_request POST /admin/seed/provider-mock-suite '{}' "$admin_token")"
openai_group_id="$(jq -er '.providers[] | select(.provider == "openai") | .group_id' \
    <<<"$seed_response")"
openai_account_id="$(jq -er '.providers[] | select(.provider == "openai") | .account_id' \
    <<<"$seed_response")"
anthropic_group_id="$(jq -er '.providers[] | select(.provider == "anthropic") | .group_id' \
    <<<"$seed_response")"
gemini_group_id="$(jq -er '.providers[] | select(.provider == "gemini") | .group_id' \
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
fault_disconnect_before_output_group_id="$(jq -er '.scenarios[] | select(.scenario == "disconnect_before_output") | .group_id' \
    <<<"$fault_seed_response")"
fault_disconnect_after_usage_group_id="$(jq -er '.scenarios[] | select(.scenario == "disconnect_after_usage") | .group_id' \
    <<<"$fault_seed_response")"
fault_client_disconnect_group_id="$(jq -er '.scenarios[] | select(.scenario == "client_disconnect") | .group_id' \
    <<<"$fault_seed_response")"
fault_malformed_group_id="$(jq -er '.scenarios[] | select(.scenario == "malformed_usage") | .group_id' \
    <<<"$fault_seed_response")"
fault_invalid_content_type_group_id="$(jq -er '.scenarios[] | select(.scenario == "invalid_content_type") | .group_id' \
    <<<"$fault_seed_response")"
assert_equals "10" "$(jq -er '.scenarios | length' <<<"$fault_seed_response")" \
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

if [[ "${BACKUP_RESTORE_SMOKE_ONLY:-0}" == "1" ]]; then
    target_exists="$(compose exec -T postgres psql --no-psqlrc --tuples-only --no-align \
        --set ON_ERROR_STOP=1 --username "$POSTGRES_USER" --dbname postgres \
        --command "SELECT 1 FROM pg_database WHERE datname = '$backup_restore_db';" | tr -d '\r')"
    if [[ "$target_exists" != "1" ]]; then
        compose exec -T postgres psql --no-psqlrc --set ON_ERROR_STOP=1 \
            --username "$POSTGRES_USER" --dbname postgres \
            --command "CREATE DATABASE $backup_restore_db;" >/dev/null
    fi

    backup_key="smoke-backup-${suffix}-idem"
    backup_response="$(admin_request POST /admin/backups/ \
        '{"kind":"postgres","retentionDays":14}' "$admin_token" "$backup_key")"
    backup_id="$(jq -er '.id' <<<"$backup_response")"
    assert_equals "completed" "$(jq -er '.status' <<<"$backup_response")" \
        "PostgreSQL backup completion"
    jq -er '.sizeBytes > 0 and (.sha256 | test("^[0-9a-f]{64}$"))' \
        <<<"$backup_response" >/dev/null
    backup_replay="$(admin_request POST /admin/backups/ \
        '{"kind":"postgres","retentionDays":14}' "$admin_token" "$backup_key")"
    assert_equals "$backup_id" "$(jq -er '.id' <<<"$backup_replay")" \
        "Backup idempotent replay"

    restore_key="smoke-restore-${suffix}-idem"
    restore_response="$(admin_request POST "/admin/backups/$backup_id/restore" '{}' \
        "$admin_token" "$restore_key")"
    assert_equals "completed" "$(jq -er '.status' <<<"$restore_response")" \
        "Isolated PostgreSQL restore completion"
    restore_replay="$(admin_request POST "/admin/backups/$backup_id/restore" '{}' \
        "$admin_token" "$restore_key")"
    assert_equals "$(jq -er '.id' <<<"$restore_response")" \
        "$(jq -er '.id' <<<"$restore_replay")" "Restore idempotent replay"
    assert_equals "1" "$(db_target_query "SELECT count(*) FROM user_accounts WHERE email = '$user_email';")" \
        "Restored user data in isolated target"
    echo "PASS: idempotent PostgreSQL backup, checksum, and isolated restore"
    exit 0
fi

if [[ "${AUTHENTICATED_UI_SMOKE_ONLY:-0}" == "1" ]]; then
    PUBLIC_UI_BASE_URL="$user_web_url" \
    PUBLIC_UI_USER_EMAIL="$user_email" \
    PUBLIC_UI_USER_PASSWORD="$user_password" \
        npm --prefix "$repo_root/user-web" run test:e2e -- \
            tests/public-pages.live.spec.ts tests/authenticated-portal.live.spec.ts
    echo "PASS: source-built User Web public and authenticated portal routes"
    exit 0
fi

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
    --argjson anthropic "$anthropic_group_id" \
    --argjson gemini "$gemini_group_id" \
    --argjson fault429 "$fault_429_group_id" \
    --argjson fault500 "$fault_500_group_id" \
    --argjson timeout "$fault_timeout_group_id" \
    --argjson disconnect "$fault_disconnect_group_id" \
    --argjson disconnectStream "$fault_disconnect_stream_group_id" \
    --argjson disconnectBeforeOutput "$fault_disconnect_before_output_group_id" \
    --argjson disconnectAfterUsage "$fault_disconnect_after_usage_group_id" \
    --argjson clientDisconnect "$fault_client_disconnect_group_id" \
    --argjson malformed "$fault_malformed_group_id" \
    --argjson invalidContentType "$fault_invalid_content_type_group_id" \
    '[$openai,$anthropic,$gemini,$fault429,$fault500,$timeout,$disconnect,$disconnectStream,$disconnectBeforeOutput,$disconnectAfterUsage,$clientDisconnect,$malformed,$invalidContentType]')"
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
anthropic_api_key="$(create_api_key "$anthropic_group_id")"
gemini_api_key="$(create_api_key "$gemini_group_id")"
content_policy_pattern="greenfield-policy-${suffix}"
content_policy_request_id="smoke-content-policy-${suffix}"
content_policy_rule="$(admin_request POST /admin/content-audit/rules \
    "$(jq -cn --arg pattern "$content_policy_pattern" \
        '{pattern:$pattern,actionType:"block",scope:"chat_completions",status:"active",stage:"request"}')" \
    "$admin_token")"
content_policy_rule_id="$(jq -er '.id' <<<"$content_policy_rule")"
content_policy_change_propagated() {
    [[ "$(db_query "SELECT count(*) FROM content_policy_change_events WHERE rule_id = $content_policy_rule_id AND action = 'created' AND propagated_at IS NOT NULL;")" == "1" ]]
}
platform_policy_faulted() {
    local container_id
    container_id="$(service_container_id platform-silo 2>/dev/null || true)"
    [[ -n "$container_id" ]] && [[ "$($container_cli inspect --format '{{.State.Status}}' "$container_id" 2>/dev/null)" != "running" ]]
}
platform_policy_fault_claimed() {
    compose exec -T platform-silo test -f \
        "/var/run/scalaapi/fault-hooks/platform-after-policy-outbox-claim.claimed"
}
if [[ "${PLATFORM_FAULT_HOOK:-}" == "platform.after_policy_outbox_claim" ]]; then
    wait_for "Platform policy outbox claim fault" 45 platform_policy_faulted
    start_platform_after_fault
    wait_for "Platform policy outbox claim marker" 30 platform_policy_fault_claimed
    platform_fault_handled=1
    echo "PASS: Platform policy outbox claim was reclaimed after process restart"
fi
wait_for "content policy change propagation" 30 content_policy_change_propagated
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
content_policy_alerts="$(admin_request GET "/admin/content-audit/alerts?requestId=$content_policy_request_id" '' "$admin_token")"
assert_equals "1|policy_block|warning|content_policy_blocked" \
    "$(jq -r '[.total, .items[0].kind, .items[0].severity, .items[0].code] | @tsv' <<<"$content_policy_alerts" | tr '\t' '|')" \
    "Content policy operational alert evidence"
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
scoped_api_key_audit_state=""
scoped_api_key_audit_ready() {
    scoped_api_key_audit_state="$(db_query "
SELECT
  (SELECT count(*) FROM api_key_audit_events WHERE api_key_id = $scoped_api_key_id AND action = 'updated') || '|' ||
  (SELECT count(*) FROM api_key_audit_events WHERE api_key_id = $scoped_api_key_id AND action = 'revoked') || '|' ||
  (SELECT count(*) FROM request_leases WHERE request_id = '$scoped_updated_request_id' AND status = 'completed');")"
    [[ "$scoped_api_key_audit_state" == "1|1|1" ]]
}
wait_for "API-key update/revoke audit persistence" 30 scoped_api_key_audit_ready
assert_equals "1|1|1" "$scoped_api_key_audit_state" \
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
fault_disconnect_before_output_api_key="$(create_api_key "$fault_disconnect_before_output_group_id")"
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
    "$(jq -cn --arg version "$embedding_jina_price_version" --arg model jina-embeddings-v5-text-small \
        --arg from "$effective_from" \
        '{version:$version,model:$model,inputUsdPerMillion:0.12,outputUsdPerMillion:0,cacheReadUsdPerMillion:0,cacheWriteUsdPerMillion:0,effectiveFrom:$from,effectiveUntil:null}')" \
    "$admin_token" >/dev/null
admin_request POST /admin/pricing/versions \
    "$(jq -cn --arg version "$embedding_gemini_price_version" --arg model gemini-embedding-001 \
        --arg from "$effective_from" \
        '{version:$version,model:$model,inputUsdPerMillion:0.08,outputUsdPerMillion:0,cacheReadUsdPerMillion:0,cacheWriteUsdPerMillion:0,effectiveFrom:$from,effectiveUntil:null}')" \
    "$admin_token" >/dev/null
admin_request POST /admin/pricing/versions \
    "$(jq -cn --arg version "$media_price_version" --arg model mock-image-1 \
        --arg from "$effective_from" \
        '{version:$version,model:$model,inputUsdPerMillion:0,outputUsdPerMillion:0,cacheReadUsdPerMillion:0,cacheWriteUsdPerMillion:0,effectiveFrom:$from,effectiveUntil:null}')" \
    "$admin_token" >/dev/null

if [[ "${MULTI_PROCESS_METRICS_SMOKE_ONLY:-0}" == "1" ]]; then
    metric_rule_response="$(admin_request POST /admin/content-audit/rules \
        '{"pattern":"mock response","actionType":"block","scope":"chat_completions","status":"active","stage":"response","classifier":"openai","redactContent":true}' \
        "$admin_token")"
    metric_rule_id="$(jq -er '.id' <<<"$metric_rule_response")"
    metric_rule_propagated() {
        [[ "$(db_query "SELECT count(*) FROM content_policy_change_events WHERE rule_id = $metric_rule_id AND action = 'created' AND propagated_at IS NOT NULL;")" == "1" ]]
    }
    wait_for "multi-process metric policy propagation" 30 metric_rule_propagated
    assert_equals "0" "$(db_query "SELECT count(*) FROM content_classifier_metric_snapshots;")" \
        "Empty classifier snapshot baseline"

    secondary_socket="/var/run/scalaapi/dispatch-metrics.sock"
    secondary_platform_container="${project}_platform-metrics-2"
    compose run --detach --no-deps --name "$secondary_platform_container" \
        -e "CapnpRpc__SocketPath=$secondary_socket" \
        -e "ASPNETCORE_URLS=http://0.0.0.0:5002" platform-silo >/dev/null
    wait_for "secondary Platform readiness" 120 "$container_cli" exec \
        "$secondary_platform_container" curl -fsS http://127.0.0.1:5002/ready >/dev/null
    secondary_silos_ready() {
        [[ "$(db_query "SELECT count(*) FROM OrleansMembershipTable WHERE DeploymentId = 'platform' AND Status = 3;")" -ge 2 ]]
    }
    wait_for "two active Platform silos" 60 secondary_silos_ready

    secondary_gateway_container="${project}_gateway-metrics-2"
    compose run --detach --no-deps --name "$secondary_gateway_container" \
        -e "CAPNP_UDS_PATH=$secondary_socket" \
        -e GATEWAY_USAGE_DB=/var/lib/scalaapi/metrics-usage-outbox.db gateway >/dev/null
    wait_for "secondary Gateway readiness" 90 "$container_cli" exec \
        "$secondary_gateway_container" curl -fsS http://127.0.0.1:8080/ready >/dev/null

    secondary_chat_request() {
        local request_id=$1
        "$container_cli" exec "$secondary_gateway_container" curl -sS --max-time 30 \
            --write-out $'\n%{http_code}' http://127.0.0.1:8080/v1/chat/completions \
            -H "Authorization: Bearer $api_key" \
            -H "Content-Type: application/json" \
            -H "X-Request-ID: $request_id" \
            -H "Idempotency-Key: ${request_id}-idem" \
            --data '{"model":"gpt-4o","messages":[{"role":"user","content":"openai-moderation-flag-marker"}],"stream":false}'
    }

    metric_request_one="smoke-metric-process-one-${suffix}"
    metric_response="$(secondary_chat_request "$metric_request_one")"
    assert_equals "400" "${metric_response##*$'\n'}" \
        "Secondary Platform OpenAI classifier response"
    metric_snapshot_state() {
        db_query "SELECT count(*) || '|' || coalesce(sum(requests), 0) || '|' || count(DISTINCT instance_id) FROM content_classifier_metric_snapshots;"
    }
    first_metric_snapshot_ready() {
        [[ "$(metric_snapshot_state)" == "1|1|1" ]]
    }
    wait_for "first runtime classifier snapshot" 30 first_metric_snapshot_ready
    assert_equals "1|1|1" "$(metric_snapshot_state)" \
        "First runtime classifier snapshot"

    "$container_cli" restart "$secondary_platform_container" >/dev/null
    wait_for "secondary Platform readiness after restart" 120 "$container_cli" exec \
        "$secondary_platform_container" curl -fsS http://127.0.0.1:5002/ready >/dev/null
    "$container_cli" restart "$secondary_gateway_container" >/dev/null
    wait_for "secondary Gateway readiness after restart" 90 "$container_cli" exec \
        "$secondary_gateway_container" curl -fsS http://127.0.0.1:8080/ready >/dev/null

    metric_request_two="smoke-metric-process-two-${suffix}"
    metric_response="$(secondary_chat_request "$metric_request_two")"
    assert_equals "400" "${metric_response##*$'\n'}" \
        "Restarted Platform OpenAI classifier response"
    restarted_metric_snapshot_ready() {
        [[ "$(metric_snapshot_state)" == "2|2|2" ]]
    }
    wait_for "restarted runtime classifier snapshot" 30 restarted_metric_snapshot_ready
    assert_equals "2|2|2" "$(metric_snapshot_state)" \
        "Cross-process classifier snapshot aggregation"
    assert_equals "2|2|2|2" "$(db_query "
SELECT count(*) || '|' || count(DISTINCT instance_id) || '|' ||
       count(*) FILTER (WHERE sequence = 1) || '|' || coalesce(sum(requests), 0)
FROM content_classifier_metric_snapshots;")" \
        "Restarted instance sequence and request totals"
    assert_equals "2|2|2" "$(db_query "
SELECT (SELECT count(*) FROM request_leases
        WHERE request_id IN ('$metric_request_one', '$metric_request_two')) || '|' ||
       (SELECT count(*) FROM usage_events WHERE request_id IN ('$metric_request_one', '$metric_request_two')) || '|' ||
       (SELECT count(*) FROM balance_ledger b JOIN request_leases l USING (lease_token)
        WHERE l.request_id IN ('$metric_request_one', '$metric_request_two') AND b.entry_type = 'usage_debit');")" \
        "Runtime classifier audit and exactly-once settlement"

    multi_gateway_idempotency_key="smoke-multi-gateway-idem-${suffix}"
    multi_gateway_request_primary="smoke-multi-gateway-primary-${suffix}"
    multi_gateway_request_secondary="smoke-multi-gateway-secondary-${suffix}"
    multi_gateway_body='{"model":"gpt-4o","messages":[{"role":"user","content":"multi gateway idempotency"}],"stream":false}'
    multi_gateway_tmp_dir="$(mktemp -d)"
    primary_multi_response="$multi_gateway_tmp_dir/primary"
    secondary_multi_response="$multi_gateway_tmp_dir/secondary"
    curl -sS --max-time 30 --write-out $'\n%{http_code}' "$gateway_url/v1/chat/completions" \
        -H "Authorization: Bearer $api_key" \
        -H "Content-Type: application/json" \
        -H "X-Request-ID: $multi_gateway_request_primary" \
        -H "Idempotency-Key: $multi_gateway_idempotency_key" \
        --data "$multi_gateway_body" >"$primary_multi_response" &
    primary_multi_pid=$!
    "$container_cli" exec "$secondary_gateway_container" curl -sS --max-time 30 \
        --write-out $'\n%{http_code}' http://127.0.0.1:8080/v1/chat/completions \
        -H "Authorization: Bearer $api_key" \
        -H "Content-Type: application/json" \
        -H "X-Request-ID: $multi_gateway_request_secondary" \
        -H "Idempotency-Key: $multi_gateway_idempotency_key" \
        --data "$multi_gateway_body" >"$secondary_multi_response" &
    secondary_multi_pid=$!
    wait "$primary_multi_pid"
    wait "$secondary_multi_pid"
    primary_multi_status="$(tail -n 1 "$primary_multi_response")"
    secondary_multi_status="$(tail -n 1 "$secondary_multi_response")"
    assert_one_of "200|409" "$primary_multi_status" \
        "Primary Gateway shared-idempotency response"
    assert_one_of "200|409" "$secondary_multi_status" \
        "Secondary Gateway shared-idempotency response"
    multi_gateway_settled() {
        [[ "$(db_query "
SELECT (SELECT count(*) FROM request_idempotency
        WHERE idempotency_key = '$multi_gateway_idempotency_key' AND status = 'completed') || '|' ||
       (SELECT count(*) FROM request_leases
        WHERE lease_token = (SELECT lease_token FROM request_idempotency
                             WHERE idempotency_key = '$multi_gateway_idempotency_key')) || '|' ||
       (SELECT count(*) FROM usage_events
        WHERE lease_token = (SELECT lease_token FROM request_idempotency
                             WHERE idempotency_key = '$multi_gateway_idempotency_key')) || '|' ||
       (SELECT count(*) FROM balance_ledger
        WHERE lease_token = (SELECT lease_token FROM request_idempotency
                             WHERE idempotency_key = '$multi_gateway_idempotency_key')
          AND entry_type = 'usage_debit');")" == "1|1|1|1" ]]
    }
    wait_for "cross-Gateway shared-idempotency settlement" 45 multi_gateway_settled
    assert_equals "1|1|1|1" "$(db_query "
SELECT (SELECT count(*) FROM request_idempotency
        WHERE idempotency_key = '$multi_gateway_idempotency_key' AND status = 'completed') || '|' ||
       (SELECT count(*) FROM request_leases
        WHERE lease_token = (SELECT lease_token FROM request_idempotency
                             WHERE idempotency_key = '$multi_gateway_idempotency_key')) || '|' ||
       (SELECT count(*) FROM usage_events
        WHERE lease_token = (SELECT lease_token FROM request_idempotency
                             WHERE idempotency_key = '$multi_gateway_idempotency_key')) || '|' ||
       (SELECT count(*) FROM balance_ledger
        WHERE lease_token = (SELECT lease_token FROM request_idempotency
                             WHERE idempotency_key = '$multi_gateway_idempotency_key')
          AND entry_type = 'usage_debit');")" \
        "Cross-Gateway exactly-once lease, usage, and debit"
    rm -rf "$multi_gateway_tmp_dir"

    rolling_primary_request="smoke-rolling-primary-${suffix}"
    rolling_secondary_request="smoke-rolling-secondary-${suffix}"
    rolling_body='{"model":"gpt-4o","messages":[{"role":"user","content":"rolling availability"}],"stream":false}'
    "$container_cli" stop "$secondary_gateway_container" "$secondary_platform_container" >/dev/null
    wait_for "primary Gateway readiness during secondary outage" 30 \
        curl -fsS "$gateway_url/ready" >/dev/null
    one_active_silo() {
        [[ "$(db_query "SELECT count(*) FROM OrleansMembershipTable WHERE DeploymentId = 'platform' AND Status = 3;")" == "1" ]]
    }
    wait_for "secondary Silo removal" 60 one_active_silo
    rolling_primary_response="$(curl -sS --max-time 30 --write-out $'\n%{http_code}' \
        "$gateway_url/v1/chat/completions" \
        -H "Authorization: Bearer $api_key" \
        -H "Content-Type: application/json" \
        -H "X-Request-ID: $rolling_primary_request" \
        -H "Idempotency-Key: ${rolling_primary_request}-idem" \
        --data "$rolling_body")"
    assert_equals "200" "${rolling_primary_response##*$'\n'}" \
        "Primary Gateway response during secondary outage"

    "$container_cli" start "$secondary_platform_container" >/dev/null
    wait_for "secondary Platform readiness after rejoin" 120 "$container_cli" exec \
        "$secondary_platform_container" curl -fsS http://127.0.0.1:5002/ready >/dev/null
    wait_for "two active Platform silos after rejoin" 60 secondary_silos_ready
    "$container_cli" start "$secondary_gateway_container" >/dev/null
    wait_for "secondary Gateway readiness after rejoin" 90 "$container_cli" exec \
        "$secondary_gateway_container" curl -fsS http://127.0.0.1:8080/ready >/dev/null
    rolling_secondary_response="$("$container_cli" exec "$secondary_gateway_container" \
        curl -sS --max-time 30 --write-out $'\n%{http_code}' \
        http://127.0.0.1:8080/v1/chat/completions \
        -H "Authorization: Bearer $api_key" \
        -H "Content-Type: application/json" \
        -H "X-Request-ID: $rolling_secondary_request" \
        -H "Idempotency-Key: ${rolling_secondary_request}-idem" \
        --data "$rolling_body")"
    assert_equals "200" "${rolling_secondary_response##*$'\n'}" \
        "Secondary Gateway response after Silo rejoin"
    rolling_settled() {
        [[ "$(db_query "
SELECT (SELECT count(*) FROM request_leases
        WHERE request_id IN ('$rolling_primary_request', '$rolling_secondary_request')
          AND status = 'completed') || '|' ||
       (SELECT count(*) FROM usage_events
        WHERE request_id IN ('$rolling_primary_request', '$rolling_secondary_request')) || '|' ||
       (SELECT count(*) FROM balance_ledger b JOIN request_leases l USING (lease_token)
        WHERE l.request_id IN ('$rolling_primary_request', '$rolling_secondary_request')
          AND b.entry_type = 'usage_debit');")" == "2|2|2" ]]
    }
    wait_for "rolling Silo/Gateway settlement" 45 rolling_settled
    assert_equals "2|2|2" "$(db_query "
SELECT (SELECT count(*) FROM request_leases
        WHERE request_id IN ('$rolling_primary_request', '$rolling_secondary_request')
          AND status = 'completed') || '|' ||
       (SELECT count(*) FROM usage_events
        WHERE request_id IN ('$rolling_primary_request', '$rolling_secondary_request')) || '|' ||
       (SELECT count(*) FROM balance_ledger b JOIN request_leases l USING (lease_token)
        WHERE l.request_id IN ('$rolling_primary_request', '$rolling_secondary_request')
          AND b.entry_type = 'usage_debit');")" \
        "Rolling Silo/Gateway settlement before and after rejoin"
    admin_request DELETE "/admin/content-audit/rules/$metric_rule_id" "" "$admin_token" >/dev/null
    echo "PASS: two Platform/Gateway pairs preserved idempotency and settlement across restart, outage, and rejoin"
    exit 0
fi

sleep 6
chat_body='{"model":"gpt-4o","messages":[{"role":"user","content":"greenfield compose smoke"}],"stream":false}'
unicode_policy_request_id="smoke-unicode-policy-${suffix}"
unicode_policy_rule="$(admin_request POST /admin/content-audit/rules \
    '{"pattern":"sensitive","actionType":"block","scope":"chat_completions","status":"active","stage":"request","redactContent":true}' \
    "$admin_token")"
unicode_policy_rule_id="$(jq -er '.id' <<<"$unicode_policy_rule")"
unicode_policy_change_propagated() {
    [[ "$(db_query "SELECT count(*) FROM content_policy_change_events WHERE rule_id = $unicode_policy_rule_id AND action = 'created' AND propagated_at IS NOT NULL;")" == "1" ]]
}
wait_for "Unicode policy change propagation" 30 unicode_policy_change_propagated
unicode_policy_body="$(jq -cn \
    '{model:"gpt-4o",messages:[{role:"user",content:"ＳｅＮѕіtіνｅ request"}],stream:false}')"
unicode_policy_response="$(curl -sS --max-time 20 --write-out $'\n%{http_code}' \
    "$gateway_url/v1/chat/completions" \
    -H "Authorization: Bearer $api_key" \
    -H "Content-Type: application/json" \
    -H "X-Request-ID: $unicode_policy_request_id" \
    -H "Idempotency-Key: ${unicode_policy_request_id}-idem" \
    --data "$unicode_policy_body")"
assert_equals "400" "${unicode_policy_response##*$'\n'}" \
    "Unicode-normalized request policy status"
jq -e '.error.type == "content_policy_violation"' \
    <<<"${unicode_policy_response%$'\n'*}" >/dev/null
assert_equals "1|[REDACTED]|unicode-confusable-v1|true|0" "$(db_query "
SELECT
  (SELECT count(*) FROM content_audit_logs WHERE request_id = '$unicode_policy_request_id' AND action = 'block' AND classifier = 'local') || '|' ||
  (SELECT max(content_snippet) FROM content_audit_logs WHERE request_id = '$unicode_policy_request_id') || '|' ||
  (SELECT max(evaluator_version) FROM content_audit_logs WHERE request_id = '$unicode_policy_request_id') || '|' ||
  (SELECT coalesce(bool_or(content_redacted), false) FROM content_audit_logs WHERE request_id = '$unicode_policy_request_id') || '|' ||
  (SELECT count(*) FROM request_leases WHERE request_id = '$unicode_policy_request_id');")" \
  "Unicode normalization, redaction, and request no-lease invariants"
unicode_policy_alerts="$(admin_request GET "/admin/content-audit/alerts?requestId=$unicode_policy_request_id" '' "$admin_token")"
assert_equals "1|policy_block|warning|content_policy_blocked" \
    "$(jq -r '[.total, .items[0].kind, .items[0].severity, .items[0].code] | @tsv' <<<"$unicode_policy_alerts" | tr '\t' '|')" \
    "Unicode policy operational alert evidence"
admin_request DELETE "/admin/content-audit/rules/$unicode_policy_rule_id" "" "$admin_token" >/dev/null
echo "PASS: versioned Unicode normalization and redacted request audit"
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

external_policy_request_id="smoke-external-classifier-match-${suffix}"
external_policy_rule="$(admin_request POST /admin/content-audit/rules \
    '{"pattern":"mock response","actionType":"block","scope":"chat_completions","status":"active","stage":"response","classifier":"external","redactContent":true}' \
    "$admin_token")"
external_policy_rule_id="$(jq -er '.id' <<<"$external_policy_rule")"
external_policy_change_propagated() {
    [[ "$(db_query "SELECT count(*) FROM content_policy_change_events WHERE rule_id = $external_policy_rule_id AND action = 'created' AND propagated_at IS NOT NULL;")" == "1" ]]
}
wait_for "external classifier policy change propagation" 30 external_policy_change_propagated
external_policy_body='{"model":"gpt-4o","messages":[{"role":"user","content":"external classifier match"}],"stream":false}'
external_policy_response="$(curl -sS --max-time 20 --write-out $'\n%{http_code}' \
    "$gateway_url/v1/chat/completions" \
    -H "Authorization: Bearer $api_key" \
    -H "Content-Type: application/json" \
    -H "X-Request-ID: $external_policy_request_id" \
    -H "Idempotency-Key: ${external_policy_request_id}-idem" \
    --data "$external_policy_body")"
assert_equals "400" "${external_policy_response##*$'\n'}" \
    "External classifier match response policy status"
jq -e '.error.type == "content_policy_violation"' \
    <<<"${external_policy_response%$'\n'*}" >/dev/null
external_policy_state=""
for attempt in $(seq 1 30); do
    external_policy_state="$(db_query "
SELECT
  (SELECT count(*) FROM content_audit_logs WHERE request_id = '$external_policy_request_id' AND classifier = 'external' AND content_redacted) || '|' ||
  (SELECT max(content_snippet) FROM content_audit_logs WHERE request_id = '$external_policy_request_id' AND classifier = 'external') || '|' ||
  (SELECT count(*) FROM request_leases WHERE request_id = '$external_policy_request_id' AND status = 'completed') || '|' ||
  (SELECT count(*) FROM usage_events WHERE request_id = '$external_policy_request_id') || '|' ||
  (SELECT count(*) FROM balance_ledger b JOIN request_leases l USING (lease_token) WHERE l.request_id = '$external_policy_request_id' AND b.entry_type = 'usage_debit') || '|' ||
  (SELECT count(*) FROM request_idempotency WHERE idempotency_key = '${external_policy_request_id}-idem' AND status = 'completed' AND response_status_code = 400);")"
    [[ "$external_policy_state" == "1|[REDACTED]|1|1|1|1" ]] && break
    sleep 1
done
assert_equals "1|[REDACTED]|1|1|1|1" "$external_policy_state" \
    "External classifier match audit and normal settlement invariants"
external_policy_alerts="$(admin_request GET "/admin/content-audit/alerts?requestId=$external_policy_request_id&ruleId=$external_policy_rule_id&kind=policy_block" '' "$admin_token")"
assert_equals "1|policy_block|warning|content_policy_blocked" \
    "$(jq -r '[.total, .items[0].kind, .items[0].severity, .items[0].code] | @tsv' <<<"$external_policy_alerts" | tr '\t' '|')" \
    "External classifier match alert evidence"
admin_request DELETE "/admin/content-audit/rules/$external_policy_rule_id" "" "$admin_token" >/dev/null
external_policy_request_id="smoke-external-classifier-outage-${suffix}"
external_policy_rule="$(admin_request POST /admin/content-audit/rules \
    '{"pattern":"external-classifier-outage-marker","actionType":"block","scope":"chat_completions","status":"active","stage":"response","classifier":"external","redactContent":true}' \
    "$admin_token")"
external_policy_rule_id="$(jq -er '.id' <<<"$external_policy_rule")"
external_policy_change_propagated() {
    [[ "$(db_query "SELECT count(*) FROM content_policy_change_events WHERE rule_id = $external_policy_rule_id AND action = 'created' AND propagated_at IS NOT NULL;")" == "1" ]]
}
wait_for "external classifier outage policy change propagation" 30 external_policy_change_propagated
external_policy_body='{"model":"gpt-4o","messages":[{"role":"user","content":"external classifier outage"}],"stream":false}'
external_policy_response="$(curl -sS --max-time 20 --write-out $'\n%{http_code}' \
    "$gateway_url/v1/chat/completions" \
    -H "Authorization: Bearer $api_key" \
    -H "Content-Type: application/json" \
    -H "X-Request-ID: $external_policy_request_id" \
    -H "Idempotency-Key: ${external_policy_request_id}-idem" \
    --data "$external_policy_body")"
assert_equals "503" "${external_policy_response##*$'\n'}" \
    "Unavailable external classifier fail-closed status"
jq -e '.error.type == "content_policy_unavailable"' \
    <<<"${external_policy_response%$'\n'*}" >/dev/null
external_policy_state=""
for attempt in $(seq 1 30); do
    external_policy_state="$(db_query "
SELECT
  (SELECT count(*) FROM content_audit_logs WHERE request_id = '$external_policy_request_id' AND classifier = 'external' AND content_redacted) || '|' ||
  (SELECT max(content_snippet) FROM content_audit_logs WHERE request_id = '$external_policy_request_id' AND classifier = 'external') || '|' ||
  (SELECT count(*) FROM request_leases WHERE request_id = '$external_policy_request_id' AND status = 'completed') || '|' ||
  (SELECT count(*) FROM usage_events WHERE request_id = '$external_policy_request_id') || '|' ||
  (SELECT count(*) FROM balance_ledger b JOIN request_leases l USING (lease_token) WHERE l.request_id = '$external_policy_request_id' AND b.entry_type = 'usage_debit') || '|' ||
  (SELECT count(*) FROM request_idempotency WHERE idempotency_key = '${external_policy_request_id}-idem' AND status = 'completed' AND response_status_code = 503);")"
    [[ "$external_policy_state" == "1|[REDACTED]|1|1|1|1" ]] && break
    sleep 1
done
assert_equals "1|[REDACTED]|1|1|1|1" "$external_policy_state" \
    "External classifier fail-closed audit and normal settlement invariants"
external_policy_alerts="$(admin_request GET "/admin/content-audit/alerts?requestId=$external_policy_request_id&ruleId=$external_policy_rule_id&kind=classifier_unavailable" '' "$admin_token")"
assert_equals "1|classifier_unavailable|critical|content_policy_classifier_unavailable" \
    "$(jq -r '[.total, .items[0].kind, .items[0].severity, .items[0].code] | @tsv' <<<"$external_policy_alerts" | tr '\t' '|')" \
    "External classifier operational alert evidence"
admin_request DELETE "/admin/content-audit/rules/$external_policy_rule_id" "" "$admin_token" >/dev/null
echo "PASS: external classifier match and outage semantics are deterministic"

openai_policy_request_id="smoke-openai-classifier-match-${suffix}"
openai_policy_rule="$(admin_request POST /admin/content-audit/rules \
    '{"pattern":"openai-policy-marker","actionType":"block","scope":"chat_completions","status":"active","stage":"response","classifier":"openai","redactContent":true}' \
    "$admin_token")"
openai_policy_rule_id="$(jq -er '.id' <<<"$openai_policy_rule")"
openai_policy_change_propagated() {
    [[ "$(db_query "SELECT count(*) FROM content_policy_change_events WHERE rule_id = $openai_policy_rule_id AND action = 'created' AND propagated_at IS NOT NULL;")" == "1" ]]
}
wait_for "OpenAI moderation policy change propagation" 30 openai_policy_change_propagated
openai_policy_body='{"model":"gpt-4o","messages":[{"role":"user","content":"openai-moderation-flag-marker"}],"stream":false}'
openai_policy_response="$(curl -sS --max-time 20 --write-out $'\n%{http_code}' \
    "$gateway_url/v1/chat/completions" \
    -H "Authorization: Bearer $api_key" \
    -H "Content-Type: application/json" \
    -H "X-Request-ID: $openai_policy_request_id" \
    -H "Idempotency-Key: ${openai_policy_request_id}-idem" \
    --data "$openai_policy_body")"
assert_equals "400" "${openai_policy_response##*$'\n'}" \
    "OpenAI moderation match response policy status"
jq -e '.error.type == "content_policy_violation"' \
    <<<"${openai_policy_response%$'\n'*}" >/dev/null
openai_policy_state=""
for attempt in $(seq 1 30); do
    openai_policy_state="$(db_query "
SELECT
  (SELECT count(*) FROM content_audit_logs WHERE request_id = '$openai_policy_request_id' AND classifier = 'openai' AND content_redacted) || '|' ||
  (SELECT max(content_snippet) FROM content_audit_logs WHERE request_id = '$openai_policy_request_id' AND classifier = 'openai') || '|' ||
  (SELECT count(*) FROM request_leases WHERE request_id = '$openai_policy_request_id' AND status = 'completed') || '|' ||
  (SELECT count(*) FROM usage_events WHERE request_id = '$openai_policy_request_id') || '|' ||
  (SELECT count(*) FROM balance_ledger b JOIN request_leases l USING (lease_token) WHERE l.request_id = '$openai_policy_request_id' AND b.entry_type = 'usage_debit') || '|' ||
  (SELECT count(*) FROM request_idempotency WHERE idempotency_key = '${openai_policy_request_id}-idem' AND status = 'completed' AND response_status_code = 400);")"
    [[ "$openai_policy_state" == "1|[REDACTED]|1|1|1|1" ]] && break
    sleep 1
done
assert_equals "1|[REDACTED]|1|1|1|1" "$openai_policy_state" \
    "OpenAI moderation match audit and normal settlement invariants"
openai_policy_alerts="$(admin_request GET "/admin/content-audit/alerts?requestId=$openai_policy_request_id&ruleId=$openai_policy_rule_id&kind=policy_block" '' "$admin_token")"
assert_equals "1|policy_block|warning|content_policy_blocked" \
    "$(jq -r '[.total, .items[0].kind, .items[0].severity, .items[0].code] | @tsv' <<<"$openai_policy_alerts" | tr '\t' '|')" \
    "OpenAI moderation match alert evidence"
admin_request DELETE "/admin/content-audit/rules/$openai_policy_rule_id" "" "$admin_token" >/dev/null
openai_policy_request_id="smoke-openai-classifier-unavailable-${suffix}"
openai_policy_rule="$(admin_request POST /admin/content-audit/rules \
    '{"pattern":"openai-moderation-unavailable-marker","actionType":"block","scope":"chat_completions","status":"active","stage":"response","classifier":"openai","redactContent":true}' \
    "$admin_token")"
openai_policy_rule_id="$(jq -er '.id' <<<"$openai_policy_rule")"
wait_for "OpenAI moderation unavailable policy change propagation" 30 openai_policy_change_propagated
openai_policy_body='{"model":"gpt-4o","messages":[{"role":"user","content":"openai-moderation-unavailable-marker"}],"stream":false}'
openai_policy_response="$(curl -sS --max-time 20 --write-out $'\n%{http_code}' \
    "$gateway_url/v1/chat/completions" \
    -H "Authorization: Bearer $api_key" \
    -H "Content-Type: application/json" \
    -H "X-Request-ID: $openai_policy_request_id" \
    -H "Idempotency-Key: ${openai_policy_request_id}-idem" \
    --data "$openai_policy_body")"
assert_equals "503" "${openai_policy_response##*$'\n'}" \
    "OpenAI moderation unavailable fail-closed status"
jq -e '.error.type == "content_policy_unavailable"' \
    <<<"${openai_policy_response%$'\n'*}" >/dev/null
openai_policy_state=""
for attempt in $(seq 1 30); do
    openai_policy_state="$(db_query "
SELECT
  (SELECT count(*) FROM content_audit_logs WHERE request_id = '$openai_policy_request_id' AND classifier = 'openai' AND content_redacted) || '|' ||
  (SELECT max(content_snippet) FROM content_audit_logs WHERE request_id = '$openai_policy_request_id' AND classifier = 'openai') || '|' ||
  (SELECT count(*) FROM request_leases WHERE request_id = '$openai_policy_request_id' AND status = 'completed') || '|' ||
  (SELECT count(*) FROM usage_events WHERE request_id = '$openai_policy_request_id') || '|' ||
  (SELECT count(*) FROM balance_ledger b JOIN request_leases l USING (lease_token) WHERE l.request_id = '$openai_policy_request_id' AND b.entry_type = 'usage_debit') || '|' ||
  (SELECT count(*) FROM request_idempotency WHERE idempotency_key = '${openai_policy_request_id}-idem' AND status = 'completed' AND response_status_code = 503);")"
    [[ "$openai_policy_state" == "1|[REDACTED]|1|1|1|1" ]] && break
    sleep 1
done
assert_equals "1|[REDACTED]|1|1|1|1" "$openai_policy_state" \
    "OpenAI moderation unavailable audit and normal settlement invariants"
openai_policy_alerts="$(admin_request GET "/admin/content-audit/alerts?requestId=$openai_policy_request_id&ruleId=$openai_policy_rule_id&kind=classifier_unavailable" '' "$admin_token")"
assert_equals "1|classifier_unavailable|critical|content_policy_classifier_unavailable" \
    "$(jq -r '[.total, .items[0].kind, .items[0].severity, .items[0].code] | @tsv' <<<"$openai_policy_alerts" | tr '\t' '|')" \
    "OpenAI moderation unavailable alert evidence"
admin_request DELETE "/admin/content-audit/rules/$openai_policy_rule_id" "" "$admin_token" >/dev/null
echo "PASS: OpenAI moderation match and unavailable semantics are deterministic"

response_stream_policy_request_id="smoke-response-stream-policy-${suffix}"
response_stream_policy_idempotency_key="${response_stream_policy_request_id}-idem"
response_stream_policy_body="$(jq -cn --arg marker "$suffix" \
    '{model:"gpt-4o",messages:[{role:"user",content:("stream response policy " + $marker)}],stream:true}')"
response_stream_policy_response="$(curl -sS --max-time 20 --write-out $'\n%{http_code}' \
    "$gateway_url/v1/chat/completions" \
    -H "Authorization: Bearer $api_key" \
    -H "Content-Type: application/json" \
    -H "X-Request-ID: $response_stream_policy_request_id" \
    -H "Idempotency-Key: $response_stream_policy_idempotency_key" \
    --data "$response_stream_policy_body")"
assert_equals "200" "${response_stream_policy_response##*$'\n'}" \
    "Streaming response policy keeps established SSE status"
grep -q '"type":"content_policy_blocked"' \
    <<<"${response_stream_policy_response%$'\n'*}"
if grep -q "mock response" <<<"${response_stream_policy_response%$'\n'*}"; then
    echo "Streaming response policy leaked Provider output" >&2
    exit 1
fi
response_stream_policy_state=""
for attempt in $(seq 1 30); do
    response_stream_policy_state="$(db_query "
SELECT
  (SELECT count(*) FROM content_audit_logs WHERE request_id = '$response_stream_policy_request_id' AND stage = 'response' AND action = 'block') || '|' ||
  (SELECT count(*) FROM request_leases WHERE request_id = '$response_stream_policy_request_id' AND status = 'reconciliation_needed') || '|' ||
  (SELECT count(*) FROM balance_holds h JOIN request_leases l USING (lease_token) WHERE l.request_id = '$response_stream_policy_request_id' AND h.status = 'active') || '|' ||
  (SELECT count(*) FROM usage_events u WHERE u.request_id = '$response_stream_policy_request_id') || '|' ||
  (SELECT count(*) FROM balance_ledger b JOIN request_leases l USING (lease_token) WHERE l.request_id = '$response_stream_policy_request_id' AND b.entry_type = 'usage_debit') || '|' ||
  (SELECT count(*) FROM request_idempotency WHERE idempotency_key = '$response_stream_policy_idempotency_key' AND status = 'reconciliation_needed');")"
    [[ "$response_stream_policy_state" == "1|1|1|0|0|1" ]] && break
    sleep 1
done
assert_equals "1|1|1|0|0|1" "$response_stream_policy_state" \
    "Streaming response policy unknown-charge invariants"
echo "PASS: streaming response policy with withheld first event and retained hold"
admin_request DELETE "/admin/content-audit/rules/$response_policy_rule_id" "" "$admin_token" >/dev/null
echo "PASS: response content policy with hidden output, normal settlement, and exact replay"
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

responses_response="$(curl -sS --max-time 30 --write-out $'\n%{http_code}' \
    "$gateway_url/v1/responses" \
    -H "Authorization: Bearer $api_key" -H "Content-Type: application/json" \
    -H "X-Request-ID: $responses_request_id" \
    -H "Idempotency-Key: $responses_idempotency_key" \
    --data '{"model":"gpt-4o","input":[{"role":"user","content":[{"type":"input_text","text":"responses json smoke"}]}],"max_output_tokens":16,"stream":false}')"
assert_equals "200" "${responses_response##*$'\n'}" \
    "OpenAI Responses JSON status"
responses_body="${responses_response%$'\n'*}"
jq -e '.object == "response" and .status == "completed" and .output_text == "mock response" and (.usage.input_tokens > 0) and (.usage.output_tokens > 0) and (.usage.total_tokens >= (.usage.input_tokens + .usage.output_tokens))' \
    <<<"$responses_body" >/dev/null
responses_id="$(jq -r '.id' <<<"$responses_body")"
[[ "$responses_id" != "null" && -n "$responses_id" ]]

responses_get_response="$(curl -sS --max-time 30 --write-out $'\n%{http_code}' \
    "$gateway_url/v1/responses/$responses_id" \
    -H "Authorization: Bearer $api_key" \
    -H "X-Request-ID: $responses_get_request_id")"
assert_equals "200" "${responses_get_response##*$'\n'}" \
    "OpenAI Responses GET subresource status"
responses_get_body="${responses_get_response%$'\n'*}"
jq -e --arg response_id "$responses_id" \
    '.object == "response" and .id == $response_id and .status == "completed" and .output_text == "mock response"' \
    <<<"$responses_get_body" >/dev/null

responses_input_items_response="$(curl -sS --max-time 30 --write-out $'\n%{http_code}' \
    "$gateway_url/v1/responses/$responses_id/input_items" \
    -H "Authorization: Bearer $api_key" \
    -H "X-Request-ID: $responses_input_items_request_id")"
assert_equals "200" "${responses_input_items_response##*$'\n'}" \
    "OpenAI Responses input_items subresource status"
responses_input_items_body="${responses_input_items_response%$'\n'*}"
jq -e --arg response_id "$responses_id" \
    '.object == "list" and .data[0].id == ($response_id + "_input_0") and .data[0].content[0].text == "responses json smoke" and .has_more == false' \
    <<<"$responses_input_items_body" >/dev/null

responses_cancel_response="$(curl -sS --max-time 30 --write-out $'\n%{http_code}' \
    -X POST "$gateway_url/v1/responses/$responses_id/cancel" \
    -H "Authorization: Bearer $api_key" -H "Content-Type: application/json" \
    -H "X-Request-ID: $responses_cancel_request_id" \
    --data '{}')"
assert_equals "200" "${responses_cancel_response##*$'\n'}" \
    "OpenAI Responses cancel subresource status"
responses_cancel_body="${responses_cancel_response%$'\n'*}"
jq -e --arg response_id "$responses_id" \
    '.object == "response" and .id == $response_id and .status == "cancelled"' \
    <<<"$responses_cancel_body" >/dev/null

responses_delete_response="$(curl -sS --max-time 30 --write-out $'\n%{http_code}' \
    -X DELETE "$gateway_url/v1/responses/$responses_id" \
    -H "Authorization: Bearer $api_key" \
    -H "X-Request-ID: $responses_delete_request_id")"
assert_equals "200" "${responses_delete_response##*$'\n'}" \
    "OpenAI Responses DELETE subresource status"
responses_delete_body="${responses_delete_response%$'\n'*}"
jq -e --arg response_id "$responses_id" \
    '.id == $response_id and .object == "response.deleted" and .deleted == true' \
    <<<"$responses_delete_body" >/dev/null

responses_malformed_response="$(curl -sS --max-time 30 --write-out $'\n%{http_code}' \
    "$gateway_url/v1/responses?scenario=malformed" \
    -H "Authorization: Bearer $api_key" -H "Content-Type: application/json" \
    -H "X-Request-ID: $responses_malformed_request_id" \
    -H "Idempotency-Key: $responses_malformed_idempotency_key" \
    --data '{"model":"gpt-4o","input":"responses malformed smoke","stream":false}')"
assert_equals "502" "${responses_malformed_response##*$'\n'}" \
    "OpenAI Responses malformed provider status"
jq -e '.error.type == "provider_error"' \
    <<<"${responses_malformed_response%$'\n'*}" >/dev/null
responses_malformed_reconciled() {
    [[ "$(db_query "SELECT count(*) FROM request_leases WHERE request_id = '$responses_malformed_request_id' AND status = 'reconciliation_needed';")" == "1" ]] \
        && [[ "$(db_query "SELECT count(*) FROM usage_events WHERE request_id = '$responses_malformed_request_id';")" == "0" ]] \
        && [[ "$(db_query "SELECT count(*) FROM balance_ledger b JOIN request_leases l USING (lease_token) WHERE l.request_id = '$responses_malformed_request_id' AND b.entry_type = 'usage_debit';")" == "0" ]]
}
wait_for "OpenAI Responses malformed reconciliation hold" 30 responses_malformed_reconciled
echo "PASS: Malformed OpenAI Responses provider payload retained an unknown-charge hold"

responses_stream_response="$(curl -sS --max-time 30 --write-out $'\n%{http_code}' \
    "$gateway_url/v1/responses" \
    -H "Authorization: Bearer $api_key" -H "Content-Type: application/json" \
    -H "X-Request-ID: $responses_stream_request_id" \
    -H "Idempotency-Key: $responses_stream_idempotency_key" \
    --data '{"model":"gpt-4o","input":"responses stream smoke","max_output_tokens":16,"stream":true}')"
assert_equals "200" "${responses_stream_response##*$'\n'}" \
    "OpenAI Responses streaming status"
responses_stream_body="${responses_stream_response%$'\n'*}"
grep -q 'event: response.created' <<<"$responses_stream_body"
grep -q 'event: response.output_text.delta' <<<"$responses_stream_body"
grep -q 'event: response.completed' <<<"$responses_stream_body"
grep -q 'mock response' <<<"$responses_stream_body"

responses_compact_response="$(curl -sS --max-time 30 --write-out $'\n%{http_code}' \
    "$gateway_url/v1/responses/compact" \
    -H "Authorization: Bearer $api_key" -H "Content-Type: application/json" \
    -H "X-Request-ID: $responses_compact_request_id" \
    -H "Idempotency-Key: $responses_compact_idempotency_key" \
    --data '{"model":"gpt-4o","input":[{"role":"user","content":[{"type":"input_text","text":"compact json smoke"}]}],"stream":false}')"
assert_equals "200" "${responses_compact_response##*$'\n'}" \
    "OpenAI Responses compact JSON status"
responses_compact_body="${responses_compact_response%$'\n'*}"
jq -e '.object == "response" and .status == "completed" and .output[0].type == "compaction" and (.output[0].encrypted_content | startswith("mock-compaction:")) and (.usage.total_tokens == (.usage.input_tokens + .usage.output_tokens))' \
    <<<"$responses_compact_body" >/dev/null

responses_compact_stream_response="$(curl -sS --max-time 30 --write-out $'\n%{http_code}' \
    "$gateway_url/v1/responses/compact" \
    -H "Authorization: Bearer $api_key" -H "Content-Type: application/json" \
    -H "X-Request-ID: $responses_compact_stream_request_id" \
    -H "Idempotency-Key: $responses_compact_stream_idempotency_key" \
    --data '{"model":"gpt-4o","input":[{"role":"user","content":[{"type":"input_text","text":"compact stream smoke"}]}],"stream":true}')"
assert_equals "200" "${responses_compact_stream_response##*$'\n'}" \
    "OpenAI Responses compact streaming status"
responses_compact_stream_body="${responses_compact_stream_response%$'\n'*}"
grep -q 'event: response.output_item.done' <<<"$responses_compact_stream_body"
grep -q '"type":"compaction"' <<<"$responses_compact_stream_body"
grep -q 'event: response.completed' <<<"$responses_compact_stream_body"

responses_settled() {
    [[ "$(db_query "
SELECT
  (SELECT count(*) FROM request_leases
   WHERE request_id IN ('$responses_request_id', '$responses_stream_request_id', '$responses_compact_request_id', '$responses_compact_stream_request_id')
     AND status = 'completed' AND final_cost_usd > 0) || '|' ||
  (SELECT count(*) FROM usage_events
   WHERE request_id IN ('$responses_request_id', '$responses_stream_request_id', '$responses_compact_request_id', '$responses_compact_stream_request_id')) || '|' ||
  (SELECT count(*) FROM usage_logs
   WHERE request_id IN ('$responses_request_id', '$responses_stream_request_id', '$responses_compact_request_id', '$responses_compact_stream_request_id')) || '|' ||
  (SELECT count(*) FROM balance_holds h JOIN request_leases l USING (lease_token)
   WHERE l.request_id IN ('$responses_request_id', '$responses_stream_request_id', '$responses_compact_request_id', '$responses_compact_stream_request_id')
     AND h.status = 'committed') || '|' ||
  (SELECT count(*) FROM balance_ledger b JOIN request_leases l USING (lease_token)
   WHERE l.request_id IN ('$responses_request_id', '$responses_stream_request_id', '$responses_compact_request_id', '$responses_compact_stream_request_id')
     AND b.entry_type = 'usage_debit');")" == "4|4|4|4|4" ]]
}
wait_for "OpenAI Responses settlement" 30 responses_settled
echo "PASS: OpenAI Responses JSON/SSE/compact requests settled exactly once"

responses_control_released() {
    [[ "$(db_query "
SELECT
  (SELECT count(*) FROM request_leases
   WHERE request_id IN ('$responses_get_request_id', '$responses_input_items_request_id', '$responses_cancel_request_id', '$responses_delete_request_id') AND status = 'aborted') || '|' ||
  (SELECT count(*) FROM usage_events
   WHERE request_id IN ('$responses_get_request_id', '$responses_input_items_request_id', '$responses_cancel_request_id', '$responses_delete_request_id')) || '|' ||
  (SELECT count(*) FROM usage_logs
   WHERE request_id IN ('$responses_get_request_id', '$responses_input_items_request_id', '$responses_cancel_request_id', '$responses_delete_request_id')) || '|' ||
  (SELECT count(*) FROM balance_holds h JOIN request_leases l USING (lease_token)
   WHERE l.request_id IN ('$responses_get_request_id', '$responses_input_items_request_id', '$responses_cancel_request_id', '$responses_delete_request_id') AND h.status = 'released') || '|' ||
  (SELECT count(*) FROM balance_ledger b JOIN request_leases l USING (lease_token)
   WHERE l.request_id IN ('$responses_get_request_id', '$responses_input_items_request_id', '$responses_cancel_request_id', '$responses_delete_request_id') AND b.entry_type = 'usage_debit');")" == "4|0|0|4|0" ]]
}
wait_for "OpenAI Responses read/input_items/cancel/delete subresource release" 30 responses_control_released
echo "PASS: OpenAI Responses read/input_items/cancel/delete subresources are non-billable and idempotently released"

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

embedding_jina_response="$(curl -fsS "$gateway_url/v1/embeddings" \
    -H "Authorization: Bearer $api_key" -H "Content-Type: application/json" \
    -H "X-Request-ID: $embedding_jina_request_id" -H "Idempotency-Key: $embedding_jina_idempotency_key" \
    --data '{"model":"jina-embeddings-v5-text-small","input":"hello world","dimensions":5,"encoding_format":"float"}')"
jq -e '(.model == "jina-embeddings-v5-text-small") and (.data | length == 1) and (.data[0].embedding | length == 5) and (.usage.prompt_tokens == 3)' \
    <<<"$embedding_jina_response" >/dev/null

embedding_gemini_response="$(curl -fsS "$gateway_url/v1/embeddings" \
    -H "Authorization: Bearer $api_key" -H "Content-Type: application/json" \
    -H "X-Request-ID: $embedding_gemini_request_id" -H "Idempotency-Key: $embedding_gemini_idempotency_key" \
    --data '{"model":"gemini-embedding-001","input":"hello world","dimensions":4,"encoding_format":"base64"}')"
jq -e '(.model == "gemini-embedding-001") and (.data | length == 1) and (.data[0].embedding | type == "string" and length == 24) and (.usage.prompt_tokens == 4)' \
    <<<"$embedding_gemini_response" >/dev/null

embedding_settled() {
    [[ "$(db_query "
SELECT
  (SELECT count(*) FROM request_leases
   WHERE request_id IN ('$embedding_request_id', '$embedding_base64_request_id', '$embedding_jina_request_id', '$embedding_gemini_request_id')
     AND status = 'completed' AND final_cost_usd > 0) || '|' ||
  (SELECT count(*) FROM request_leases WHERE request_id = '$embedding_request_id' AND pricing_version = '$embedding_price_version') || '|' ||
  (SELECT count(*) FROM request_leases WHERE request_id = '$embedding_base64_request_id' AND pricing_version = '$embedding_price_version') || '|' ||
  (SELECT count(*) FROM request_leases WHERE request_id = '$embedding_jina_request_id' AND pricing_version = '$embedding_jina_price_version') || '|' ||
  (SELECT count(*) FROM request_leases WHERE request_id = '$embedding_gemini_request_id' AND pricing_version = '$embedding_gemini_price_version');")" == "4|1|1|1|1" ]]
}
wait_for "embedding settlement" 30 embedding_settled
echo "PASS: Embeddings provider profiles, dimensions, token accounting, and exactly-once settlement"

anthropic_count_response="$(curl -sS --max-time 20 --write-out $'\n%{http_code}' \
    "$gateway_url/v1/messages/count_tokens" \
    -H "Authorization: Bearer $anthropic_api_key" \
    -H "Content-Type: application/json" \
    -H "anthropic-version: 2023-06-01" \
    -H "X-Request-ID: $anthropic_count_request_id" \
    --data '{"model":"claude-3-5-sonnet","messages":[{"role":"user","content":"provider group count"}]}')"
anthropic_count_status="${anthropic_count_response##*$'\n'}"
anthropic_count_response="${anthropic_count_response%$'\n'*}"
assert_equals "200" "$anthropic_count_status" "Anthropic count_tokens response status"
jq -e '.input_tokens > 0' <<<"$anthropic_count_response" >/dev/null

anthropic_response="$(curl -sS --max-time 30 --write-out $'\n%{http_code}' \
    "$gateway_url/v1/messages" \
    -H "Authorization: Bearer $anthropic_api_key" \
    -H "Content-Type: application/json" \
    -H "anthropic-version: 2023-06-01" \
    -H "X-Request-ID: $anthropic_request_id" \
    -H "Idempotency-Key: ${anthropic_request_id}-idem" \
    --data '{"model":"claude-3-5-sonnet","max_tokens":16,"messages":[{"role":"user","content":"provider group anthropic"}],"stream":false}')"
anthropic_status="${anthropic_response##*$'\n'}"
anthropic_response="${anthropic_response%$'\n'*}"
assert_equals "200" "$anthropic_status" "Anthropic JSON provider-group status"
jq -e '.type == "message" and .content[0].text == "mock response" and (.usage.input_tokens > 0) and (.usage.output_tokens > 0)' \
    <<<"$anthropic_response" >/dev/null

anthropic_stream_response="$(curl -sS --max-time 30 --write-out $'\n%{http_code}' \
    "$gateway_url/v1/messages" \
    -H "Authorization: Bearer $anthropic_api_key" \
    -H "Content-Type: application/json" \
    -H "anthropic-version: 2023-06-01" \
    -H "X-Request-ID: $anthropic_stream_request_id" \
    -H "Idempotency-Key: ${anthropic_stream_request_id}-idem" \
    --data '{"model":"claude-3-5-sonnet","max_tokens":16,"messages":[{"role":"user","content":"provider group anthropic stream"}],"stream":true}')"
anthropic_stream_status="${anthropic_stream_response##*$'\n'}"
assert_equals "200" "$anthropic_stream_status" \
    "Anthropic streaming provider-group status"
anthropic_stream_body="${anthropic_stream_response%$'\n'*}"
grep -q 'event: message_start' <<<"$anthropic_stream_body"
grep -q 'event: message_delta' <<<"$anthropic_stream_body"

gemini_models_response="$(curl -sS --max-time 20 --write-out $'\n%{http_code}' \
    "$gateway_url/v1beta/models" \
    -H "Authorization: Bearer $gemini_api_key" \
    -H "X-Request-ID: $gemini_models_request_id")"
gemini_models_status="${gemini_models_response##*$'\n'}"
gemini_models_response="${gemini_models_response%$'\n'*}"
assert_equals "200" "$gemini_models_status" "Gemini models provider-group status"
jq -e '.models[0].name == "models/gemini-2.0-flash" and (.models[0].supportedGenerationMethods | index("generateContent")) != null' \
    <<<"$gemini_models_response" >/dev/null

gemini_response="$(curl -sS --max-time 30 --write-out $'\n%{http_code}' \
    "$gateway_url/v1beta/models/gemini-2.0-flash:generateContent" \
    -H "Authorization: Bearer $gemini_api_key" \
    -H "Content-Type: application/json" \
    -H "X-Request-ID: $gemini_request_id" \
    -H "Idempotency-Key: ${gemini_request_id}-idem" \
    --data '{"contents":[{"role":"user","parts":[{"text":"provider group gemini"}]}]}')"
gemini_status="${gemini_response##*$'\n'}"
gemini_response="${gemini_response%$'\n'*}"
assert_equals "200" "$gemini_status" "Gemini JSON provider-group status"
jq -e '.candidates[0].content.parts[0].text == "mock response" and (.usageMetadata.totalTokenCount > 0)' \
    <<<"$gemini_response" >/dev/null

gemini_stream_response="$(curl -sS --max-time 30 --write-out $'\n%{http_code}' \
    "$gateway_url/v1beta/models/gemini-2.0-flash:streamGenerateContent?alt=sse" \
    -H "Authorization: Bearer $gemini_api_key" \
    -H "Content-Type: application/json" \
    -H "X-Request-ID: $gemini_stream_request_id" \
    -H "Idempotency-Key: ${gemini_stream_request_id}-idem" \
    --data '{"contents":[{"role":"user","parts":[{"text":"provider group gemini stream"}]}]}')"
assert_equals "200" "${gemini_stream_response##*$'\n'}" \
    "Gemini streaming provider-group status"
gemini_stream_body="${gemini_stream_response%$'\n'*}"
grep -q 'data:' <<<"$gemini_stream_body"
grep -q 'mock response' <<<"$gemini_stream_body"

provider_group_settled() {
    [[ "$(db_query "
SELECT
  (SELECT count(*) FROM request_leases
   WHERE request_id IN ('$anthropic_request_id', '$anthropic_stream_request_id', '$gemini_request_id', '$gemini_stream_request_id')
     AND status = 'completed' AND final_cost_usd > 0) || '|' ||
  (SELECT count(*) FROM usage_events
   WHERE request_id IN ('$anthropic_request_id', '$anthropic_stream_request_id', '$gemini_request_id', '$gemini_stream_request_id')) || '|' ||
  (SELECT count(*) FROM usage_logs
   WHERE request_id IN ('$anthropic_request_id', '$anthropic_stream_request_id', '$gemini_request_id', '$gemini_stream_request_id')) || '|' ||
  (SELECT count(*) FROM balance_holds h JOIN request_leases l USING (lease_token)
   WHERE l.request_id IN ('$anthropic_request_id', '$anthropic_stream_request_id', '$gemini_request_id', '$gemini_stream_request_id')
     AND h.status = 'committed') || '|' ||
  (SELECT count(*) FROM balance_ledger b JOIN request_leases l USING (lease_token)
   WHERE l.request_id IN ('$anthropic_request_id', '$anthropic_stream_request_id', '$gemini_request_id', '$gemini_stream_request_id')
     AND b.entry_type = 'usage_debit');")" == "4|4|4|4|4" ]]
}
if ! wait_for "Anthropic/Gemini provider-group settlement" 45 provider_group_settled; then
    echo "Provider-group settlement diagnostics:" >&2
    db_query "
SELECT request_id, model, upstream_model, status, final_cost_usd,
       pricing_version, price_input_per_million, price_output_per_million
FROM request_leases
WHERE request_id IN ('$anthropic_request_id', '$anthropic_stream_request_id',
                     '$gemini_request_id', '$gemini_stream_request_id')
ORDER BY request_id;" >&2 || true
    db_query "
SELECT l.request_id,
       (SELECT count(*) FROM usage_events e WHERE e.request_id = l.request_id) AS usage_events,
       (SELECT count(*) FROM usage_logs u WHERE u.request_id = l.request_id) AS usage_logs,
       (SELECT count(*) FROM balance_holds h WHERE h.lease_token = l.lease_token AND h.status = 'committed') AS committed_holds,
       (SELECT count(*) FROM balance_ledger b WHERE b.lease_token = l.lease_token AND b.entry_type = 'usage_debit') AS usage_debits
FROM request_leases l
WHERE l.request_id IN ('$anthropic_request_id', '$anthropic_stream_request_id',
                       '$gemini_request_id', '$gemini_stream_request_id')
ORDER BY l.request_id;" >&2 || true
    exit 1
fi
echo "PASS: Anthropic and Gemini JSON/SSE provider-group requests settled exactly once"

provider_control_released() {
    [[ "$(db_query "
SELECT
  (SELECT count(*) FROM request_leases
   WHERE request_id IN ('$anthropic_count_request_id', '$gemini_models_request_id')
     AND status = 'aborted') || '|' ||
  (SELECT count(*) FROM balance_holds h JOIN request_leases l USING (lease_token)
   WHERE l.request_id IN ('$anthropic_count_request_id', '$gemini_models_request_id')
     AND h.status = 'released') || '|' ||
  (SELECT count(*) FROM usage_events
   WHERE request_id IN ('$anthropic_count_request_id', '$gemini_models_request_id')) || '|' ||
  (SELECT count(*) FROM usage_logs
   WHERE request_id IN ('$anthropic_count_request_id', '$gemini_models_request_id')) || '|' ||
  (SELECT count(*) FROM balance_ledger b JOIN request_leases l USING (lease_token)
   WHERE l.request_id IN ('$anthropic_count_request_id', '$gemini_models_request_id')
     AND b.entry_type = 'usage_debit');")" == "2|2|0|0|0" ]]
}
wait_for "Anthropic/Gemini control release" 30 provider_control_released
echo "PASS: Anthropic count_tokens and Gemini models released without billing"

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

if (( platform_fault_handled == 0 )) && [[ -n "${PLATFORM_FAULT_HOOK:-}" && \
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

python3 "$stack_dir/realtime_soak.py" "$gateway_url" "$api_key" \
    "$realtime_soak_prefix" "$realtime_soak_count" "$realtime_soak_hold_seconds"
realtime_soak_settled() {
    local summary
    summary="$(db_query "
WITH target_leases AS (
    SELECT lease_token, request_id, status
    FROM request_leases
    WHERE request_id LIKE '$realtime_soak_prefix-%'
)
SELECT
  (SELECT count(*) FROM target_leases) || '|' ||
  (SELECT count(*) FROM target_leases WHERE status = 'completed') || '|' ||
  (SELECT count(*) FROM usage_events WHERE request_id LIKE '$realtime_soak_prefix-%') || '|' ||
  (SELECT count(*) FROM usage_logs WHERE request_id LIKE '$realtime_soak_prefix-%') || '|' ||
  (SELECT count(*) FROM balance_holds h JOIN target_leases l USING (lease_token) WHERE h.status = 'committed') || '|' ||
  (SELECT count(*) FROM balance_ledger b JOIN target_leases l USING (lease_token)
   WHERE b.entry_type = 'usage_debit');")"
    [[ "$summary" == "${realtime_soak_count}|${realtime_soak_count}|${realtime_soak_count}|${realtime_soak_count}|${realtime_soak_count}|${realtime_soak_count}" ]]
}
wait_for "realtime WebSocket soak settlement" 45 realtime_soak_settled
echo "PASS: realtime WebSocket soak settled $realtime_soak_count sessions without duplicate billing"

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
run_chat_stream_fault "disconnect_before_output" "$fault_disconnect_before_output_api_key"
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

media_restart_response="$(curl -fsS "$gateway_url/v1/images/generations/async" \
    -H "Authorization: Bearer $api_key" -H "Content-Type: application/json" \
    -H "Idempotency-Key: $media_restart_idempotency_key" \
    --data '{"model":"mock-image-1","prompt":"media restart recovery smoke","size":"1024x1024"}')"
media_restart_id="$(jq -er '.id' <<<"$media_restart_response")"
media_restart_pending() {
    [[ "$(db_query "SELECT count(*) FROM media_operations WHERE operation_id = '$media_restart_id' AND status IN ('pending', 'running') AND upstream_task_id <> '';")" == "1" ]]
}
wait_for "durable media operation before restart" 15 media_restart_pending
db_query "UPDATE media_operations SET next_poll_at = now() + interval '30 seconds' WHERE operation_id = '$media_restart_id';" >/dev/null
recreate_service platform-silo
wait_for "Platform readiness after media restart" 90 compose exec -T platform-silo \
    curl -fsS http://127.0.0.1:5000/ready >/dev/null
db_query "UPDATE media_operations SET next_poll_at = now() - interval '1 minute' WHERE operation_id = '$media_restart_id' AND status IN ('pending', 'running');" >/dev/null
media_restart_result=""
media_restart_stored() {
    media_restart_result="$(curl -fsS "$gateway_url/v1/images/tasks/$media_restart_id" \
        -H "Authorization: Bearer $api_key")" || return 1
    [[ "$(jq -r '.status' <<<"$media_restart_result")" == "succeeded" ]] \
        && [[ "$(jq -r '.url // empty' <<<"$media_restart_result")" == http://* ]]
}
wait_for "media operation recovery after Platform restart" 45 media_restart_stored
assert_equals "stored" \
    "$(db_query "SELECT object_status FROM media_operations WHERE operation_id = '$media_restart_id';")" \
    "Media restart recovery object status"
echo "PASS: pending media operation resumed and settled after Platform restart"

media_batch_cancel_response="$(curl -fsS "$gateway_url/v1/images/batches" \
    -H "Authorization: Bearer $api_key" -H "Content-Type: application/json" \
    -H "Idempotency-Key: $media_batch_cancel_idempotency_key" \
    --data '{"model":"mock-image-1","items":[{"custom_id":"batch-cancel-1","prompt":"batch cancellation smoke"}]}')"
media_batch_cancel_id="$(jq -er '.id' <<<"$media_batch_cancel_response")"
media_batch_cancel_result="$(curl -fsS -X POST \
    "$gateway_url/v1/images/batches/$media_batch_cancel_id/cancel" \
    -H "Authorization: Bearer $api_key")"
assert_equals "canceled" "$(jq -er '.status' <<<"$media_batch_cancel_result")" \
    "Provider-backed media batch cancellation"
media_batch_cancel_poll="$(curl -fsS "$gateway_url/v1/images/batches/$media_batch_cancel_id" \
    -H "Authorization: Bearer $api_key")"
assert_equals "canceled" "$(jq -er '.status' <<<"$media_batch_cancel_poll")" \
    "Durable canceled media batch state"

media_batch_response="$(curl -fsS "$gateway_url/v1/images/batches" \
    -H "Authorization: Bearer $api_key" -H "Content-Type: application/json" \
    -H "Idempotency-Key: $media_batch_idempotency_key" \
    --data '{"model":"mock-image-1","items":[{"custom_id":"batch-item-1","prompt":"batch object smoke"}]}')"
media_batch_id="$(jq -er '.id' <<<"$media_batch_response")"
media_batch_result=""
media_batch_stored() {
    media_batch_result="$(curl -fsS "$gateway_url/v1/images/batches/$media_batch_id" \
        -H "Authorization: Bearer $api_key")" || return 1
    [[ "$(jq -r '.status' <<<"$media_batch_result")" == "succeeded" ]]
}
wait_for "media batch object persistence" 45 media_batch_stored
media_batch_list="$(curl -fsS "$gateway_url/v1/images/batches" \
    -H "Authorization: Bearer $api_key")"
assert_equals "true" "$(jq -er --arg id "$media_batch_id" \
    '.object == "list" and (.data | any(.id == $id))' <<<"$media_batch_list")" \
    "Durable media batch list"
media_batch_items="$(curl -fsS "$gateway_url/v1/images/batches/$media_batch_id/items" \
    -H "Authorization: Bearer $api_key")"
assert_equals "true" "$(jq -er \
    'type == "array" and length == 1 and .[0].custom_id == "mock-1" and .[0].status == "stored" and (.[0].url | startswith("http://"))' <<<"$media_batch_items")" \
    "Normalized media batch items"
media_batch_item_url="$(jq -er '.[0].url' <<<"$media_batch_items")"
media_batch_item_size="$(curl -fsSL "$media_batch_item_url" | wc -c | tr -d ' ')"
if (( media_batch_item_size <= 0 )); then
    echo "Downloaded media batch item was empty" >&2
    exit 1
fi
echo "PASS: durable per-item media object projection and signed download"

secondary_media_socket="/var/run/scalaapi/dispatch-media.sock"
secondary_platform_container="${project}_platform-media-2"
compose run --detach --no-deps --name "$secondary_platform_container" \
    -e "CapnpRpc__SocketPath=$secondary_media_socket" \
    -e "ASPNETCORE_URLS=http://0.0.0.0:5002" platform-silo >/dev/null
wait_for "secondary media Platform readiness" 120 "$container_cli" exec \
    "$secondary_platform_container" curl -fsS http://127.0.0.1:5002/ready >/dev/null
media_secondary_silos_ready() {
    [[ "$(db_query "SELECT count(*) FROM OrleansMembershipTable WHERE DeploymentId = 'platform' AND Status = 3;")" -ge 2 ]]
}
wait_for "two active media reconciliation silos" 60 media_secondary_silos_ready

media_batch_item_attempts_before_outage="$(db_query "
    SELECT object_reconcile_attempts
    FROM media_operation_items
    WHERE operation_id = '$media_batch_id';")"
media_batch_parent_attempts_before_outage="$(db_query "
    SELECT object_reconcile_attempts
    FROM media_operations
    WHERE operation_id = '$media_batch_id';")"
compose stop object-storage >/dev/null
db_query "
    UPDATE media_operation_items
    SET object_next_check_at = now() - interval '1 second'
    WHERE operation_id = '$media_batch_id';
    UPDATE media_operations
    SET object_next_check_at = now() - interval '1 second'
    WHERE operation_id = '$media_batch_id';" >/dev/null
media_batch_item_outage_recorded() {
    [[ "$(db_query "
        SELECT
          (SELECT object_status || '|' || object_reconcile_attempts || '|' ||
                  COALESCE(error ->> 'type', '')
           FROM media_operation_items WHERE operation_id = '$media_batch_id') || '|' ||
          (SELECT object_status || '|' || object_reconcile_attempts
           FROM media_operations WHERE operation_id = '$media_batch_id');")" == \
       "failed|$((media_batch_item_attempts_before_outage + 1))|item_object_reconcile_error|failed|$((media_batch_parent_attempts_before_outage + 1))" ]]
}
wait_for "media item storage-outage evidence" 75 media_batch_item_outage_recorded

compose start object-storage >/dev/null
wait_for "object storage readiness after outage" 60 \
    curl -fsS "http://127.0.0.1:${OBJECT_STORAGE_PORT}/minio/health/live" >/dev/null
db_query "
    UPDATE media_operation_items
    SET object_next_check_at = now() - interval '1 second'
    WHERE operation_id = '$media_batch_id';
    UPDATE media_operations
    SET object_next_check_at = now() - interval '1 second'
    WHERE operation_id = '$media_batch_id';" >/dev/null
media_batch_item_recovered() {
    [[ "$(db_query "
        SELECT
          (SELECT object_status || '|' || object_reconcile_attempts || '|' ||
                  (object_verified_at IS NOT NULL)::text
           FROM media_operation_items WHERE operation_id = '$media_batch_id') || '|' ||
          (SELECT object_status || '|' || object_reconcile_attempts
           FROM media_operations WHERE operation_id = '$media_batch_id');")" == \
       "stored|$((media_batch_item_attempts_before_outage + 2))|true|stored|$((media_batch_parent_attempts_before_outage + 2))" ]]
}
wait_for "media item verification recovery" 75 media_batch_item_recovered

recreate_service object-storage
wait_for "replacement object storage readiness" 60 \
    curl -fsS "http://127.0.0.1:${OBJECT_STORAGE_PORT}/minio/health/live" >/dev/null
wait_for "Platform readiness after object storage replacement" 90 compose exec -T platform-silo \
    curl -fsS http://127.0.0.1:5000/ready >/dev/null
wait_for "Admin API readiness after object storage replacement" 90 compose exec -T admin-api \
    curl -fsS http://127.0.0.1:5001/ready >/dev/null
wait_for "Gateway readiness after object storage replacement" 90 \
    curl -fsS "$gateway_url/ready" >/dev/null
media_batch_item_size_after_replacement="$(curl -fsSL "$media_batch_item_url" | wc -c | tr -d ' ')"
assert_equals "$media_batch_item_size" "$media_batch_item_size_after_replacement" \
    "Signed item download after object storage replacement"

"$container_cli" rm -f "$secondary_platform_container" >/dev/null
secondary_platform_container=""
echo "PASS: two Silo item claim, storage outage recovery, and volume-preserving replacement"
media_batch_archive="${TMPDIR:-/tmp}/scalaapi-${project}-batch.zip"
python3 - "$gateway_url/v1/images/batches/$media_batch_id/download" \
    "$api_key" "$media_batch_archive" <<'PY'
import sys
import urllib.request
import zipfile
from urllib.error import HTTPError

url, api_key, output_path = sys.argv[1:4]
request = urllib.request.Request(url, headers={"Authorization": f"Bearer {api_key}"})
class NoRedirect(urllib.request.HTTPRedirectHandler):
    def redirect_request(self, req, fp, code, msg, headers, newurl):
        return None

opener = urllib.request.build_opener(NoRedirect)
try:
    response = opener.open(request, timeout=30)
except HTTPError as error:
    if error.code not in (301, 302, 303, 307, 308):
        raise
    signed_url = error.headers.get("Location")
    if not signed_url:
        raise RuntimeError("media download redirect omitted Location") from error
    response = urllib.request.urlopen(signed_url, timeout=30)
with response:
    with open(output_path, "wb") as output:
        output.write(response.read())
with zipfile.ZipFile(output_path) as archive:
    names = set(archive.namelist())
    if not {"mock-1.png", "manifest.json", "errors.json"} <= names:
        raise SystemExit(f"batch archive missing expected entries: {sorted(names)}")
PY
unlink "$media_batch_archive"
echo "PASS: durable batch download archive with manifest"
assert_equals "stored" \
    "$(db_query "SELECT object_status FROM media_operations WHERE operation_id = '$media_batch_id';")" \
    "Media batch object status"
compose stop object-storage >/dev/null
db_query "UPDATE media_operations
SET retention_until = now() - interval '1 minute',
    object_next_check_at = now() - interval '1 minute'
WHERE operation_id = '$media_batch_id';" >/dev/null
media_batch_retention_failure_recorded() {
    [[ "$(db_query "
        SELECT operation.status || '|' || operation.object_status || '|' ||
               COALESCE(operation.object_error ->> 'type', '') || '|' ||
               item.object_status || '|' || lease.status || '|' || hold.status
        FROM media_operations AS operation
        JOIN media_operation_items AS item
          ON item.operation_id = operation.operation_id
        JOIN request_leases AS lease ON lease.lease_token = operation.lease_token
        JOIN balance_holds AS hold ON hold.hold_id = lease.hold_handle
        WHERE operation.operation_id = '$media_batch_id';")" == \
       "succeeded|failed|media_retention_delete_failed|stored|completed|committed" ]]
}
wait_for "media retention delete-outage evidence" 75 media_batch_retention_failure_recorded

compose start object-storage >/dev/null
wait_for "object storage readiness after retention failure" 60 \
    curl -fsS "http://127.0.0.1:${OBJECT_STORAGE_PORT}/minio/health/live" >/dev/null
db_query "UPDATE media_operations
SET object_next_check_at = now() - interval '1 minute'
WHERE operation_id = '$media_batch_id';" >/dev/null
media_batch_retention_complete() {
    [[ "$(db_query "SELECT object_status FROM media_operations WHERE operation_id = '$media_batch_id';")" == "deleted" ]]
}
wait_for "media batch retention cleanup" 60 media_batch_retention_complete
assert_equals "|deleted|" \
    "$(db_query "SELECT object_key || '|' || object_status || '|' || output_url FROM media_operations WHERE operation_id = '$media_batch_id';")" \
    "Media batch retention clears object metadata"
assert_equals "1" \
    "$(db_query "SELECT count(*) FROM media_operation_items WHERE operation_id = '$media_batch_id' AND object_key = '' AND object_status = 'deleted' AND output_url = '';")" \
    "Media batch retention clears per-item object metadata"
echo "PASS: failed media retention delete retries and clears parent/item metadata"

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
# The streaming response-policy case intentionally retains one additional
# unknown-charge lease so the first SSE event can be withheld before blocking.
expected_unknown_incidents=$((12 + gateway_hook_unknown_incidents + 1))
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

if [[ "$GARNET_TLS" == "true" || "$GARNET_TLS" == "1" ]] &&
   [[ "$garnet_tls_rotation_enabled" == "true" || "$garnet_tls_rotation_enabled" == "1" ]]; then
    tls_platform_ready() {
        compose exec -T platform-silo curl -fsS http://127.0.0.1:5000/ready >/dev/null
    }

    tls_platform_not_ready() {
        local status
        status="$(compose exec -T platform-silo \
            curl -sS -o /dev/null -w '%{http_code}' http://127.0.0.1:5000/ready \
            2>/dev/null | tr -d '\r' | tail -n 1 || true)"
        [[ "$status" == "503" ]]
    }

    tls_refresh_certificate() {
        local replacement=$1
        cp -- "$replacement" "$GARNET_SERVER_CERT_FILE"
        sleep "$((GARNET_CERT_REFRESH_SECONDS + 2))"
    }

    tls_restart_clients() {
        recreate_service platform-silo
        recreate_service gateway
    }

    echo "Rotating Garnet TLS server certificate and reconnecting clients"
    tls_refresh_certificate "$GARNET_SERVER_CERT_ROTATED_FILE"
    tls_restart_clients
    wait_for "Platform readiness after Garnet certificate rotation" 90 tls_platform_ready
    wait_for "Gateway readiness after Garnet certificate rotation" 90 \
        curl -fsS "$gateway_url/ready" >/dev/null

    tls_wrong_name_request_id="smoke-garnet-tls-rotation-${suffix}"
    tls_wrong_name_idempotency_key="${tls_wrong_name_request_id}-idem"
    tls_refresh_certificate "$GARNET_SERVER_CERT_WRONG_NAME_FILE"
    recreate_service platform-silo
    wait_for "Garnet wrong-name certificate rejection" 45 tls_platform_not_ready

    tls_refresh_certificate "$GARNET_SERVER_CERT_EXPIRED_FILE"
    recreate_service platform-silo
    wait_for "Garnet expired certificate rejection" 45 tls_platform_not_ready

    tls_refresh_certificate "$GARNET_SERVER_CERT_ROTATED_FILE"
    tls_restart_clients
    wait_for "Platform readiness after Garnet certificate recovery" 90 tls_platform_ready
    wait_for "Gateway readiness after Garnet certificate recovery" 90 \
        curl -fsS "$gateway_url/ready" >/dev/null

    tls_rotation_response="$(curl -fsS "$gateway_url/v1/chat/completions" \
        -H "Authorization: Bearer $api_key" -H "Content-Type: application/json" \
        -H "X-Request-ID: $tls_wrong_name_request_id" \
        -H "Idempotency-Key: $tls_wrong_name_idempotency_key" \
        --data "$chat_body")"
    jq -e '(.choices | length > 0) and (.usage.total_tokens > 0)' \
        <<<"$tls_rotation_response" >/dev/null
    tls_rotation_settled() {
        [[ "$(db_query "SELECT count(*) FROM request_leases WHERE request_id = '$tls_wrong_name_request_id' AND status = 'completed' AND final_cost_usd > 0;")" == "1" ]]
    }
    wait_for "post-Garnet-TLS-recovery settlement" 30 tls_rotation_settled
    garnet_tls_rotation_passed=1
    echo "PASS: Garnet TLS certificate rotation, wrong-name/expiry rejection, recovery, and billing"
fi

if [[ "$GARNET_TLS" == "true" || "$GARNET_TLS" == "1" ]]; then
    # The Platform readiness endpoint performs an authenticated TLS RESP PING
    # through the production RemoteGarnetService. The busybox helper has no TLS
    # client, so do not send a plaintext probe to a TLS-only Garnet listener.
    garnet_probe="$(compose exec -T platform-silo \
        curl -fsS http://127.0.0.1:5000/ready)"
else
    garnet_probe="$(compose exec -T garnet-health sh -c '
    pass="$GARNET_PASSWORD"; len=$(printf %s "$pass" | wc -c)
    { printf "*2\r\n\$4\r\nAUTH\r\n\$%s\r\n%s\r\n" "$len" "$pass"; printf "*1\r\n\$4\r\nPING\r\n"; } | nc -w 2 garnet 6379
    ' | tr -d '\r')"
fi
if [[ "$GARNET_TLS" == "true" || "$GARNET_TLS" == "1" ]]; then
    [[ "$garnet_probe" == *ready* ]] && garnet_probe_ok=true || garnet_probe_ok=false
else
    [[ "$garnet_probe" == *PONG* ]] && garnet_probe_ok=true || garnet_probe_ok=false
fi
if [[ "$garnet_probe_ok" != "true" ]]; then
    [[ "$GARNET_TLS" == "true" || "$GARNET_TLS" == "1" ]] \
        && echo "Authenticated Garnet TLS readiness did not return ready" >&2 \
        || echo "Authenticated Garnet PING did not return PONG" >&2
    exit 1
fi

echo "PASS: $expected_migrations empty-volume migrations and second-run idempotency"
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
if (( garnet_tls_rotation_passed > 0 )); then
    echo "PASS: Garnet TLS certificate rotation and expiry/failure recovery"
fi
echo "PASS: S3-compatible bucket bootstrap, object persistence, and signed download ($media_size bytes)"
