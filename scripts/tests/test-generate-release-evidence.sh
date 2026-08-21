#!/usr/bin/env bash
# Verifies generate-release-evidence.sh against a fixture release manifest,
# fixture image metadata, and a fixture gates.json carrying real test totals,
# the skipped-job list, and evidence artifact references.
set -euo pipefail
export LC_ALL=C

tests_dir="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
repo_root="$(cd "$tests_dir/../.." && pwd)"
fixtures="$tests_dir/fixtures/evidence"
work_dir="$(mktemp -d)"
trap 'rm -rf "$work_dir"' EXIT

commit="1111111111111111111111111111111111111111"
contract_digest="cccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccc"
migration_digest="eeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeee"

images_dir="$work_dir/image-metadata"
mkdir -p "$images_dir"
write_image_metadata() {
    local key="$1" component="$2" digest_hex="$3"
    local repository="ghcr.io/gmorandi/scalaapi-platform/$key"
    jq -n \
        --arg repository "$repository" \
        --arg digest "sha256:$digest_hex" \
        --arg component "$component" \
        --arg component_commit "$commit" \
        --arg contract_digest "$contract_digest" \
        --arg migration_digest "$migration_digest" \
        '{
            repository: $repository,
            tag: "v9.9.9",
            reference: ($repository + ":v9.9.9"),
            digest: $digest,
            component: $component,
            component_commit: $component_commit,
            contract_digest: $contract_digest,
            migration_digest: $migration_digest
        }' > "$images_dir/$key.json"
}
write_image_metadata admin-api platform \
    "1111111111111111111111111111111111111111111111111111111111111111"
write_image_metadata gateway gateway \
    "2222222222222222222222222222222222222222222222222222222222222222"
write_image_metadata migrator platform \
    "3333333333333333333333333333333333333333333333333333333333333333"
write_image_metadata platform-silo platform \
    "4444444444444444444444444444444444444444444444444444444444444444"
write_image_metadata provider-mock platform \
    "5555555555555555555555555555555555555555555555555555555555555555"

run_evidence() {
    "$repo_root/scripts/generate-release-evidence.sh" \
        --version v9.9.9 \
        --release-manifest "$fixtures/manifest.json" \
        --images-dir "$images_dir" \
        --gates-json "$1" \
        --output "$2"
}

# Happy path.
run_evidence "$fixtures/gates.json" "$work_dir/evidence.json"

assert_evidence() {
    jq -e "$1" "$work_dir/evidence.json" >/dev/null || {
        echo "assertion failed: $1" >&2
        jq '.' "$work_dir/evidence.json" >&2
        exit 1
    }
}
assert_evidence '.evidence_version == 3'
assert_evidence '.release.tag == "v9.9.9"'
assert_evidence '.release_manifest_digest | test("^[0-9a-f]{64}$")'
assert_evidence '.generated_bindings.digest == "dddddddddddddddddddddddddddddddddddddddddddddddddddddddddddddddd"'
assert_evidence '.verification.skipped == ["Release dry-run notice"]'
assert_evidence '.verification.tests == {executed: 28, passed: 23, failed: 5, skipped: 5}'
assert_evidence '(.verification.artifacts | length) == 2'
assert_evidence 'all(.verification.artifacts[]; .digest | test("^[0-9a-f]{64}$"))'
assert_evidence '.verification.gates[0].tests == {executed: 10, passed: 7, failed: 3, skipped: 2}'
assert_evidence '(.deferred_evidence | length) == 2'
assert_evidence '[.deferred_evidence[].item] == ["one-hour runtime evidence", "backup restore drill"]'
assert_evidence '(.providers | length) == 4 and all(.providers[]; .verification_level == "mock")'

# gates.json without the new fields must be rejected.
jq 'del(.skipped_jobs)' "$fixtures/gates.json" > "$work_dir/gates-no-skipped.json"
if run_evidence "$work_dir/gates-no-skipped.json" "$work_dir/evidence-bad.json" 2>/dev/null; then
    echo "expected failure for gates.json without skipped_jobs" >&2
    exit 1
fi

# A manifest recording a non-passed migration double-run must be rejected.
jq '.migrations.double_run = "not_run"' "$fixtures/manifest.json" \
    > "$work_dir/manifest-not-run.json"
if "$repo_root/scripts/generate-release-evidence.sh" \
    --version v9.9.9 \
    --release-manifest "$work_dir/manifest-not-run.json" \
    --images-dir "$images_dir" \
    --gates-json "$fixtures/gates.json" \
    --output "$work_dir/evidence-bad2.json" 2>/dev/null; then
    echo "expected failure for a manifest with double_run != passed" >&2
    exit 1
fi
