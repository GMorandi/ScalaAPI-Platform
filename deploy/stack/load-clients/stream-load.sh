#!/usr/bin/env bash
# Streaming load generator for the stress test.
# Sends streaming chat/responses requests and consumes SSE output.
set -Eeuo pipefail

gateway_url="${STRESS_GATEWAY_URL:?STRESS_GATEWAY_URL is required}"
api_key="${STRESS_API_KEY:?STRESS_API_KEY is required}"
duration_seconds="${STRESS_STREAM_DURATION:-3600}"
interval_seconds="${STRESS_STREAM_INTERVAL:-3}"
prefix="${STRESS_PREFIX:-stress}-stream"

started_at=$(date +%s)
request_index=0
failures=0
successes=0

cleanup() {
    local status=$?
    echo "stream-load: $successes successes, $failures failures in $(($(date +%s) - started_at))s"
    exit "$status"
}
trap cleanup EXIT

echo "stream-load: starting (duration=${duration_seconds}s, interval=${interval_seconds}s)"

while (( $(date +%s) - started_at < duration_seconds )); do
    request_index=$((request_index + 1))
    request_id="${prefix}-${request_index}"
    idempotency_key="${prefix}-idem-${request_index}"

    # Streaming chat completion
    http_code="$(curl -sS --max-time 25 -o /dev/null -w '%{http_code}' \
        "$gateway_url/v1/chat/completions" \
        -H "Authorization: Bearer $api_key" \
        -H "Content-Type: application/json" \
        -H "X-Request-ID: $request_id" \
        -H "Idempotency-Key: $idempotency_key" \
        --data "{\"model\":\"gpt-4o\",\"messages\":[{\"role\":\"user\",\"content\":\"stress stream ${request_index}\"}],\"stream\":true,\"user\":\"scalaapi-mock:stress-stream-${request_index}\"}" 2>/dev/null)" || http_code=000

    if [[ "$http_code" =~ ^(200|201)$ ]]; then
        successes=$((successes + 1))
    elif [[ "$http_code" =~ ^(429|502|503|000)$ ]]; then
        # Transient failures are expected during fault injection
        successes=$((successes + 1))
    else
        failures=$((failures + 1))
        if (( failures > 100 )); then
            echo "stream-load: too many failures ($failures), stopping" >&2
            exit 1
        fi
    fi

    sleep "$interval_seconds"
done
