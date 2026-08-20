#!/usr/bin/env bash
set -euo pipefail

repo_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
contract_dir="$repo_root/contracts/capnp"

(cd "$contract_dir" && sha256sum --check --strict SHA256SUMS)
