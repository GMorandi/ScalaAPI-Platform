#!/usr/bin/env bash
set -euo pipefail
export LC_ALL=C

usage() {
    cat <<'EOF'
Usage: scripts/collect-gate-results.sh --output gates.json

Collect the real per-gate job conclusions of the current workflow run from the
GitHub Actions API and verify that every expected release gate is present and
successful. Then download the run's test result artifacts (TRX, ctest JUnit
XML, Playwright JSON, integration assertion log) and aggregate real
executed/passed/failed/skipped totals per gate. The output gates.json is
consumed by scripts/generate-release-evidence.sh.

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
command -v unzip >/dev/null 2>&1 || fail "required command not found: unzip"
command -v sha256sum >/dev/null 2>&1 || fail "required command not found: sha256sum"

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

# --- Skipped jobs -------------------------------------------------------------
# Every job of this attempt whose conclusion is "skipped". An expected gate can
# never appear here: a skipped expected gate fails the validation above.
skipped_jobs_json="$(jq -c \
    '[.[] | select(.conclusion == "skipped") | .name] | unique' <<<"$jobs_json")"

# --- Test result artifacts ----------------------------------------------------
artifacts_json="$(GH_TOKEN="$token" gh api \
    "$api_url/repos/$GITHUB_REPOSITORY/actions/runs/$GITHUB_RUN_ID/artifacts?per_page=100" \
    --jq '.artifacts' ||
    fail "could not query the workflow run artifacts API")"

[[ "$(jq -r 'type' <<<"$artifacts_json")" == "array" ]] ||
    fail "workflow run artifacts API did not return an artifact array"

artifacts_dir="$(mktemp -d)"
cleanup() {
    rm -rf "$artifacts_dir"
}
trap cleanup EXIT

# Extract one numeric attribute from an XML element given on stdin.
xml_attr() {
    sed -n "s/.* $1=\"\\([0-9]*\\)\".*/\\1/p"
}

# Print "executed passed failed skipped" from a TRX ResultSummary/Counters
# element; return 1 when the file carries no Counters element.
parse_trx_counters() {
    local result_file="$1"
    local counters executed passed failed not_executed
    counters="$(grep -m1 '<Counters' "$result_file")" || return 1
    executed="$(xml_attr executed <<<"$counters")"
    passed="$(xml_attr passed <<<"$counters")"
    failed="$(xml_attr failed <<<"$counters")"
    not_executed="$(xml_attr notExecuted <<<"$counters")"
    [[ -n "$executed" && -n "$passed" && -n "$failed" ]] || return 1
    echo "$executed $passed $failed ${not_executed:-0}"
}

# Print "executed passed failed skipped" from ctest JUnit XML (<testsuite>
# totals plus <skipped/> testcase markers); return 1 without a testsuite.
parse_junit_results() {
    local result_file="$1"
    grep -q '<testsuite ' "$result_file" || return 1
    local tests failures skipped skipped_cases
    tests="$(grep -o '<testsuite [^>]*' "$result_file" |
        xml_attr tests | awk '{s += $1} END {print s + 0}')"
    failures="$(grep -o '<testsuite [^>]*' "$result_file" |
        xml_attr failures | awk '{s += $1} END {print s + 0}')"
    skipped="$(grep -o '<testsuite [^>]*' "$result_file" |
        xml_attr skipped | awk '{s += $1} END {print s + 0}')"
    skipped_cases="$(grep -c '<skipped' "$result_file" || true)"
    if (( skipped == 0 && skipped_cases > 0 )); then
        skipped="$skipped_cases"
    fi
    (( tests > 0 )) || return 1
    echo "$((tests - skipped)) $((tests - skipped - failures)) $failures $skipped"
}

# Print "executed passed failed skipped" from Playwright JSON stats; return 1
# when the file does not carry a stats object.
parse_playwright_stats() {
    local result_file="$1"
    jq -er '
        .stats as $stats |
        "\($stats.expected + $stats.flaky + $stats.unexpected) \($stats.expected + $stats.flaky) \($stats.unexpected) \($stats.skipped)"
    ' "$result_file" 2>/dev/null || return 1
}

# Gates that must produce test results, as
# "gate name|artifact name prefix|result format".
test_result_sources=(
    "Platform .NET build, tests, and benchmark smoke|platform-test-results-|trx"
    "Gateway build, test, and benchmark smoke|gateway-test-results-|junit"
    "admin-web typecheck, build, and e2e|admin-web-playwright-|playwright"
    "user-web typecheck, build, and e2e|user-web-playwright-|playwright"
    "Full stack integration and live UI|stack-integration-results-|integration"
)

total_executed=0
total_passed=0
total_failed=0
total_skipped=0
evidence_artifacts="[]"

