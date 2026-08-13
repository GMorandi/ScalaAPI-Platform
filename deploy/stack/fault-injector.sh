#!/usr/bin/env bash
# Fault injector for the one-hour stress test.
# Periodically injects Provider, Garnet, PostgreSQL, MinIO, TLS, and process
# replacement faults while load clients are running.
set -Eeuo pipefail

stack_dir="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"

# --- Configuration from environment ---
container_cli="${STRESS_CONTAINER_CLI:?STRESS_CONTAINER_CLI is required}"
project="${STRESS_PROJECT:?STRESS_PROJECT is required}"
duration_seconds="${STRESS_FAULT_DURATION:-3600}"
interval_seconds="${STRESS_FAULT_INTERVAL:-120}"
compose_files_arg="${STRESS_COMPOSE_FILES:?STRESS_COMPOSE_FILES is required}"
gateway_port="${STRESS_GATEWAY_PORT:-28080}"
gateway_url="${STRESS_GATEWAY_URL:?STRESS_GATEWAY_URL is required}"
garnet_tls="${STRESS_GARNET_TLS:-false}"

compose() {
    local compose_arguments=(--project-name "$project")
    IFS='|' read -ra files <<<"$compose_files_arg"
    for file in "${files[@]}"; do
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

wait_for_ready() {
    local description=$1
    local service=$2
    local port=$3
    local timeout=${4:-90}
    local container_id
    container_id="$(service_container_id "$service")"
    if [[ -z "$container_id" ]]; then
        echo "fault-injector: no container for $service" >&2
        return 1
    fi
    for ((attempt = 1; attempt <= timeout; attempt++)); do
        if "$container_cli" exec "$container_id" \
            curl -fsS "http://127.0.0.1:${port}/ready" >/dev/null 2>&1; then
            return 0
        fi
        sleep 1
    done
    echo "fault-injector: timed out waiting for $description" >&2
    return 1
}

wait_for_silo_cluster() {
    local expected=$1
    local timeout=${2:-90}
    local pg_container
    pg_container="$(service_container_id postgres)"
    for ((attempt = 1; attempt <= timeout; attempt++)); do
        local count
        count="$("$container_cli" exec "$pg_container" \
            psql --no-psqlrc --tuples-only --no-align \
            --set ON_ERROR_STOP=1 \
            --username "${STRESS_POSTGRES_USER:-platform}" \
            --dbname "${STRESS_POSTGRES_DB:-platform}" \
            --command "SELECT count(*) FROM OrleansMembershipTable WHERE DeploymentId = 'platform' AND Status = 3;" 2>/dev/null | tr -d '\r')" || count=0
        if [[ "$count" == "$expected" ]]; then
            return 0
        fi
        sleep 1
    done
    echo "fault-injector: timed out waiting for $expected-silo cluster" >&2
    return 1
}

# --- Fault types ---

inject_provider_fault() {
    echo "fault-injector: [$(date +%H:%M:%S)] injecting Provider fault (restart provider-mock)"
    compose restart provider-mock >/dev/null 2>&1 || true
    wait_for_ready "provider-mock" "provider-mock" 8081 60 || true
    echo "fault-injector: [$(date +%H:%M:%S)] Provider fault injected and recovered"
}

inject_garnet_fault() {
    echo "fault-injector: [$(date +%H:%M:%S)] injecting Garnet fault (stop/restart garnet)"
    compose stop garnet >/dev/null 2>&1 || true
    sleep 5
    compose start garnet >/dev/null 2>&1 || true
    # Garnet has no /ready endpoint; wait for the health container to succeed
    local garnet_health_container
    garnet_health_container="$(service_container_id garnet-health)"
    if [[ -n "$garnet_health_container" ]]; then
        for ((attempt = 1; attempt <= 30; attempt++)); do
            if "$container_cli" exec "$garnet_health_container" \
                sh -c 'pass="$GARNET_PASSWORD"; len=$(printf %s "$pass" | wc -c); { printf "*2\r\n$4\r\nAUTH\r\n$%s\r\n%s\r\n" "$len" "$pass"; printf "*1\r\n$4\r\nPING\r\n"; } | nc -w 2 garnet 6379 | grep PONG' >/dev/null 2>&1; then
                break
            fi
            sleep 2
        done
    fi
    echo "fault-injector: [$(date +%H:%M:%S)] Garnet fault injected and recovered"
}

inject_postgres_fault() {
    echo "fault-injector: [$(date +%H:%M:%S)] injecting PostgreSQL fault (stop/restart postgres)"
    compose stop postgres >/dev/null 2>&1 || true
    sleep 5
    compose start postgres >/dev/null 2>&1 || true
    # Wait for postgres healthcheck
    local pg_container
    pg_container="$(service_container_id postgres)"
    for ((attempt = 1; attempt <= 60; attempt++)); do
        if "$container_cli" exec "$pg_container" \
            pg_isready -U "${STRESS_POSTGRES_USER:-platform}" \
            -d "${STRESS_POSTGRES_DB:-platform}" >/dev/null 2>&1; then
            break
        fi
        sleep 1
    done
    # Wait for silos to reconnect
    wait_for_ready "platform-silo-1 after pg restart" "platform-silo-1" 5000 90 || true
    wait_for_ready "platform-silo-2 after pg restart" "platform-silo-2" 5000 90 || true
    echo "fault-injector: [$(date +%H:%M:%S)] PostgreSQL fault injected and recovered"
}

inject_minio_fault() {
    echo "fault-injector: [$(date +%H:%M:%S)] injecting MinIO fault (restart object-storage)"
    compose restart object-storage >/dev/null 2>&1 || true
    # Wait for object-storage health
    local minio_container
    minio_container="$(service_container_id object-storage)"
    for ((attempt = 1; attempt <= 60; attempt++)); do
        if "$container_cli" exec "$minio_container" \
            curl --fail --silent http://127.0.0.1:9000/minio/health/live >/dev/null 2>&1; then
            break
        fi
        sleep 1
    done
    # Also restart the fault proxy so it reconnects
    compose restart object-storage-fault-proxy >/dev/null 2>&1 || true
    echo "fault-injector: [$(date +%H:%M:%S)] MinIO fault injected and recovered"
}

inject_minio_proxy_fault() {
    echo "fault-injector: [$(date +%H:%M:%S)] injecting MinIO proxy fault (arm truncate)"
    compose exec -T platform-silo-1 curl -fsS -X POST \
        http://object-storage-fault-proxy:9002/faults/clear >/dev/null 2>&1 || true
    compose exec -T platform-silo-1 curl -fsS -X POST \
        http://object-storage-fault-proxy:9002/faults/arm \
        -H 'Content-Type: application/json' \
        --data '{"mode":"truncate_request","method":"PUT","pathContains":"/items/","requestBodyBytes":16}' \
        >/dev/null 2>&1 || true
    sleep 10
    compose exec -T platform-silo-1 curl -fsS -X POST \
        http://object-storage-fault-proxy:9002/faults/clear >/dev/null 2>&1 || true
    echo "fault-injector: [$(date +%H:%M:%S)] MinIO proxy fault injected and cleared"
}

inject_tls_fault() {
    if [[ "$garnet_tls" != "true" && "$garnet_tls" != "1" ]]; then
        echo "fault-injector: [$(date +%H:%M:%S)] TLS fault skipped (Garnet TLS not enabled)"
        return 0
    fi
    echo "fault-injector: [$(date +%H:%M:%S)] injecting TLS fault (restart silo to exercise TLS reconnect)"
    compose restart platform-silo-1 >/dev/null 2>&1 || true
    wait_for_ready "platform-silo-1 after TLS fault" "platform-silo-1" 5000 90 || true
    echo "fault-injector: [$(date +%H:%M:%S)] TLS fault injected and recovered"
}

inject_process_replacement_fault() {
    echo "fault-injector: [$(date +%H:%M:%S)] injecting process replacement (recreate gateway-1)"
    local container_before
    container_before="$(service_container_id gateway-1)"
    compose up --detach --no-deps --force-recreate --no-build gateway-1 >/dev/null 2>&1 || true
    local container_after
    container_after="$(service_container_id gateway-1)"
    if [[ "$container_after" == "$container_before" ]]; then
        echo "fault-injector: gateway-1 did not receive a replacement container" >&2
        return 1
    fi
    wait_for_ready "gateway-1 after replacement" "gateway-1" 8080 90 || true
    echo "fault-injector: [$(date +%H:%M:%S)] process replacement fault injected and recovered"
}

inject_silo_replacement_fault() {
    echo "fault-injector: [$(date +%H:%M:%S)] injecting Silo replacement (recreate platform-silo-2)"
    local container_before
    container_before="$(service_container_id platform-silo-2)"
    compose up --detach --no-deps --force-recreate --no-build platform-silo-2 >/dev/null 2>&1 || true
    local container_after
    container_after="$(service_container_id platform-silo-2)"
    if [[ "$container_after" == "$container_before" ]]; then
        echo "fault-injector: platform-silo-2 did not receive a replacement container" >&2
        return 1
    fi
    wait_for_ready "platform-silo-2 after replacement" "platform-silo-2" 5000 90 || true
    wait_for_silo_cluster 2 90 || true
    echo "fault-injector: [$(date +%H:%M:%S)] Silo replacement fault injected and recovered"
}

# --- Main loop ---

started_at=$(date +%s)
fault_index=0
fault_types=(
    provider
    garnet
    postgres
    minio
    minio_proxy
    tls
    process_replacement
    silo_replacement
)

echo "fault-injector: starting (duration=${duration_seconds}s, interval=${interval_seconds}s)"

while (( $(date +%s) - started_at < duration_seconds )); do
    sleep "$interval_seconds"
    if (( $(date +%s) - started_at >= duration_seconds )); then
        break
    fi

    fault_type="${fault_types[$((fault_index % ${#fault_types[@]}))]}"
    fault_index=$((fault_index + 1))

    case "$fault_type" in
        provider)              inject_provider_fault ;;
        garnet)                inject_garnet_fault ;;
        postgres)              inject_postgres_fault ;;
        minio)                 inject_minio_fault ;;
        minio_proxy)           inject_minio_proxy_fault ;;
        tls)                   inject_tls_fault ;;
        process_replacement)   inject_process_replacement_fault ;;
        silo_replacement)      inject_silo_replacement_fault ;;
    esac

    # Brief pause between faults to let the system stabilise
    sleep 10
done

echo "fault-injector: completed $fault_index faults in $(($(date +%s) - started_at))s"
