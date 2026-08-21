#!/usr/bin/env bash
# Fixture-driven tests for the release tooling scripts. No real GitHub run is
# required: a fake `gh` (fakes/gh) serves canned API responses and the test
# result artifacts under fixtures/. Wired into the .NET Build gate as the
# "Test release tooling scripts" step; run locally with:
#
#   scripts/tests/run-tests.sh
set -euo pipefail
export LC_ALL=C

tests_dir="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"

for command_name in bash jq git zip unzip sha256sum; do
    command -v "$command_name" >/dev/null 2>&1 || {
        echo "missing required command: $command_name" >&2
        exit 1
    }
done

failed=0
for test_file in "$tests_dir"/test-*.sh; do
    echo "== $(basename "$test_file")"
    if bash "$test_file"; then
        echo "PASS: $(basename "$test_file")"
    else
        echo "FAIL: $(basename "$test_file")" >&2
        failed=1
    fi
done

if (( failed != 0 )); then
    echo "some script tests failed" >&2
    exit 1
fi
echo "all script tests passed"
