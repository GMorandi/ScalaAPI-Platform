#!/usr/bin/env bash
set -euo pipefail

repo_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
contract_dir="$repo_root/contracts/capnp"
checked_in_dir="${GENERATED_CONTRACT_DIR:-$repo_root/src/Platform.Host/Generated}"
compiler="${CAPNP_COMPILER:-capnp}"
expected_compiler_version="Cap'n Proto version 1.0.2"

if [[ "$compiler" == */* ]]; then
    if [[ ! -x "$compiler" ]]; then
        echo "CAPNP_COMPILER is not executable: $compiler" >&2
        exit 2
    fi
elif ! command -v "$compiler" >/dev/null 2>&1; then
    echo "Cap'n Proto compiler not found; set CAPNP_COMPILER to version 1.0.2" >&2
    exit 2
fi

actual_compiler_version="$($compiler --version)"
if [[ "$actual_compiler_version" != "$expected_compiler_version" ]]; then
    echo "Expected '$expected_compiler_version', got '$actual_compiler_version'" >&2
    exit 2
fi

dotnet tool restore --tool-manifest "$repo_root/.config/dotnet-tools.json" >/dev/null

generated_dir="$(mktemp -d)"
cleanup() {
    find "$generated_dir" -type f -delete
    rmdir "$generated_dir"
}
trap cleanup EXIT

"$compiler" compile --src-prefix="$contract_dir" \
    -o"$repo_root/scripts/capnpc-csharp:$generated_dir" \
    "$contract_dir/types.capnp" \
    "$contract_dir/dispatch.capnp" \
    "$contract_dir/invalidation.capnp"

status=0
for name in dispatch.capnp.cs invalidation.capnp.cs types.capnp.cs; do
    expected="$checked_in_dir/$name"
    actual="$generated_dir/$name"
    if [[ ! -f "$expected" ]]; then
        echo "Missing checked-in generated contract: $expected" >&2
        status=1
        continue
    fi
    if ! cmp -s "$expected" "$actual"; then
        echo "Generated contract drift: $name" >&2
        diff -u "$expected" "$actual" >&2 || true
        status=1
    fi
done

if (( status != 0 )); then
    echo "Regenerate the C# contracts with the pinned compiler and tool manifest" >&2
    exit "$status"
fi

echo "Generated C# contracts match Cap'n Proto 1.0.2 / capnpc-csharp 1.3.118"
