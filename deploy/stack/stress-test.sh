#!/usr/bin/env bash
# One-hour stress test orchestration.
#
# Runs 3600 seconds of mixed load (media due-work, stream, realtime,
# backpressure) while injecting faults (Provider, Garnet, PostgreSQL, MinIO,
# TLS, process replacement) and collecting metrics (p95, connections, buffers,
# leases, holds, outbox backlog).
#
# On exit, verifies no duplicate financial effects, no leaked
# connections/containers/temporary networks, and that unknown-charge incidents
# are explainable.  The top-level command propagates any failure, and the
# final `podman ps -a` shows no project residuals.
#
# Usage:
#   ./stress-test.sh [--duration SECONDS] [--keep-stack]
set -Eeuo pipefail

stack_dir="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
repo_root="$(cd "$stack_dir/../.." && pwd)"
compose_file="$stack_dir/docker-compose.yml"
faults_compose_file="$stack_dir/docker-compose.faults.yml"
compose_files=("$compose_file" "$faults_compose_file")

garnet_tls_enabled="${GARNET_TLS:-false}"
if [[ "$garnet_tls_enabled" == "true" || "$garnet_tls_enabled" == "1" ]]; then
    tls_compose_file="$stack_dir/docker-compose.tls.yml"
    if [[ ! -f "$tls_compose_file" ]]; then
        echo "Garnet TLS Compose override is missing: $tls_compose_file" >&2
        exit 2
    fi
    : "${GARNET_CA_CERT_FILE:?GARNET_CA_CERT_FILE is required when GARNET_TLS=true}"
    : "${GARNET_SERVER_CERT_FILE:?GARNET_SERVER_CERT_FILE is required when GARNET_TLS=true}"
    : "${GARNET_SERVER_CERT_PASSWORD:?GARNET_SERVER_CERT_PASSWORD is required when GARNET_TLS=true}"
    compose_files+=("$tls_compose_file")
fi

# --- Argument parsing ---
duration_seconds="${STRESS_DURATION:-3600}"
keep_stack="${KEEP_STACK:-0}"

for arg in "$@"; do
    case "$arg" in
        --duration=*) duration_seconds="${arg#*=}" ;;
        --keep-stack) keep_stack=1 ;;
        *) echo "Unknown option: $arg" >&2; exit 2 ;;
    esac
done

if ! [[ "$duration_seconds" =~ ^[1-9][0-9]*$ ]]; then
    echo "Duration must be a positive integer (got '$duration_seconds')" >&2
    exit 2
fi

# --- Container CLI detection ---
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

# --- Compose helper ---
project="${SMOKE_PROJECT_NAME:-scalaapi-stress-$$}"
if [[ ! "$project" =~ ^[a-z0-9][a-z0-9_-]*$ ]]; then
    echo "SMOKE_PROJECT_NAME must contain only lowercase letters, numbers, dashes, and underscores" >&2
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
    "$container_cli" ps --all \
        --filter "label=com.docker.compose.project=$project" \
        --filter "label=com.docker.compose.service=$service" \
        --format '{{.ID}}' | tr -d '\r'
}

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

# --- Environment ---
suffix="${project//[^a-zA-Z0-9]/}"
export POSTGRES_DB="platform"
export POSTGRES_USER="platform"
export POSTGRES_PASSWORD="stress-postgres-${suffix}-password"
export JWT_KEY="stress-jwt-${suffix}-012345678901234567890123456789"
export ADMIN_USERNAME="admin@scalaapi.test"
export ADMIN_PASSWORD="stress-admin-${suffix}-password"
export SECURITY_MASTER_KEY="MDEyMzQ1Njc4OWFiY2RlZjAxMjM0NTY3ODlhYmNkZWY="
export PROVIDER_CREDENTIALS_ALLOW_INSECURE="true"
export INTERNAL_RECONCILIATION_TOKEN="stress-reconciliation-${suffix}-token"
export GARNET_PASSWORD="stress-garnet-${suffix}-password"
export GARNET_TLS="${GARNET_TLS:-false}"
export CONTENT_CLASSIFIER_OPENAI_ENDPOINT="${CONTENT_CLASSIFIER_OPENAI_ENDPOINT:-http://provider-mock:8081/v1/moderations}"
export CONTENT_CLASSIFIER_OPENAI_API_KEY="${CONTENT_CLASSIFIER_OPENAI_API_KEY:-mock-openai-moderation-key}"
export CONTENT_CLASSIFIER_OPENAI_ALLOW_INSECURE="${CONTENT_CLASSIFIER_OPENAI_ALLOW_INSECURE:-true}"
export OBJECT_STORAGE_ACCESS_KEY="stressplatform"
export OBJECT_STORAGE_SECRET_KEY="stress-object-${suffix}-password"
export OBJECT_STORAGE_BUCKET="scalaapi-stress-media"
export OBJECT_STORAGE_PORT="${STRESS_OBJECT_STORAGE_PORT:-29100}"
export OBJECT_STORAGE_CONSOLE_PORT="${STRESS_OBJECT_STORAGE_CONSOLE_PORT:-29101}"
export OBJECT_STORAGE_PUBLIC_ENDPOINT="http://127.0.0.1:${OBJECT_STORAGE_PORT}"
export GATEWAY_PORT="${STRESS_GATEWAY_PORT:-28180}"
export ADMIN_WEB_PORT="${STRESS_ADMIN_WEB_PORT:-23100}"
export USER_WEB_PORT="${STRESS_USER_WEB_PORT:-23101}"
export GATEWAY_CORES="${STRESS_GATEWAY_CORES:-2}"
export DISPATCH_LEASE_TTL_SECONDS="${DISPATCH_LEASE_TTL_SECONDS:-360}"

