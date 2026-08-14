#!/usr/bin/env bash
# Metrics collector for the one-hour stress test.
# Periodically samples p95 latency, connection counts, buffer sizes, lease
# counts, hold counts, and outbox backlog from the running stack.
set -Eeuo pipefail

stack_dir="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"

# --- Configuration ---
container_cli="${STRESS_CONTAINER_CLI:?STRESS_CONTAINER_CLI is required}"
project="${STRESS_PROJECT:?STRESS_PROJECT is required}"
duration_seconds="${STRESS_METRICS_DURATION:-3600}"
interval_seconds="${STRESS_METRICS_INTERVAL:-30}"
output_dir="${STRESS_METRICS_DIR:?STRESS_METRICS_DIR is required}"
gateway_url="${STRESS_GATEWAY_URL:?STRESS_GATEWAY_URL is required}"
compose_files_arg="${STRESS_COMPOSE_FILES:?STRESS_COMPOSE_FILES is required}"

compose() {
    local compose_arguments=(--project-name "$project")
    IFS='|' read -ra files <<<"$compose_files_arg"
    for file in "${files[@]}"; do
        compose_arguments+=(--file "$file")
    done
    "$container_cli" compose "${compose_arguments[@]}" "$@"
}

db_query() {
    compose exec -T postgres psql --no-psqlrc --tuples-only --no-align \
        --set ON_ERROR_STOP=1 \
        --username "${STRESS_POSTGRES_USER:-platform}" \
        --dbname "${STRESS_POSTGRES_DB:-platform}" \
        --command "$1" 2>/dev/null | tr -d '\r'
}

mkdir -p "$output_dir"

latency_log="$output_dir/latency-samples.csv"
connections_log="$output_dir/connections.csv"
leases_log="$output_dir/leases.csv"
holds_log="$output_dir/holds.csv"
outbox_log="$output_dir/outbox.csv"
summary_log="$output_dir/metrics-summary.csv"

echo "timestamp,p95_ms,mean_ms,sample_count" > "$latency_log"
echo "timestamp,active_connections,gateway_1_connections,gateway_2_connections" > "$connections_log"
echo "timestamp,active_leases,completed_leases,aborted_leases,reconciliation_needed" > "$leases_log"
echo "timestamp,active_holds,released_holds" > "$holds_log"
echo "timestamp,outbox_pending,outbox_processing,outbox_total" > "$outbox_log"
echo "timestamp,p95_ms,active_leases,active_holds,outbox_pending,active_connections" > "$summary_log"

started_at=$(date +%s)
sample_index=0
latency_history=()

echo "metrics-collector: starting (duration=${duration_seconds}s, interval=${interval_seconds}s, output=$output_dir)"

