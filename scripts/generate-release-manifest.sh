#!/usr/bin/env bash
set -euo pipefail
export LC_ALL=C

fail() {
    echo "release manifest generation failed: $*" >&2
    exit 1
}

require_command() {
    command -v "$1" >/dev/null 2>&1 || fail "required command not found: $1"
}

if (( $# > 1 )); then
    echo "Usage: scripts/generate-release-manifest.sh [output.json]" >&2
    exit 2
fi

for command_name in git jq sha256sum find sort awk basename; do
    require_command "$command_name"
done

invocation_dir="$PWD"
repo_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
output_path="${1:-release-manifest.json}"
if [[ "$output_path" != /* ]]; then
    output_path="$invocation_dir/$output_path"
fi

# The single-repository gate: worktree must be clean and the canonical
# contract digest must verify.
git -C "$repo_root" diff --quiet || fail "repository worktree has uncommitted changes"
(cd "$repo_root/contracts/capnp" && sha256sum --check --strict SHA256SUMS) >/dev/null ||
    fail "canonical contract digest verification failed"

temp_dir="$(mktemp -d)"
cleanup() {
    rm -rf "$temp_dir"
}
trap cleanup EXIT

contract_rows="$temp_dir/contracts.ndjson"
migration_rows="$temp_dir/migrations.ndjson"
migration_digest_input="$temp_dir/migration-digests.txt"
: > "$contract_rows"
: > "$migration_rows"
: > "$migration_digest_input"

while read -r digest contract_file; do
    contract_file="${contract_file#\*}"
    jq -cn \
        --arg path "$contract_file" \
        --arg sha256 "$digest" \
        '{path: $path, sha256: $sha256}' >> "$contract_rows"
done < "$repo_root/contracts/capnp/SHA256SUMS"

generated_rows="$temp_dir/generated-bindings.ndjson"
generated_digest_input="$temp_dir/generated-binding-digests.txt"
: > "$generated_rows"
: > "$generated_digest_input"

# Digest the generated contract bindings in deterministic (sorted) order so the
# release evidence also pins what was compiled, not only the canonical schema.
while IFS= read -r generated_path; do
    generated_file_digest="$(sha256sum "$generated_path" | awk '{print $1}')"
    generated_logical_path="src/Platform.Host/Generated/$(basename "$generated_path")"
    jq -cn \
        --arg path "$generated_logical_path" \
        --arg sha256 "$generated_file_digest" \
        '{path: $path, sha256: $sha256}' >> "$generated_rows"
    printf '%s  %s\n' "$generated_file_digest" "$generated_logical_path" \
        >> "$generated_digest_input"
done < <(
    find "$repo_root/src/Platform.Host/Generated" \
        -maxdepth 1 -type f -name '*.capnp.cs' -print |
        sort
)

[[ -s "$generated_digest_input" ]] ||
    fail "no generated contract bindings found under src/Platform.Host/Generated"

add_migration() {
    local logical_path="$1"
    local source_path="$2"
    local source_relative_path="$3"
    local digest

    [[ -s "$source_path" ]] || fail "migration is missing or empty: $source_path"
    digest="$(sha256sum "$source_path" | awk '{print $1}')"
    jq -cn \
        --arg path "$logical_path" \
        --arg source "$source_relative_path" \
        --arg sha256 "$digest" \
        '{path: $path, source: $source, sha256: $sha256}' >> "$migration_rows"
    printf '%s  %s\n' "$digest" "$logical_path" >> "$migration_digest_input"
}

add_migration \
    "000-orleans.sql" \
    "$repo_root/deploy/orleans-postgres-schema.sql" \
    "deploy/orleans-postgres-schema.sql"

while IFS= read -r migration_path; do
    add_migration \
        "$(basename "$migration_path")" \
        "$migration_path" \
        "deploy/migrations/$(basename "$migration_path")"
done < <(
    find "$repo_root/deploy/migrations" \
        -maxdepth 1 -type f -name '*.sql' -print |
        sort
)

contract_array="$temp_dir/contracts.json"
migration_array="$temp_dir/migrations.json"
generated_array="$temp_dir/generated-bindings.json"
jq -s '.' "$contract_rows" > "$contract_array"
jq -s '.' "$migration_rows" > "$migration_array"
jq -s '.' "$generated_rows" > "$generated_array"

repository_sha="$(git -C "$repo_root" rev-parse HEAD)"
repository_url="$(git -C "$repo_root" remote get-url origin 2>/dev/null || printf '%s' unknown)"
contract_digest="$(
    sha256sum "$repo_root/contracts/capnp/SHA256SUMS" |
        awk '{print $1}'
)"
migration_digest="$(sha256sum "$migration_digest_input" | awk '{print $1}')"
migration_count="$(jq 'length' "$migration_array")"
latest_migration="$(jq -r '.[-1].path' "$migration_array")"
generated_bindings_digest="$(sha256sum "$generated_digest_input" | awk '{print $1}')"
# Result of the dotnet.yml empty-database migration double-run gate, passed
# through by the release workflow; "not_run" when produced outside a release.
migration_double_run="${MIGRATION_DOUBLE_RUN_RESULT:-not_run}"

mkdir -p "$(dirname "$output_path")"
temporary_output="$temp_dir/release-manifest.json"
jq -n \
    --arg repository_url "$repository_url" \
    --arg repository_sha "$repository_sha" \
    --arg contract_digest "$contract_digest" \
    --slurpfile contract_files "$contract_array" \
    --arg migration_digest "$migration_digest" \
    --arg latest_migration "$latest_migration" \
    --argjson migration_count "$migration_count" \
    --arg migration_double_run "$migration_double_run" \
    --slurpfile migrations "$migration_array" \
    --arg generated_bindings_digest "$generated_bindings_digest" \
    --slurpfile generated_bindings "$generated_array" \
    '{
        manifest_version: 3,
        repository: {
            url: $repository_url,
            commit: $repository_sha
        },
        components: {
            platform: {
                repository: $repository_url,
                path: ".",
                commit: $repository_sha
            },
            gateway: {
                repository: "https://github.com/GMorandi/ScalaAPI-GateWay.git",
                path: "gateway",
                commit: $repository_sha,
                provenance: "subtree import of GMorandi/ScalaAPI-GateWay master 3349d64; history preserved"
            }
        },
        contract: {
            algorithm: "sha256",
            digest: $contract_digest,
            canonical_path: "contracts/capnp",
            files: $contract_files[0]
        },
        generated_bindings: {
            algorithm: "sha256",
            digest: $generated_bindings_digest,
            path: "src/Platform.Host/Generated",
            files: $generated_bindings[0]
        },
        migrations: {
            algorithm: "sha256",
            digest: $migration_digest,
            latest: $latest_migration,
            count: $migration_count,
            double_run: $migration_double_run,
            files: $migrations[0]
        },
        deferred_evidence: [
            {
                item: "one-hour runtime evidence",
                reason: "deferred past v0.1.0, tracked as a documented deviation"
            },
            {
                item: "backup restore drill",
                reason: "deferred past v0.1.0, tracked as a documented deviation"
            }
        ],
        providers: [
            {provider: "openai", verification_level: "mock", live_acceptance: "deferred"},
            {provider: "anthropic", verification_level: "mock", live_acceptance: "deferred"},
            {provider: "gemini", verification_level: "mock", live_acceptance: "deferred"},
            {provider: "xai", verification_level: "mock", live_acceptance: "deferred"}
        ]
    }' > "$temporary_output"

mv "$temporary_output" "$output_path"
echo "wrote release manifest: $output_path"