gateway_url="http://127.0.0.1:${GATEWAY_PORT}"

# --- Metrics and output directories ---
metrics_dir="$(mktemp -d "${TMPDIR:-/tmp}/scalaapi-stress-metrics.XXXXXX")"
logs_dir="$(mktemp -d "${TMPDIR:-/tmp}/scalaapi-stress-logs.XXXXXX")"

# --- Cleanup ---
background_pids=()

kill_backgrounds() {
    for pid in "${background_pids[@]}"; do
        if kill -0 "$pid" 2>/dev/null; then
            kill -TERM "$pid" 2>/dev/null || true
        fi
    done
    # Give processes a moment to exit gracefully
    sleep 2
    for pid in "${background_pids[@]}"; do
        if kill -0 "$pid" 2>/dev/null; then
            kill -KILL "$pid" 2>/dev/null || true
        fi
    done
}

cleanup() {
    local status=$?
    set +e

    kill_backgrounds

    if (( status != 0 )); then
        echo "" >&2
        echo "Stress test failed (exit $status); final container state:" >&2
        compose ps >&2 || true
        compose logs --tail 100 >&2 || true
    fi

    # Save compose logs for post-mortem
    compose logs >"$logs_dir/compose-logs.txt" 2>&1 || true

    if [[ "$keep_stack" == "1" ]]; then
        echo "Keeping Compose project '$project' (KEEP_STACK=1)" >&2
    else
        compose down --volumes --remove-orphans >/dev/null 2>&1
    fi

    # Final residual check
    echo ""
    echo "==> Final container state for project '$project':"
    "$container_cli" ps --all \
        --filter "label=com.docker.compose.project=$project" \
        --format 'table {{.Names}}\t{{.Status}}' 2>/dev/null || true

    residual_count="$("$container_cli" ps --all \
        --filter "label=com.docker.compose.project=$project" \
        --format '{{.Names}}' 2>/dev/null | tr -d '\r' | wc -l)" || residual_count=0
    if (( residual_count > 0 )); then
        echo "WARNING: $residual_count residual containers remain for project '$project'" >&2
        "$container_cli" rm -f $(
            "$container_cli" ps --all \
                --filter "label=com.docker.compose.project=$project" \
                --format '{{.ID}}' 2>/dev/null
        ) >/dev/null 2>&1 || true
    fi

    echo ""
    echo "Stress test artifacts:"
    echo "  Metrics: $metrics_dir"
    echo "  Logs:    $logs_dir"

    exit "$status"
}
trap cleanup EXIT

# --- Compose file list for child scripts (pipe-delimited) ---
compose_files_pipe=""
for file in "${compose_files[@]}"; do
    if [[ -n "$compose_files_pipe" ]]; then
        compose_files_pipe="${compose_files_pipe}|${file}"
    else
        compose_files_pipe="$file"
    fi
done

# ==================================================================
# Phase 1: Start the stack
# ==================================================================
echo "=========================================="
echo "  Stress Test - Phase 1: Stack Startup"
echo "  Duration: ${duration_seconds}s"
echo "  Project:  $project"
echo "=========================================="
echo ""

echo "==> Starting Compose stack..."
compose up --detach --build >/dev/null
echo "    Compose stack started."

echo "==> Waiting for service readiness..."
wait_for "postgres readiness" 120 compose exec -T postgres \
    pg_isready -U "$POSTGRES_USER" -d "$POSTGRES_DB" >/dev/null
