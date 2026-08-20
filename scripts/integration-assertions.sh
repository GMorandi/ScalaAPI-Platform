#!/usr/bin/env bash
set -euo pipefail
export LC_ALL=C

usage() {
    cat <<'EOF'
Usage: scripts/integration-assertions.sh

Drive one billable request through the running integration stack
(Gateway -> Cap'n Proto dispatch -> Platform -> Provider mock) and assert the
resulting lease, hold, usage, ledger, and idempotency invariants directly in
PostgreSQL. The stack must already be up.

Required environment:
  INTEGRATION_COMPOSE_FILE   compose file of the integration stack
  INTEGRATION_PROJECT_NAME   compose project name of the stack
  POSTGRES_USER / POSTGRES_DB
  ADMIN_USERNAME / ADMIN_PASSWORD
  INTEGRATION_ENV_FILE       (optional) env file passed to compose
  INTEGRATION_GATEWAY_PORT   (default 18080) host port of the gateway
EOF
}

fail() {
    echo "integration assertions failed: $*" >&2
    exit 1
}

for command_name in docker jq curl; do
    command -v "$command_name" >/dev/null 2>&1 ||
        fail "required command not found: $command_name"
done

for variable_name in INTEGRATION_COMPOSE_FILE INTEGRATION_PROJECT_NAME \
    POSTGRES_USER POSTGRES_DB ADMIN_USERNAME ADMIN_PASSWORD; do
    [[ -n "${!variable_name:-}" ]] || fail "$variable_name is not set"
done

repo_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
cd "$repo_root"

gateway_port="${INTEGRATION_GATEWAY_PORT:-18080}"
gateway_url="http://127.0.0.1:${gateway_port}"

compose() {
    local arguments=(--project-name "$INTEGRATION_PROJECT_NAME" --file "$INTEGRATION_COMPOSE_FILE")
    if [[ -n "${INTEGRATION_ENV_FILE:-}" && -f "${INTEGRATION_ENV_FILE:-}" ]]; then
        arguments+=(--env-file "$INTEGRATION_ENV_FILE")
    fi
    docker compose "${arguments[@]}" "$@"
}

wait_for() {
    local description=$1
    local attempts=$2
    shift 2
    for ((attempt = 1; attempt <= attempts; attempt++)); do
        if "$@"; then
            return 0
        fi
        sleep 2
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

echo "== Waiting for stack readiness =="
wait_for "gateway /ready (dispatch UDS, Garnet, durable usage store)" 90 \
    curl -fsS "$gateway_url/ready" >/dev/null
wait_for "admin-api /ready" 90 \
    compose exec -T admin-api curl -fsS http://127.0.0.1:5001/ready >/dev/null
echo "PASS: gateway and admin-api report ready"

echo "== Verifying applied migrations =="
expected_migrations="$((1 + $(find deploy/migrations -maxdepth 1 -type f -name '*.sql' | wc -l)))"
migration_count="$(db_query "SELECT count(*) FROM schema_migrations;")"
assert_equals "$expected_migrations" "$migration_count" "Applied migration count" ||
    fail "unexpected migration count"
echo "PASS: $migration_count migrations applied (Orleans schema + product migrations)"

echo "== Seeding the catalog =="
login_response="$(admin_request POST /admin/auth/login \
    "$(jq -cn --arg username "$ADMIN_USERNAME" --arg password "$ADMIN_PASSWORD" \
        '{username:$username,password:$password}')")"
admin_token="$(jq -er '.token' <<<"$login_response")" ||
    fail "admin login did not return a token"

seed_response="$(admin_request POST /admin/seed/provider-mock-suite '{}' "$admin_token")"
openai_group_id="$(jq -er '.providers[] | select(.provider == "openai") | .group_id' \
    <<<"$seed_response")" ||
    fail "provider-mock-suite seed did not return an OpenAI group"
assert_equals "3" "$(jq -er '.providers | length' <<<"$seed_response")" \
    "Seeded provider count" || fail "unexpected seeded provider count"
echo "PASS: provider mock suite seeded"

echo "== Creating the test user and API key =="
user_email="integration@scalaapi.test"
user_password="integration-user-password"
register_response="$(admin_request POST /auth/register \
    "$(jq -cn --arg email "$user_email" --arg password "$user_password" \
        '{email:$email,password:$password,displayName:"Paired integration"}')")"
user_id="$(jq -er '.id' <<<"$register_response")" ||
    fail "user registration did not return an id"

admin_request PUT "/admin/users/$user_id" \
    "$(jq -cn --argjson groups "[${openai_group_id}]" \
        '{role:"user",concurrency:4,rpmLimit:0,allowedGroups:$groups}')" \
    "$admin_token" >/dev/null

balance_response="$(admin_request POST "/admin/users/$user_id/balance" \
    '{"delta":1000,"reason":"Paired integration funding"}' \
    "$admin_token" "integration-balance-key")"
jq -e '.balance == 1000 and .duplicate == false' <<<"$balance_response" >/dev/null ||
    fail "balance funding did not settle as expected"

api_key="$(admin_request POST /admin/apikeys/ \
    "$(jq -cn --argjson user "$user_id" --argjson group "$openai_group_id" \
        '{userId:$user,groupId:$group,quota:100,expiresAt:null,ipWhitelist:[],ipBlacklist:[],rateLimit5h:0,rateLimit1d:0,rateLimit7d:0}')" \
    "$admin_token" | jq -er '.key')" ||
    fail "API key creation did not return a key"
echo "PASS: user funded and API key issued"

echo "== Publishing a price version =="
admin_request POST /admin/pricing/versions \
    '{"version":"integration-v1","model":"gpt-4o","inputUsdPerMillion":2.5,"outputUsdPerMillion":10,"cacheReadUsdPerMillion":0,"cacheWriteUsdPerMillion":1.25,"effectiveFrom":"1970-01-01T00:00:00Z","effectiveUntil":null}' \
    "$admin_token" >/dev/null
echo "PASS: price version published"

echo "== Waiting for the anonymous model catalog =="
models_ready() {
    curl -fsS "$gateway_url/v1/models" 2>/dev/null | grep -q published
}
wait_for "gateway /v1/models catalog from Garnet" 30 models_ready ||
    fail "model catalog did not reach the gateway cache"
echo "PASS: gateway serves the published model catalog"

echo "== Sending one billable chat request through the pair =="
chat_request_id="integration-chat"
chat_idempotency_key="integration-chat-idem"
chat_body='{"model":"gpt-4o","messages":[{"role":"user","content":"paired integration check"}],"stream":false}'
chat_response="$(curl -fsS --max-time 120 \
    -H "Authorization: Bearer $api_key" \
    -H "Content-Type: application/json" \
    -H "X-Request-ID: $chat_request_id" \
    -H "Idempotency-Key: $chat_idempotency_key" \
    --data "$chat_body" \
    "$gateway_url/v1/chat/completions")"
jq -e '.choices | length > 0' <<<"$chat_response" >/dev/null ||
    fail "chat completion did not return choices"
echo "PASS: chat completion returned through gateway dispatch"

echo "== Asserting settlement invariants =="
settlement_complete() {
    [[ "$(db_query "SELECT count(*) FROM request_leases WHERE request_id = '$chat_request_id' AND status = 'completed';")" == "1" ]]
}
wait_for "chat request settlement" 30 settlement_complete ||
    fail "chat request did not reach a completed lease"

settlement_state="$(db_query "
WITH target_leases AS (
  SELECT lease_token, request_id, status
  FROM request_leases
  WHERE request_id = '$chat_request_id'
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
  (SELECT count(*) FROM request_idempotency WHERE idempotency_key = '$chat_idempotency_key' AND status = 'completed');")"
assert_equals "1|1|0|1|1|0|1|1|1|1" "$settlement_state" \
    "chat settlement invariants" ||
    fail "settlement invariants violated (leases|completed|reconciliation_needed|holds|committed|active|usage_events|usage_logs|ledger|idempotency)"
echo "PASS: one completed lease, committed hold, usage event, ledger entry, and idempotency record"

# --- Dual-process leadership ---
echo "Checking dual-process leadership..."

silo1_id="$(compose ps -q platform-silo-1)"
[[ -n "$silo1_id" ]] || fail "platform-silo-1 container not found"
silo1_healthy="$(docker inspect --format='{{.State.Health.Status}}' "$silo1_id")"
assert_equals "healthy" "$silo1_healthy" "platform-silo-1 health" ||
    fail "platform-silo-1 is not healthy: $silo1_healthy"

silo2_id="$(compose ps -q platform-silo-2)"
[[ -n "$silo2_id" ]] || fail "platform-silo-2 container not found"
silo2_healthy="$(docker inspect --format='{{.State.Health.Status}}' "$silo2_id")"
assert_equals "healthy" "$silo2_healthy" "platform-silo-2 health" ||
    fail "platform-silo-2 is not healthy: $silo2_healthy"
echo "PASS: both silo containers are healthy"

active_claims="$(db_query "SELECT count(*) FROM backup_schedule_claims WHERE expires_at > now();")"
[[ "$active_claims" =~ ^[0-9]+$ ]] || fail "unable to read backup schedule claim count"
if (( active_claims <= 1 )); then
    echo "PASS: at most one active backup schedule claim ($active_claims)"
else
    fail "expected at most 1 active backup schedule claim, got $active_claims"
fi

echo "integration assertions passed"
