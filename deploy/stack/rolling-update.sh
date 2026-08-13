#!/usr/bin/env bash
# Rolling replacement of Silo and Gateway instances.
#
# Usage:
#   ./rolling-update.sh [--rebuild] [--silo-only|--gateway-only]
#
# The script replaces one instance at a time, waiting for the surviving peer
# to absorb traffic and the replacement to become ready before continuing.
set -Eeuo pipefail

stack_dir="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
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
    compose_files+=("$tls_compose_file")
fi

rebuild=false
silo_only=false
gateway_only=false
drain_timeout="${ROLLING_DRAIN_TIMEOUT:-30}"
readiness_timeout="${ROLLING_READINESS_TIMEOUT:-120}"

for arg in "$@"; do
    case "$arg" in
        --rebuild) rebuild=true ;;
        --silo-only) silo_only=true ;;
        --gateway-only) gateway_only=true ;;
        --drain-timeout=*) drain_timeout="${arg#*=}" ;;
        --readiness-timeout=*) readiness_timeout="${arg#*=}" ;;
        *) echo "Unknown option: $arg" >&2; exit 2 ;;
    esac
done

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

project="${SMOKE_PROJECT_NAME:-scalaapi}"
compose() {
    local compose_arguments=(--project-name "$project")
    for file in "${compose_files[@]}"; do
        compose_arguments+=(--file "$file")
    done
    "$container_cli" compose "${compose_arguments[@]}" "$@"
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

service_container_id() {
    local service=$1
    "$container_cli" ps --all \
        --filter "label=com.docker.compose.project=$project" \
        --filter "label=com.docker.compose.service=$service" \
        --format '{{.ID}}' | tr -d '\r'
}

service_health_url() {
    local service=$1
    local port=$2
    echo "http://127.0.0.1:${port}/ready"
}

wait_for_service_ready() {
    local service=$1
    local port=$2
    local description="${3:-$service readiness}"
    local container_id
    container_id="$(service_container_id "$service")"
    if [[ -z "$container_id" ]]; then
        echo "No container found for service '$service'" >&2
        return 1
    fi
    wait_for "$description" "$readiness_timeout" \
        "$container_cli" exec "$container_id" \
        curl -fsS "$(service_health_url "$service" "$port")" >/dev/null
}

wait_for_silo_cluster_count() {
    local expected=$1
    local description="${2:-$expected active silo(s)}"
    local pg_container
    pg_container="$(service_container_id postgres)"
    wait_for "$description" "$readiness_timeout" bash -c "
        count=\$(\"$container_cli\" exec '$pg_container' psql --no-psqlrc --tuples-only --no-align \
            --set ON_ERROR_STOP=1 --username \"\${POSTGRES_USER:-platform}\" --dbname \"\${POSTGRES_DB:-platform}\" \
            --command \"SELECT count(*) FROM OrleansMembershipTable WHERE DeploymentId = 'platform' AND Status = 3;\" 2>/dev/null | tr -d '\r')
        [[ \"\$count\" == '$expected' ]]
    "
}

drain_and_stop_silo() {
    local service=$1
    echo "==> Draining $service (stop_grace_period=${drain_timeout}s)..."
    # Docker/Podman sends SIGTERM on stop; the .NET host performs graceful drain.
    # stop_grace_period in compose controls how long before SIGKILL.
    compose stop --timeout "$drain_timeout" "$service" >/dev/null
    echo "    $service stopped."
}

drain_and_stop_gateway() {
    local service=$1
    echo "==> Draining $service (stop_grace_period=${drain_timeout}s)..."
    compose stop --timeout "$drain_timeout" "$service" >/dev/null
    echo "    $service stopped."
}

replace_silo() {
    local service=$1
    local peer=$2
    echo ""
    echo "=== Rolling replacement: $service ==="

    drain_and_stop_silo "$service"

    echo "    Waiting for peer ($peer) to absorb traffic..."
    wait_for_service_ready "$peer" 5000 "peer $peer readiness during replacement"

    echo "    Recreating $service..."
    if [[ "$rebuild" == "true" ]]; then
        compose up --detach --no-deps --build "$service" >/dev/null
    else
        compose up --detach --no-deps --force-recreate --no-build "$service" >/dev/null
    fi

    echo "    Waiting for $service readiness..."
    wait_for_service_ready "$service" 5000 "$service readiness after replacement"

    echo "    Waiting for 2-silo cluster..."
    wait_for_silo_cluster_count 2 "two active silos after $service replacement"

    echo "    $service replacement complete."
}

replace_gateway() {
    local service=$1
    local peer=$2
    echo ""
    echo "=== Rolling replacement: $service ==="

    drain_and_stop_gateway "$service"

    echo "    Waiting for peer ($peer) to absorb traffic..."
    wait_for_service_ready "$peer" 8080 "peer $peer readiness during gateway replacement"

    echo "    Recreating $service..."
    if [[ "$rebuild" == "true" ]]; then
        compose up --detach --no-deps --build "$service" >/dev/null
    else
        compose up --detach --no-deps --force-recreate --no-build "$service" >/dev/null
    fi

    echo "    Waiting for $service readiness..."
    wait_for_service_ready "$service" 8080 "$service readiness after replacement"

    echo "    $service replacement complete."
}

echo "Rolling update starting (rebuild=$rebuild, silo_only=$silo_only, gateway_only=$gateway_only)"
echo "  drain_timeout=${drain_timeout}s, readiness_timeout=${readiness_timeout}s"

# Verify both peers are initially healthy
echo ""
echo "==> Verifying initial cluster state..."
wait_for_service_ready platform-silo-1 5000 "initial platform-silo-1 readiness"
wait_for_service_ready platform-silo-2 5000 "initial platform-silo-2 readiness"
wait_for_service_ready gateway-1 8080 "initial gateway-1 readiness"
wait_for_service_ready gateway-2 8080 "initial gateway-2 readiness"
wait_for_silo_cluster_count 2 "initial two-silo cluster"
echo "    Initial cluster healthy."

# Rolling replacement: Silos first, then Gateways
if [[ "$gateway_only" != "true" ]]; then
    replace_silo platform-silo-2 platform-silo-1
    replace_silo platform-silo-1 platform-silo-2
fi

if [[ "$silo_only" != "true" ]]; then
    replace_gateway gateway-2 gateway-1
    replace_gateway gateway-1 gateway-2
fi

echo ""
echo "==> Verifying final cluster state..."
wait_for_service_ready platform-silo-1 5000 "final platform-silo-1 readiness"
wait_for_service_ready platform-silo-2 5000 "final platform-silo-2 readiness"
wait_for_service_ready gateway-1 8080 "final gateway-1 readiness"
wait_for_service_ready gateway-2 8080 "final gateway-2 readiness"
wait_for_silo_cluster_count 2 "final two-silo cluster"

echo ""
echo "Rolling update complete. All instances healthy."
