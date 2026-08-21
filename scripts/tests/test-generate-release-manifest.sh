#!/usr/bin/env bash
# Verifies generate-release-manifest.sh against a temporary fixture repository:
# generated-binding digest, migration double-run result, deferred_evidence and
# providers sections.
set -euo pipefail
export LC_ALL=C

tests_dir="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
repo_root="$(cd "$tests_dir/../.." && pwd)"
work_dir="$(mktemp -d)"
trap 'rm -rf "$work_dir"' EXIT

fixture_repo="$work_dir/repo"
mkdir -p "$fixture_repo/scripts" \
    "$fixture_repo/contracts/capnp" \
    "$fixture_repo/deploy/migrations" \
    "$fixture_repo/src/Platform.Host/Generated"

echo "module types" > "$fixture_repo/contracts/capnp/types.capnp"
(cd "$fixture_repo/contracts/capnp" && sha256sum types.capnp > SHA256SUMS)
echo "orleans schema" > "$fixture_repo/deploy/orleans-postgres-schema.sql"
echo "create table t(id int);" > "$fixture_repo/deploy/migrations/001-init.sql"
echo "// generated a" > "$fixture_repo/src/Platform.Host/Generated/a.capnp.cs"
echo "// generated b" > "$fixture_repo/src/Platform.Host/Generated/b.capnp.cs"
cp "$repo_root/scripts/generate-release-manifest.sh" "$fixture_repo/scripts/"

git -C "$fixture_repo" init -q
git -C "$fixture_repo" add -A
git -C "$fixture_repo" -c user.email=test@example.test -c user.name=test \
    commit -qm "fixture"

manifest="$work_dir/manifest.json"
(
    cd "$fixture_repo"
    MIGRATION_DOUBLE_RUN_RESULT=passed \
        scripts/generate-release-manifest.sh "$manifest" >/dev/null
)

assert_jq() {
    jq -e "$1" "$manifest" >/dev/null || {
        echo "assertion failed: $1" >&2
        jq '.' "$manifest" >&2
        exit 1
    }
}

# Generated-binding digest, computed independently over the sorted files.
expected_bindings_digest="$(
    cd "$fixture_repo"
    find src/Platform.Host/Generated -maxdepth 1 -type f -name '*.capnp.cs' |
        sort |
        while IFS= read -r generated_path; do
            printf '%s  %s\n' \
                "$(sha256sum "$generated_path" | awk '{print $1}')" \
                "$generated_path"
        done | sha256sum | awk '{print $1}'
)"

assert_jq '.manifest_version == 3'
assert_jq ".generated_bindings.digest == \"$expected_bindings_digest\""
assert_jq '.generated_bindings.files == [
    {path: "src/Platform.Host/Generated/a.capnp.cs", sha256: .generated_bindings.files[0].sha256},
    {path: "src/Platform.Host/Generated/b.capnp.cs", sha256: .generated_bindings.files[1].sha256}
]'
assert_jq '.migrations.double_run == "passed"'
assert_jq '[.deferred_evidence[].item] == ["one-hour runtime evidence", "backup restore drill"]'
assert_jq 'all(.deferred_evidence[]; .reason | contains("deferred past v0.1.0"))'
assert_jq '[.providers[].provider] == ["openai", "anthropic", "gemini", "xai"]'
assert_jq 'all(.providers[]; .verification_level == "mock" and .live_acceptance == "deferred")'

# Without the gate plumbing the double-run must be recorded as not_run.
manifest_no_gate="$work_dir/manifest-no-gate.json"
(
    cd "$fixture_repo"
    scripts/generate-release-manifest.sh "$manifest_no_gate" >/dev/null
)
jq -e '.migrations.double_run == "not_run"' "$manifest_no_gate" >/dev/null || {
    echo "assertion failed: double_run defaults to not_run" >&2
    exit 1
}
