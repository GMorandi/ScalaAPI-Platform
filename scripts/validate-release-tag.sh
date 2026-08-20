#!/usr/bin/env bash
set -euo pipefail
export LC_ALL=C

fail() {
    echo "release tag validation failed: $*" >&2
    exit 1
}

if (( $# != 1 )); then
    echo "Usage: scripts/validate-release-tag.sh <vX.Y.Z[-prerelease]>" >&2
    exit 2
fi

version="$1"
[[ "$version" =~ ^v[0-9]+\.[0-9]+\.[0-9]+(-[0-9A-Za-z][0-9A-Za-z.-]*)?$ ]] ||
    fail "tag is not a supported SemVer release: $version"

repo_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
head_sha="$(git -C "$repo_root" rev-parse HEAD)"
tag_sha="$(git -C "$repo_root" rev-parse "refs/tags/$version^{commit}" 2>/dev/null)" ||
    fail "tag does not exist in the repository: $version"

[[ "$tag_sha" == "$head_sha" ]] ||
    fail "repository tag $version resolves to $tag_sha, not checked-out $head_sha"

if [[ -n "${GITHUB_REF_TYPE:-}" && "${GITHUB_REF_TYPE}" != "tag" ]]; then
    fail "release workflow is not running for a Git tag"
fi
if [[ -n "${GITHUB_REF_NAME:-}" && "${GITHUB_REF_NAME}" != "$version" ]]; then
    fail "workflow ref ${GITHUB_REF_NAME} does not match release version $version"
fi

echo "release tag $version identifies repository commit $head_sha"