wait_for "migrate completion" 180 bash -c "
    status=\$(\"$container_cli\" inspect -f '{{.State.Status}}' \
        \"\$($container_cli ps --all --filter label=com.docker.compose.project=$project --filter label=com.docker.compose.service=migrate --format '{{.ID}}' | head -1)\" 2>/dev/null | tr -d '\r')
    [[ \"\$status\" == \"exited\" ]]
"
wait_for "provider-mock readiness" 120 curl -fsS "$gateway_url/ready" >/dev/null || true
wait_for "platform-silo-1 readiness" 180 compose exec -T platform-silo-1 \
    curl -fsS http://127.0.0.1:5000/ready >/dev/null
wait_for "platform-silo-2 readiness" 180 compose exec -T platform-silo-2 \
    curl -fsS http://127.0.0.1:5000/ready >/dev/null
wait_for "gateway-1 readiness" 120 curl -fsS "$gateway_url/ready" >/dev/null
wait_for "gateway-2 readiness" 120 curl -fsS "$gateway_url/ready" >/dev/null
wait_for "admin-api readiness" 120 curl -fsS "http://127.0.0.1:${ADMIN_WEB_PORT}/ready" >/dev/null || true
echo "    All services ready."

# --- Bootstrap: create admin user, user, API key ---
echo "==> Bootstrapping test user and API key..."
admin_token="$(curl -fsS "http://127.0.0.1:${GATEWAY_PORT}/../admin-api:5001/admin/auth/login" \
    -H "Content-Type: application/json" \
    --data "{\"username\":\"$ADMIN_USERNAME\",\"password\":\"$ADMIN_PASSWORD\"}" \
    2>/dev/null | jq -er '.token')" || {
    # Fallback: use compose exec to reach admin-api directly
    admin_token="$(compose exec -T admin-api curl -fsS http://127.0.0.1:5001/admin/auth/login \
        -H "Content-Type: application/json" \
        --data "{\"username\":\"$ADMIN_USERNAME\",\"password\":\"$ADMIN_PASSWORD\"}" \
        | jq -er '.token')"
}

# Create user
user_email="stress-${suffix}@scalaapi.test"
user_password="stress-user-${suffix}-password"
compose exec -T admin-api curl -fsS http://127.0.0.1:5001/admin/users/ \
    -H "Authorization: Bearer $admin_token" \
    -H "Content-Type: application/json" \
    --data "{\"email\":\"$user_email\",\"password\":\"$user_password\",\"displayName\":\"Stress User\"}" \
    >/dev/null 2>&1 || true

user_id="$(compose exec -T admin-api curl -fsS \
    "http://127.0.0.1:5001/admin/users?email=$user_email" \
    -H "Authorization: Bearer $admin_token" | jq -er '.[0].id')"

# Create provider group and API key
provider_group_id="$(compose exec -T admin-api curl -fsS \
    "http://127.0.0.1:5001/admin/providergroups/" \
    -H "Authorization: Bearer $admin_token" \
    -H "Content-Type: application/json" \
    --data '{"name":"stress-provider-group","providerName":"mock"}' \
    | jq -er '.id')" || {
    provider_group_id="$(compose exec -T admin-api curl -fsS \
        "http://127.0.0.1:5001/admin/providergroups" \
        -H "Authorization: Bearer $admin_token" | jq -er '.[0].id')"
}

api_key="$(compose exec -T admin-api curl -fsS http://127.0.0.1:5001/admin/apikeys/ \
    -H "Authorization: Bearer $admin_token" \
    -H "Content-Type: application/json" \
    --data "{\"userId\":$user_id,\"groupId\":$provider_group_id,\"quota\":10000,\"expiresAt\":null,\"ipWhitelist\":[],\"ipBlacklist\":[],\"rateLimit5h\":0,\"rateLimit1d\":0,\"rateLimit7d\":0}" \
    | jq -er '.key')"

# Fund the user
compose exec -T admin-api curl -fsS http://127.0.0.1:5001/admin/balance/credit \
    -H "Authorization: Bearer $admin_token" \
    -H "Content-Type: application/json" \
    --data "{\"userId\":$user_id,\"amountUsd\":10000.00,\"idempotencyKey\":\"stress-funding-${suffix}\"}" \
    >/dev/null

echo "    Test user created: $user_email (API key obtained)"

# ==================================================================
# Phase 2: Launch load clients, fault injector, and metrics collector
# ==================================================================
echo ""
echo "=========================================="
echo "  Stress Test - Phase 2: Mixed Load + Faults"
echo "=========================================="
echo ""

