#!/usr/bin/env bash
set -euo pipefail
export LC_ALL=C

usage() {
    cat <<'EOF'
Usage: scripts/generate-release-evidence.sh \
  --version vX.Y.Z --release-manifest manifest.json \
  --images-dir image-metadata --gates-json gates.json \
  --output release-evidence.json
EOF
}

fail() {
    echo "release evidence generation failed: $*" >&2
    exit 1
}

version=""
release_manifest=""
images_dir=""
gates_json=""
output_path=""
while (( $# > 0 )); do
    case "$1" in
        --version|--release-manifest|--images-dir|--gates-json|--output)
            (( $# >= 2 )) || fail "missing value for $1"
            case "$1" in
                --version) version="$2" ;;
                --release-manifest) release_manifest="$2" ;;
                --images-dir) images_dir="$2" ;;
                --gates-json) gates_json="$2" ;;
                --output) output_path="$2" ;;
            esac
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

[[ -n "$version" && -n "$release_manifest" && -n "$images_dir" && -n "$gates_json" && -n "$output_path" ]] || {
    usage >&2
    exit 2
}
[[ "$version" =~ ^v[0-9]+\.[0-9]+\.[0-9]+(-[0-9A-Za-z][0-9A-Za-z.-]*)?$ ]] ||
    fail "version is not a supported SemVer release: $version"
[[ -f "$release_manifest" ]] || fail "release manifest not found: $release_manifest"
[[ -d "$images_dir" ]] || fail "image metadata directory not found: $images_dir"
[[ -f "$gates_json" ]] || fail "gate results not found: $gates_json"
command -v jq >/dev/null 2>&1 || fail "required command not found: jq"
command -v sha256sum >/dev/null 2>&1 || fail "required command not found: sha256sum"

jq -e '
    .status == "passed" and
    (.run_id | test("^[0-9]+$")) and
    (.run_attempt | test("^[0-9]+$")) and
    (.gates | type == "array" and length > 0) and
    (all(.gates[]; .conclusion == "success")) and
    (all(.gates[]; .tests == null or (
        (.tests.executed | type == "number" and . >= 0) and
        (.tests.passed | type == "number" and . >= 0) and
        (.tests.failed | type == "number" and . >= 0) and
        (.tests.skipped | type == "number" and . >= 0)
    ))) and
    (.tests | type == "object") and
    (.tests.executed | type == "number" and . >= 0) and
    (.tests.passed | type == "number" and . >= 0) and
    (.tests.failed | type == "number" and . >= 0) and
    (.tests.skipped | type == "number" and . >= 0) and
    (.skipped_jobs | type == "array") and
    (all(.skipped_jobs[]; type == "string")) and
    (.artifacts | type == "array" and length > 0) and
    (all(.artifacts[]; (.name | type == "string") and (.digest | test("^[0-9a-f]{64}$"))))
' "$gates_json" >/dev/null ||
    fail "gate results do not demonstrate fully green release gates: $gates_json"

jq -e '
    .manifest_version == 3 and
    (.repository.commit | test("^[0-9a-f]{40}$")) and
    (.components.platform.commit | test("^[0-9a-f]{40}$")) and
    (.components.gateway.commit | test("^[0-9a-f]{40}$")) and
    (.contract.digest | test("^[0-9a-f]{64}$")) and
    (.migrations.digest | test("^[0-9a-f]{64}$")) and
    (.migrations.double_run == "passed") and
    (.generated_bindings.digest | test("^[0-9a-f]{64}$")) and
    (.deferred_evidence | type == "array" and length == 2) and
    (all(.deferred_evidence[]; (.item | type == "string") and (.reason | type == "string"))) and
    (.providers | type == "array" and length == 4) and
    (all(.providers[]; .verification_level == "mock"))
' "$release_manifest" >/dev/null || fail "release manifest has an invalid schema"

