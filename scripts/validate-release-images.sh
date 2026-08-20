#!/usr/bin/env bash
set -euo pipefail
export LC_ALL=C

usage() {
    cat <<'EOF'
Usage: scripts/validate-release-images.sh <vX.Y.Z[-prerelease]>

Refuse to publish a release tag that already exists in the image registry.
Every repository in the release image set must be absent for the given tag;
an indeterminate registry answer is a release blocker, not a pass.
EOF
}

fail() {
    echo "release image validation failed: $*" >&2
    exit 1
}

if (( $# != 1 )); then
    usage >&2
    exit 2
fi

version="$1"
[[ "$version" =~ ^v[0-9]+\.[0-9]+\.[0-9]+(-[0-9A-Za-z][0-9A-Za-z.-]*)?$ ]] ||
    fail "tag is not a supported SemVer release: $version"

command -v docker >/dev/null 2>&1 || fail "required command not found: docker"

repositories=(
    ghcr.io/gmorandi/scalaapi-platform/admin-api
    ghcr.io/gmorandi/scalaapi-platform/gateway
    ghcr.io/gmorandi/scalaapi-platform/migrator
    ghcr.io/gmorandi/scalaapi-platform/platform-silo
    ghcr.io/gmorandi/scalaapi-platform/provider-mock
)

for repository in "${repositories[@]}"; do
    reference="$repository:$version"
    if inspect_output="$(docker manifest inspect "$reference" 2>&1)"; then
        fail "tag already exists in the registry: $reference — republishing a \
release tag is not allowed"
    fi
    case "$inspect_output" in
        *"manifest unknown"*|*"no such manifest"*|*"not found"*|*"404"*)
            ;;
        *)
            fail "could not determine whether $reference already exists: \
$inspect_output"
            ;;
    esac
done

echo "release tag $version is absent from all ${#repositories[@]} release repositories"