while (( $(date +%s) - started_at < duration_seconds )); do
    timestamp=$(date -u +%Y-%m-%dT%H:%M:%SZ)
    sample_index=$((sample_index + 1))

    # --- p95 latency via a probe request ---
    probe_start_ns=$(date +%s%N 2>/dev/null || echo 0)
    http_code="$(curl -sS --max-time 15 -o /dev/null -w '%{http_code}' \
        "$gateway_url/v1/chat/completions" \
        -H "Authorization: Bearer ${STRESS_API_KEY:?STRESS_API_KEY is required}" \
        -H "Content-Type: application/json" \
        -H "X-Request-ID: stress-metrics-probe-${sample_index}" \
        -H "Idempotency-Key: stress-metrics-idem-${sample_index}" \
        --data '{"model":"gpt-4o","messages":[{"role":"user","content":"metrics probe"}],"stream":false,"user":"scalaapi-mock:metrics-probe"}' 2>/dev/null)" || http_code=000
    probe_end_ns=$(date +%s%N 2>/dev/null || echo 0)

    if [[ "$probe_start_ns" != "0" && "$probe_end_ns" != "0" ]]; then
        latency_ms=$(( (probe_end_ns - probe_start_ns) / 1000000 ))
    else
        latency_ms=0
    fi
    latency_history+=("$latency_ms")

    # Keep only the last 120 samples for p95 calculation (1 hour at 30s = 120)
    if (( ${#latency_history[@]} > 120 )); then
        latency_history=("${latency_history[@]: -120}")
    fi

    # Calculate p95 and mean from recent history
    sorted_latency=($(printf '%s\n' "${latency_history[@]}" | sort -n))
    count=${#sorted_latency[@]}
    if (( count > 0 )); then
        p95_index=$(( (count * 95 + 99) / 100 ))
        (( p95_index > count )) && p95_index=$count
        (( p95_index < 1 )) && p95_index=1
        p95_ms="${sorted_latency[$((p95_index - 1))]}"
        total=0
        for v in "${sorted_latency[@]}"; do
            total=$((total + v))
        done
        mean_ms=$((total / count))
    else
        p95_ms=0
        mean_ms=0
    fi

    echo "$timestamp,$p95_ms,$mean_ms,$count" >> "$latency_log"

    # --- Connection counts ---
    gateway_1_conns=0
    gateway_2_conns=0
    gateway_1_id="$("$container_cli" ps --all \
        --filter "label=com.docker.compose.project=$project" \
        --filter "label=com.docker.compose.service=gateway-1" \
        --format '{{.ID}}' | tr -d '\r')" || true
    gateway_2_id="$("$container_cli" ps --all \
        --filter "label=com.docker.compose.project=$project" \
        --filter "label=com.docker.compose.service=gateway-2" \
        --format '{{.ID}}' | tr -d '\r')" || true
    if [[ -n "$gateway_1_id" ]]; then
        gateway_1_conns="$("$container_cli" exec "$gateway_1_id" \
            sh -c 'ss -t state established 2>/dev/null | tail -n +2 | wc -l' 2>/dev/null | tr -d ' \r')" || gateway_1_conns=0
    fi
    if [[ -n "$gateway_2_id" ]]; then
        gateway_2_conns="$("$container_cli" exec "$gateway_2_id" \
            sh -c 'ss -t state established 2>/dev/null | tail -n +2 | wc -l' 2>/dev/null | tr -d ' \r')" || gateway_2_conns=0
    fi
    active_connections=$((gateway_1_conns + gateway_2_conns))
    echo "$timestamp,$active_connections,$gateway_1_conns,$gateway_2_conns" >> "$connections_log"

    # --- Lease counts ---
    lease_state="$(db_query "SELECT
        (SELECT count(*) FROM request_leases WHERE status = 'active') || '|' ||
        (SELECT count(*) FROM request_leases WHERE status = 'completed') || '|' ||
        (SELECT count(*) FROM request_leases WHERE status = 'aborted') || '|' ||
        (SELECT count(*) FROM request_leases WHERE status = 'reconciliation_needed');" 2>/dev/null)" || lease_state="0|0|0|0"
    IFS='|' read -r active_leases completed_leases aborted_leases reconciliation_needed <<<"$lease_state"
    echo "$timestamp,${active_leases:-0},${completed_leases:-0},${aborted_leases:-0},${reconciliation_needed:-0}" >> "$leases_log"

    # --- Hold counts ---
    hold_state="$(db_query "SELECT
        (SELECT count(*) FROM balance_holds WHERE status = 'active') || '|' ||
        (SELECT count(*) FROM balance_holds WHERE status = 'released');" 2>/dev/null)" || hold_state="0|0"
    IFS='|' read -r active_holds released_holds <<<"$hold_state"
    echo "$timestamp,${active_holds:-0},${released_holds:-0}" >> "$holds_log"

    # --- Outbox backlog ---
    outbox_state="$(db_query "SELECT
        (SELECT count(*) FROM usage_outbox WHERE processed_at IS NULL AND dead_lettered_at IS NULL) || '|' ||
        (SELECT count(*) FROM usage_outbox WHERE processed_at IS NULL AND dead_lettered_at IS NOT NULL) || '|' ||
        (SELECT count(*) FROM usage_outbox WHERE processed_at IS NULL);" 2>/dev/null)" || outbox_state="0|0|0"
    IFS='|' read -r outbox_pending outbox_processing outbox_total <<<"$outbox_state"
    echo "$timestamp,${outbox_pending:-0},${outbox_processing:-0},${outbox_total:-0}" >> "$outbox_log"

    # --- Summary ---
    echo "$timestamp,$p95_ms,${active_leases:-0},${active_holds:-0},${outbox_pending:-0},$active_connections" >> "$summary_log"

    if (( sample_index % 10 == 0 )); then
        echo "metrics-collector: sample $sample_index at $timestamp (p95=${p95_ms}ms, leases=${active_leases:-0}, holds=${active_holds:-0}, outbox=${outbox_pending:-0})"
    fi

    sleep "$interval_seconds"
done

echo "metrics-collector: completed $sample_index samples in $(($(date +%s) - started_at))s"
echo "metrics-collector: output written to $output_dir"