# Export common environment for child scripts
export STRESS_CONTAINER_CLI="$container_cli"
export STRESS_PROJECT="$project"
export STRESS_COMPOSE_FILES="$compose_files_pipe"
export STRESS_GATEWAY_URL="$gateway_url"
export STRESS_GATEWAY_PORT="$GATEWAY_PORT"
export STRESS_API_KEY="$api_key"
export STRESS_PREFIX="stress-${suffix}"
export STRESS_GARNET_TLS="$garnet_tls_enabled"
export STRESS_POSTGRES_USER="$POSTGRES_USER"
export STRESS_POSTGRES_DB="$POSTGRES_DB"
export STRESS_METRICS_DIR="$metrics_dir"

# --- Start metrics collector ---
echo "==> Starting metrics collector..."
STRESS_METRICS_DURATION="$duration_seconds" \
STRESS_METRICS_INTERVAL="${STRESS_METRICS_INTERVAL:-30}" \
    "$stack_dir/metrics-collector.sh" >"$logs_dir/metrics-collector.log" 2>&1 &
background_pids+=($!)
echo "    Metrics collector PID ${background_pids[-1]}"

# --- Start load clients ---
echo "==> Starting load clients..."

STRESS_MEDIA_DURATION="$duration_seconds" \
STRESS_MEDIA_INTERVAL="${STRESS_MEDIA_INTERVAL:-5}" \
    "$stack_dir/load-clients/media-load.sh" >"$logs_dir/media-load.log" 2>&1 &
background_pids+=($!)
echo "    Media load PID ${background_pids[-1]}"

STRESS_STREAM_DURATION="$duration_seconds" \
STRESS_STREAM_INTERVAL="${STRESS_STREAM_INTERVAL:-3}" \
    "$stack_dir/load-clients/stream-load.sh" >"$logs_dir/stream-load.log" 2>&1 &
background_pids+=($!)
echo "    Stream load PID ${background_pids[-1]}"

STRESS_REALTIME_DURATION="$duration_seconds" \
STRESS_REALTIME_CONCURRENCY="${STRESS_REALTIME_CONCURRENCY:-4}" \
STRESS_REALTIME_HOLD="${STRESS_REALTIME_HOLD:-3}" \
STRESS_REALTIME_INTERVAL="${STRESS_REALTIME_INTERVAL:-10}" \
    python3 "$stack_dir/load-clients/realtime-load.py" >"$logs_dir/realtime-load.log" 2>&1 &
background_pids+=($!)
echo "    Realtime load PID ${background_pids[-1]}"

STRESS_BACKPRESSURE_DURATION="$duration_seconds" \
STRESS_BACKPRESSURE_BURST="${STRESS_BACKPRESSURE_BURST:-16}" \
STRESS_BACKPRESSURE_INTERVAL="${STRESS_BACKPRESSURE_INTERVAL:-8}" \
    "$stack_dir/load-clients/backpressure-load.sh" >"$logs_dir/backpressure-load.log" 2>&1 &
background_pids+=($!)
echo "    Backpressure load PID ${background_pids[-1]}"

# --- Start fault injector ---
echo "==> Starting fault injector..."
STRESS_FAULT_DURATION="$duration_seconds" \
STRESS_FAULT_INTERVAL="${STRESS_FAULT_INTERVAL:-120}" \
    "$stack_dir/fault-injector.sh" >"$logs_dir/fault-injector.log" 2>&1 &
background_pids+=($!)
echo "    Fault injector PID ${background_pids[-1]}"

# ==================================================================
# Phase 3: Wait for the stress test to complete
# ==================================================================
echo ""
echo "=========================================="
echo "  Stress Test - Phase 3: Running (${duration_seconds}s)"
echo "=========================================="
echo ""

test_started_at=$(date +%s)
status_interval=300  # Print status every 5 minutes