for source in "${test_result_sources[@]}"; do
    IFS='|' read -r gate_name artifact_prefix result_format <<<"$source"

    gate_executed=0
    gate_passed=0
    gate_failed=0
    gate_skipped=0
    gate_artifacts="[]"
    results_found=0

    # Newest non-expired artifact per name (re-runs reuse the same names).
    mapfile -t matching_artifacts < <(jq -r --arg prefix "$artifact_prefix" '
        [.[] | select(.expired == false) | select(.name | startswith($prefix))]
        | sort_by(.id) | group_by(.name) | map(.[-1]) | .[]
        | "\(.id)\t\(.name)"
    ' <<<"$artifacts_json")

    for artifact_record in "${matching_artifacts[@]}"; do
        artifact_id="${artifact_record%%$'\t'*}"
        artifact_name="${artifact_record#*$'\t'}"
        artifact_dir="$artifacts_dir/$artifact_name"
        mkdir -p "$artifact_dir"
        GH_TOKEN="$token" gh api \
            "$api_url/repos/$GITHUB_REPOSITORY/actions/artifacts/$artifact_id/zip" \
            > "$artifact_dir/artifact.zip" ||
            fail "could not download test result artifact: $artifact_name"
        unzip -q -o "$artifact_dir/artifact.zip" -d "$artifact_dir" ||
            fail "could not extract test result artifact: $artifact_name"
        rm -f "$artifact_dir/artifact.zip"

        # Content digest over the extracted files, independent of zip packing.
        artifact_digest="$(
            cd "$artifact_dir"
            find . -type f -print0 | sort -z | xargs -0 sha256sum |
                sha256sum | awk '{print $1}'
        )"
        gate_artifacts="$(jq --arg name "$artifact_name" \
            --arg digest "$artifact_digest" \
            '. + [{name: $name, digest: $digest}]' <<<"$gate_artifacts")"

        case "$result_format" in
            trx)
                while IFS= read -r result_file; do
                    counters="$(parse_trx_counters "$result_file")" ||
                        fail "TRX file has no ResultSummary/Counters: $result_file"
                    read -r executed passed failed skipped <<<"$counters"
                    results_found=1
                    gate_executed=$((gate_executed + executed))
                    gate_passed=$((gate_passed + passed))
                    gate_failed=$((gate_failed + failed))
                    gate_skipped=$((gate_skipped + skipped))
                done < <(find "$artifact_dir" -type f -name '*.trx' | sort)
                ;;
            junit)
                while IFS= read -r result_file; do
                    counters="$(parse_junit_results "$result_file")" ||
                        fail "JUnit XML file has no testsuite totals: $result_file"
                    read -r executed passed failed skipped <<<"$counters"
                    results_found=1
                    gate_executed=$((gate_executed + executed))
                    gate_passed=$((gate_passed + passed))
                    gate_failed=$((gate_failed + failed))
                    gate_skipped=$((gate_skipped + skipped))
                done < <(find "$artifact_dir" -type f -name '*.xml' | sort)
                ;;
            playwright)
                while IFS= read -r result_file; do
                    stats="$(parse_playwright_stats "$result_file")" ||
                        fail "Playwright JSON has no stats object: $result_file"
                    read -r executed passed failed skipped <<<"$stats"
                    results_found=1
                    gate_executed=$((gate_executed + executed))
                    gate_passed=$((gate_passed + passed))
                    gate_failed=$((gate_failed + failed))
                    gate_skipped=$((gate_skipped + skipped))
                done < <(find "$artifact_dir" -type f -name 'results.json' | sort)
                ;;
            integration)
                if [[ -f "$artifact_dir/integration-assertions.log" ]]; then
                    results_found=1
                fi
                while IFS= read -r result_file; do
                    stats="$(parse_playwright_stats "$result_file")" ||
                        fail "Playwright JSON has no stats object: $result_file"
                    read -r executed passed failed skipped <<<"$stats"
                    results_found=1
                    gate_executed=$((gate_executed + executed))
                    gate_passed=$((gate_passed + passed))
                    gate_failed=$((gate_failed + failed))
                    gate_skipped=$((gate_skipped + skipped))
                done < <(find "$artifact_dir" -type f -name 'results.json' | sort)
                ;;
        esac
    done

    if (( results_found == 0 )); then
        fail "release gate must produce test results but none were found: $gate_name"
    fi

    gate_tests="$(jq -cn \
        --argjson executed "$gate_executed" \
        --argjson passed "$gate_passed" \
        --argjson failed "$gate_failed" \
        --argjson skipped "$gate_skipped" \
        '{executed: $executed, passed: $passed, failed: $failed, skipped: $skipped}')"
    gates_entries="$(jq --arg name "$gate_name" \
        --argjson tests "$gate_tests" \
        --argjson artifacts "$gate_artifacts" '
        map(if .name == $name
            then . + {tests: $tests, artifacts: $artifacts}
            else . end)
    ' <<<"$gates_entries")"
    evidence_artifacts="$(jq --argjson artifacts "$gate_artifacts" \
        '. + $artifacts' <<<"$evidence_artifacts")"
    total_executed=$((total_executed + gate_executed))
    total_passed=$((total_passed + gate_passed))
    total_failed=$((total_failed + gate_failed))
    total_skipped=$((total_skipped + gate_skipped))
done

tests_totals="$(jq -cn \
    --argjson executed "$total_executed" \
    --argjson passed "$total_passed" \
    --argjson failed "$total_failed" \
    --argjson skipped "$total_skipped" \
    '{executed: $executed, passed: $passed, failed: $failed, skipped: $skipped}')"

jq -n \
    --arg repository "$GITHUB_REPOSITORY" \
    --arg run_id "$GITHUB_RUN_ID" \
    --arg run_attempt "$GITHUB_RUN_ATTEMPT" \
    --argjson gates "$gates_entries" \
    --argjson tests "$tests_totals" \
    --argjson skipped_jobs "$skipped_jobs_json" \
    --argjson artifacts "$evidence_artifacts" \
    '{
        status: "passed",
        repository: $repository,
        run_id: $run_id,
        run_attempt: $run_attempt,
        gates: $gates,
        tests: $tests,
        skipped_jobs: $skipped_jobs,
        artifacts: $artifacts
    }' > "$output_path"

gate_count="$(jq '.gates | length' "$output_path")"
echo "collected $gate_count successful gate conclusions into $output_path"
echo "test totals: $total_executed executed, $total_passed passed, $total_failed failed, $total_skipped skipped"
