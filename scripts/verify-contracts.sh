#!/usr/bin/env bash
set -euo pipefail

repo_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
contract_dir="$repo_root/contracts/capnp"

(cd "$contract_dir" && sha256sum --check SHA256SUMS)

if [[ $# -eq 0 ]]; then
    exit 0
fi

gateway_root="$(cd "$1" && pwd)"
for schema in dispatch.capnp invalidation.capnp types.capnp; do
    cmp "$contract_dir/$schema" "$gateway_root/proto/$schema"
done

(cd "$gateway_root/proto" && sha256sum --check SHA256SUMS)
