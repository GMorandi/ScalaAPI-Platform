#!/usr/bin/env bash
# Media due-work load generator for the stress test.
# Submits image batch requests at a configurable rate and polls for completion.
set -Eeuo pipefail

gateway_url="${STRESS_GATEWAY_URL:?STRESS_GATEWAY_URL is required}"
api_key="${STRESS_API_KEY:?STRESS_API_KEY is required}"
duration_seconds="${STRESS_MEDIA_DURATION:-3600}"
interval_seconds="${STRESS_MEDIA_INTERVAL:-5}"
prefix="${STRESS_PREFIX:-stress}-media"

started_at=$(date +%s)
request_index=0
failures=0
successes=0

cleanup() {
    local status=$?
    echo "media-load: $successes successes, $failures failures in $(($(date +%s) - started_at))s"
    exit "$status"
}
trap cleanup EXIT

echo "media-load: starting (duration=${duration_seconds}s, interval=${interval_seconds}s)"

while (( $(date +%s) - started_at < duration_seconds )); do
    request_index=$((request_index + 1))
    request_id="${prefix}-${request_index}"
    idempotency_key="${prefix}-idem-${request_index}"

    response="$(curl -sS --max-time 30 --write-out $'\n%{http_code}' \
        "$gateway_url/v1/images/batches" \
        -H "Authorization: Bearer $api_key" \
        -H "Content-Type: application/json" \
        -H "X-Request-ID: $request_id" \
        -H "Idempotency-Key: $idempotency_key" \
        --data "{\"model\":\"mock-image-1\",\"items\":[{\"custom_id\":\"stress-${request_index}\",\"prompt\":\"stress media ${request_index}\"}]}" 2>/dev/null)" || true

    http_code="${response##*$'\n'}"
    response_body="${response%$'\n'*}"

    if [[ "$http_code" =~ ^(200|201)$ ]]; then
        batch_id="$(jq -er '.id' <<<"$response_body" 2>/dev/null)" || batch_id=""
        if [[ -n "$batch_id" ]]; then
            # Poll for completion (non-blocking, short timeout)
            for ((poll = 0; poll < 6; poll++)); do
                poll_response="$(curl -sS --max-time 5 \
                    "$gateway_url/v1/images/batches/$batch_id" \
                    -H "Authorization: Bearer $api_key" 2>/dev/null)" || true
                poll_status="$(jq -r '.status' <<<"$poll_response" 2>/dev/null)" || poll_status=""
                if [[ "$poll_status" == "succeeded" || "$poll_status" == "failed" ]]; then
                    break
                fi
                sleep 1
            done
        fi
        successes=$((successes + 1))
    else
        failures=$((failures + 1))
        if (( failures > 100 )); then
            echo "media-load: too many consecutive failures ($failures), stopping" >&2
            exit 1
        fi
    fi

    sleep "$interval_seconds"
done