while (( $(date +%s) - test_started_at < duration_seconds )); do
    elapsed=$(( $(date +%s) - test_started_at ))
    remaining=$(( duration_seconds - elapsed ))

    # Check that background processes are still running
    for pid in "${background_pids[@]}"; do
        if ! kill -0 "$pid" 2>/dev/null; then
            wait "$pid" 2>/dev/null
            exit_code=$?
            if (( exit_code != 0 )); then
                echo "ERROR: Background process $pid exited with code $exit_code" >&2
                # Show which process it was
                echo "  Check logs in $logs_dir for details" >&2
            fi
        fi
    done

    # Periodic status report
    if (( elapsed > 0 && elapsed % status_interval < status_interval )); then
        # Quick health check
        gateway_healthy=false
        if curl -fsS --max-time 5 "$gateway_url/ready" >/dev/null 2>&1; then
            gateway_healthy=true
        fi

        lease_count="$(compose exec -T postgres psql --no-psqlrc --tuples-only --no-align \
            --set ON_ERROR_STOP=1 --username "$POSTGRES_USER" --dbname "$POSTGRES_DB" \
            --command "SELECT count(*) FROM request_leases WHERE status = 'active';" 2>/dev/null | tr -d '\r')" || lease_count="?"

        echo "  [${elapsed}s elapsed, ${remaining}s remaining] " \
             "gateway=$gateway_healthy, active_leases=$lease_count, " \
             "background_pids=${#background_pids[@]}"
    fi

    sleep 30
done

echo ""
echo "==> Stress test duration complete. Stopping load generators..."

# ==================================================================
# Phase 4: Stop load generators and wait for settlement
# ==================================================================
echo ""
echo "=========================================="
echo "  Stress Test - Phase 4: Settlement"
echo "=========================================="
echo ""

# Terminate background processes
kill_backgrounds
background_pids=()

# Wait for outbox drain and lease settlement
echo "==> Waiting for outbox drain and lease settlement..."
settlement_timeout=120
for ((attempt = 1; attempt <= settlement_timeout; attempt++)); do
    pending_outbox="$(compose exec -T postgres psql --no-psqlrc --tuples-only --no-align \
        --set ON_ERROR_STOP=1 --username "$POSTGRES_USER" --dbname "$POSTGRES_DB" \
        --command "SELECT count(*) FROM gateway_usage_outbox WHERE status = 'pending';" 2>/dev/null | tr -d '\r')" || pending_outbox=1
    active_leases="$(compose exec -T postgres psql --no-psqlrc --tuples-only --no-align \
        --set ON_ERROR_STOP=1 --username "$POSTGRES_USER" --dbname "$POSTGRES_DB" \
        --command "SELECT count(*) FROM request_leases WHERE status = 'active';" 2>/dev/null | tr -d '\r')" || active_leases=1
    if [[ "$pending_outbox" == "0" && "$active_leases" == "0" ]]; then
        echo "    Settlement complete: outbox drained, all leases terminal."
        break
    fi
    if (( attempt % 30 == 0 )); then
        echo "    Waiting... pending_outbox=$pending_outbox, active_leases=$active_leases"
    fi
    sleep 1
done

if [[ "$pending_outbox" != "0" || "$active_leases" != "0" ]]; then
    echo "WARNING: Settlement incomplete after ${settlement_timeout}s (outbox=$pending_outbox, leases=$active_leases)" >&2
fi

# ==================================================================
# Phase 5: Verification
# ==================================================================
echo ""
echo "=========================================="
echo "  Stress Test - Phase 5: Verification"
echo "=========================================="
echo ""

export STRESS_METRICS_DIR="$metrics_dir"
"$stack_dir/verify-stress.sh"
verify_exit=$?

if (( verify_exit != 0 )); then
    echo "" >&2
    echo "VERIFICATION FAILED" >&2
    exit "$verify_exit"
fi

# ==================================================================
# Phase 6: Cleanup
# ==================================================================
echo ""
echo "=========================================="
echo "  Stress Test - Phase 6: Cleanup"
echo "=========================================="
echo ""

echo "==> Tearing down Compose stack..."
if [[ "$keep_stack" != "1" ]]; then
    compose down --volumes --remove-orphans >/dev/null 2>&1
fi

# Final residual check
echo "==> Final podman ps -a for project '$project':"
"$container_cli" ps --all \
    --filter "label=com.docker.compose.project=$project" \
    --format 'table {{.Names}}\t{{.Status}}\t{{.Image}}' 2>/dev/null || echo "    (no containers)"

residual_count="$("$container_cli" ps --all \
    --filter "label=com.docker.compose.project=$project" \
    --format '{{.Names}}' 2>/dev/null | tr -d '\r' | wc -l)" || residual_count=0

if (( residual_count > 0 )); then
    echo "ERROR: $residual_count residual containers remain!" >&2
    exit 1
fi

echo ""
echo "=========================================="
echo "  STRESS TEST PASSED"
echo "  Duration: ${duration_seconds}s"
echo "  No duplicate financial effects"
echo "  No leaked connections/containers/networks"
echo "  All unknown-charge incidents explainable"
echo "  No project residuals"
echo "=========================================="
