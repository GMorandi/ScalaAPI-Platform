#!/usr/bin/env bash
# Verifies collect-gate-results.sh end to end against fake `gh` API responses
# and fixture test result artifacts (TRX, ctest JUnit XML, Playwright JSON,
# integration assertion log).
set -euo pipefail
export LC_ALL=C

tests_dir="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
repo_root="$(cd "$tests_dir/../.." && pwd)"
work_dir="$(mktemp -d)"
trap 'rm -rf "$work_dir"' EXIT

export FAKE_GH_FIXTURES="$tests_dir/fixtures"
export PATH="$tests_dir/fakes:$PATH"
export GITHUB_REPOSITORY="example/scalaapi-platform"
export GITHUB_RUN_ID="12345"
export GITHUB_RUN_ATTEMPT="1"
export GITHUB_TOKEN="fake-token"

assert_jq() {
    jq -e "$1" "$work_dir/gates.json" >/dev/null || {
        echo "assertion failed: $1" >&2
        jq '.' "$work_dir/gates.json" >&2
        exit 1
    }
}

# Happy path: all gates green, every test result artifact present.
"$repo_root/scripts/collect-gate-results.sh" --output "$work_dir/gates.json" >/dev/null

assert_jq '.status == "passed"'
assert_jq '(.gates | length) == 10'
assert_jq '.skipped_jobs == ["Release dry-run notice"]'
assert_jq '.tests == {executed: 28, passed: 23, failed: 5, skipped: 5}'
assert_jq '(.artifacts | length) == 5'
assert_jq 'all(.artifacts[]; .digest | test("^[0-9a-f]{64}$"))'
assert_jq '[.artifacts[].name] | sort == [
    "admin-web-playwright-def123",
    "gateway-test-results-def123",
    "platform-test-results-def123",
    "stack-integration-results-def123",
    "user-web-playwright-def123"
]'
assert_jq '.gates[] | select(.name == "Platform .NET build, tests, and benchmark smoke")
    | .tests == {executed: 10, passed: 7, failed: 3, skipped: 2}'
assert_jq '.gates[] | select(.name == "Gateway build, test, and benchmark smoke")
    | .tests == {executed: 5, passed: 4, failed: 1, skipped: 1}'
assert_jq '.gates[] | select(.name == "admin-web typecheck, build, and e2e")
    | .tests == {executed: 7, passed: 6, failed: 1, skipped: 1}'
assert_jq '.gates[] | select(.name == "user-web typecheck, build, and e2e")
    | .tests == {executed: 4, passed: 4, failed: 0, skipped: 0}'
assert_jq '.gates[] | select(.name == "Full stack integration and live UI")
    | .tests == {executed: 2, passed: 2, failed: 0, skipped: 1}'
assert_jq '.gates[] | select(.name == "Build and scan gateway image")
    | has("tests") | not'

# A required test result artifact missing must fail the collection.
if FAKE_GH_ARTIFACTS_FILE="artifacts-missing.json" \
    "$repo_root/scripts/collect-gate-results.sh" \
    --output "$work_dir/gates-missing.json" 2>"$work_dir/missing.err"; then
    echo "expected failure when a gate produces no test results" >&2
    exit 1
fi
grep -q "must produce test results" "$work_dir/missing.err" || {
    echo "unexpected failure output:" >&2
    cat "$work_dir/missing.err" >&2
    exit 1
}
