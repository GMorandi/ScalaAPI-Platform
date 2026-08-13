#!/usr/bin/env bash
# Backpressure load generator for the stress test.
# Sends a burst of concurrent requests to saturate Gateway/Silo capacity,
# then measures how the system recovers under sustained pressure.
set -Eeuo pipefail

gateway_url="${STRESS_GATEWAY_URL:?STRESS_GATEWAY_URL is required}"
api_key="${STRESS_API_KEY:?STRESS_API_KEY is required}"
duration_seconds="${STRESS_BACKPRESSURE_DURATION:-3600}"
burst_size="${STRESS_BACKPRESSURE_BURST:-16}"
burst_interval="${STRESS_BACKPRESSURE_INTERVAL:-8}"
prefix="${STRESS_PREFIX:-stress}-bp"

started_at=$(date +%s)
burst_index=0
total_successes=0
total_failures=0
total_429s=0

cleanup() {
    local status=$?
    echo "backpressure-load: $total_successes successes, $total_failures failures " \
         "($total_429s rate-limited) in $(($(date +%s) - started_at))s"
    exit "$status"
}
trap cleanup EXIT

echo "backpressure-load: starting (duration=${duration_seconds}s, burst=${burst_size}, interval=${burst_interval}s)"

while (( $(date +%s) - started_at < duration_seconds )); do
    burst_index=$((burst_index + 1))
    burst_successes=0
    burst_failures=0
    burst_429s=0

    # Launch a burst of concurrent requests
    pids=()
    tmpdir="$(mktemp -d "${TMPDIR:-/tmp}/stress-bp.XXXXXX")"
    for ((i = 0; i < burst_size; i++)); do
        (
            request_id="${prefix}-${burst_index}-${i}"
            idempotency_key="${prefix}-idem-${burst_index}-${i}"
            http_code="$(curl -sS --max-time 15 -o /dev/null -w '%{http_code}' \
                "$gateway_url/v1/chat/completions" \
                -H "Authorization: Bearer $api_key" \
                -H "Content-Type: application/json" \
                -H "X-Request-ID: $request_id" \
                -H "Idempotency-Key: $idempotency_key" \
                --data "{\"model\":\"gpt-4o\",\"messages\":[{\"role\":\"user\",\"content\":\"backpressure burst ${burst_index} item ${i}\"}],\"stream\":false,\"user\":\"scalaapi-mock:stress-bp-${burst_index}-${i}\"}" 2>/dev/null)" || http_code=000
            echo "$http_code" > "$tmpdir/$i"
        ) &
        pids+=($!)
    done

    # Wait for all burst requests
    for pid in "${pids[@]}"; do
        wait "$pid" 2>/dev/null || true
    done

    # Collect results
    for ((i = 0; i < burst_size; i++)); do
        if [[ -f "$tmpdir/$i" ]]; then
            code="$(cat "$tmpdir/$i")"
            if [[ "$code" =~ ^(200|201)$ ]]; then
                burst_successes=$((burst_successes + 1))
            elif [[ "$code" == "429" ]]; then
                burst_429s=$((burst_429s + 1))
            else
                burst_failures=$((burst_failures + 1))
            fi
        fi
    done
    rm -rf "$tmpdir"

    total_successes=$((total_successes + burst_successes))
    total_failures=$((total_failures + burst_failures))
    total_429s=$((total_429s + burst_429s))

    echo "backpressure-load: burst $burst_index -> $burst_successes ok, $burst_429s rate-limited, $burst_failures failed"

    sleep "$burst_interval"
done