mapfile -t image_files < <(find "$images_dir" -maxdepth 1 -type f -name '*.json' -print | sort)
(( ${#image_files[@]} == 5 )) ||
    fail "expected metadata for exactly five images, found ${#image_files[@]}"

expected_repositories_file="$(mktemp)"
actual_repositories_file="$(mktemp)"
images_array_file="$(mktemp)"
cleanup() {
    rm -f "$expected_repositories_file" "$actual_repositories_file" "$images_array_file"
}
trap cleanup EXIT

cat > "$expected_repositories_file" <<'EOF'
ghcr.io/gmorandi/scalaapi-platform/admin-api
ghcr.io/gmorandi/scalaapi-platform/gateway
ghcr.io/gmorandi/scalaapi-platform/migrator
ghcr.io/gmorandi/scalaapi-platform/platform-silo
ghcr.io/gmorandi/scalaapi-platform/provider-mock
EOF

repository_sha="$(jq -r '.repository.commit' "$release_manifest")"
contract_digest="$(jq -r '.contract.digest' "$release_manifest")"
migration_digest="$(jq -r '.migrations.digest' "$release_manifest")"

for image_file in "${image_files[@]}"; do
    jq -e \
        --arg version "$version" \
        --arg repository_sha "$repository_sha" \
        --arg contract_digest "$contract_digest" \
        --arg migration_digest "$migration_digest" '
        (.repository | type == "string") and
        (.tag == $version) and
        (.reference == (.repository + ":" + $version)) and
        (.digest | test("^sha256:[0-9a-f]{64}$")) and
        (.component_commit == $repository_sha) and
        (.contract_digest == $contract_digest) and
        (.migration_digest == $migration_digest)
    ' "$image_file" >/dev/null || fail "invalid image metadata: $image_file"

    component="$(jq -r '.component' "$image_file")"
    component_commit="$(jq -r '.component_commit' "$image_file")"
    expected_component_commit="$(jq -r ".components.${component}.commit" "$release_manifest")"
    [[ "$component_commit" == "$expected_component_commit" ]] ||
        fail "component commit mismatch in $image_file"
done

jq -r '.repository' "${image_files[@]}" | sort > "$actual_repositories_file"
if ! diff -u "$expected_repositories_file" "$actual_repositories_file"; then
    fail "image metadata does not contain the required release image set"
fi

jq -s 'sort_by(.repository)' "${image_files[@]}" > "$images_array_file"
release_manifest_digest="$(sha256sum "$release_manifest" | awk '{print $1}')"
mkdir -p "$(dirname "$output_path")"

jq -n \
    --arg version "$version" \
    --arg release_manifest_digest "$release_manifest_digest" \
    --arg workflow_repository "${GITHUB_REPOSITORY:-unknown}" \
    --arg workflow_run_id "${GITHUB_RUN_ID:-unknown}" \
    --arg workflow_run_attempt "${GITHUB_RUN_ATTEMPT:-unknown}" \
    --slurpfile manifest "$release_manifest" \
    --slurpfile gates "$gates_json" \
    --slurpfile images "$images_array_file" '
    {
        evidence_version: 3,
        release: {
            tag: $version,
            repository_sha: $manifest[0].repository.commit,
            gateway_provenance: $manifest[0].components.gateway.provenance
        },
        release_manifest_digest: $release_manifest_digest,
        contract: $manifest[0].contract,
        generated_bindings: $manifest[0].generated_bindings,
        migration_manifest: $manifest[0].migrations,
        deferred_evidence: $manifest[0].deferred_evidence,
        providers: $manifest[0].providers,
        verification: {
            status: $gates[0].status,
            run_id: $gates[0].run_id,
            run_attempt: $gates[0].run_attempt,
            gates: $gates[0].gates,
            tests: $gates[0].tests,
            skipped: $gates[0].skipped_jobs,
            artifacts: $gates[0].artifacts
        },
        images: $images[0],
        workflow: {
            repository: $workflow_repository,
            run_id: $workflow_run_id,
            run_attempt: $workflow_run_attempt
        }
    }' > "$output_path"

echo "wrote release evidence: $output_path"
