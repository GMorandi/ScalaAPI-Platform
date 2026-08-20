#!/usr/bin/env bash
set -euo pipefail
export LC_ALL=C

usage() {
    cat <<'EOF'
Usage: scripts/collect-gate-results.sh --output gates.json

Collect the real per-gate job conclusions of the current workflow run from the
GitHub Actions API and verify that every expected release gate is present and
successful. The output gates.json is consumed by
scripts/generate-release-evidence.sh.

Required environment:
  GITHUB_REPOSITORY   owner/name of the workflow repository
  GITHUB_RUN_ID       id of the current run
  GITHUB_RUN_ATTEMPT  attempt number of the current run
  GITHUB_TOKEN        token with actions:read and checks:read (or GH_TOKEN)
Optional environment:
  GITHUB_API_URL      defaults to https://api.github.com
EOF
}

fail() {
    echo "gate result collection failed: $*" >&2
    exit 1
}

output_path=""
while (( $# > 0 )); do
    case "$1" in
        --output)
            (( $# >= 2 )) || fail "missing value for --output"
            output_path="$2"
            shift 2
            ;;
        -h|--help)
            usage
            exit 0
            ;;
        *)
            usage >&2
            fail "unknown argument: $1"
            ;;
    esac
done

[[ -n "$output_path" ]] || { usage >&2; exit 2; }

command -v gh >/dev/null 2>&1 || fail "required command not found: gh"
command -v jq >/dev/null 2>&1 || fail "required command not found: jq"

for variable_name in GITHUB_REPOSITORY GITHUB_RUN_ID GITHUB_RUN_ATTEMPT; do
    [[ -n "${!variable_name:-}" ]] || fail "$variable_name is not set"
done

token="${GITHUB_TOKEN:-${GH_TOKEN:-}}"
[[ -n "$token" ]] || fail "GITHUB_TOKEN or GH_TOKEN is not set"
api_url="${GITHUB_API_URL:-https://api.github.com}"

# Every expected release gate by its check-run name. Keep this list in sync
# with the job names in .github/workflows/{dotnet,gateway,admin-web,user-web,stack}.yml.
expected_gates=(
    "Platform .NET build, tests, and benchmark smoke"
    "Gateway build, test, and benchmark smoke"
    "admin-web typecheck, build, and e2e"
    "user-web typecheck, build, and e2e"
    "Build and scan gateway image"
    "Build and scan platform-silo image"
    "Build and scan admin-api image"
    "Build and scan migrator image"
    "Build and scan provider-mock image"
    "Full stack integration and live UI"
)

jobs_json="$(GH_TOKEN="$token" gh api \
    "$api_url/repos/$GITHUB_REPOSITORY/actions/runs/$GITHUB_RUN_ID/attempts/$GITHUB_RUN_ATTEMPT/jobs?per_page=100" \
    --jq '.jobs' ||
    fail "could not query the workflow run jobs API")"

[[ "$(jq -r 'type' <<<"$jobs_json")" == "array" ]] ||
    fail "workflow run jobs API did not return a job array"

missing_gates=()
failed_gates=()
gates_entries="[]"

for gate_name in "${expected_gates[@]}"; do
    matching_jobs="$(jq --arg name "$gate_name" \
        'map(select(.name == $name or (.name | endswith(" / " + $name))))' <<<"$jobs_json")"
    match_count="$(jq 'length' <<<"$matching_jobs")"
    if (( match_count == 0 )); then
        missing_gates+=("$gate_name")
        continue
    fi
    conclusions="$(jq -r '.[].conclusion' <<<"$matching_jobs" | sort -u)"
    statuses="$(jq -r '.[].status' <<<"$matching_jobs" | sort -u)"
    if [[ "$statuses" != "completed" ]]; then
        failed_gates+=("$gate_name (status: $statuses)")
        continue
    fi
    if [[ "$conclusions" != "success" ]]; then
        failed_gates+=("$gate_name (conclusion: $conclusions)")
        continue
    fi
    job_url="$(jq -r --arg name "$gate_name" \
        'map(select(.name == $name or (.name | endswith(" / " + $name))))[0].html_url' <<<"$jobs_json")"
    gates_entries="$(jq --arg name "$gate_name" --arg url "$job_url" \
        '. + [{name: $name, conclusion: "success", job_url: $url}]' \
        <<<"$gates_entries")"
done

if (( ${#missing_gates[@]} > 0 )); then
    printf 'missing expected gate: %s\n' "${missing_gates[@]}" >&2
fi
if (( ${#failed_gates[@]} > 0 )); then
    printf 'gate did not succeed: %s\n' "${failed_gates[@]}" >&2
fi
if (( ${#missing_gates[@]} > 0 || ${#failed_gates[@]} > 0 )); then
    fail "the producing run does not demonstrate a fully green release gates"
fi

jq -n \
    --arg repository "$GITHUB_REPOSITORY" \
    --arg run_id "$GITHUB_RUN_ID" \
    --arg run_attempt "$GITHUB_RUN_ATTEMPT" \
    --argjson gates "$gates_entries" \
    '{
        status: "passed",
        repository: $repository,
        run_id: $run_id,
        run_attempt: $run_attempt,
        gates: $gates
    }' > "$output_path"

gate_count="$(jq '.gates | length' "$output_path")"
echo "collected $gate_count successful gate conclusions into $output_path"
