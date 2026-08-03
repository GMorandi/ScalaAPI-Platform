#!/usr/bin/env bash
set -euo pipefail

fstar_bin="${FSTAR:-fstar.exe}"
z3_bin="${Z3:-z3}"
out_dir="${FSTAR_OUT:-${TMPDIR:-/tmp}/apitf-fstar-out}"
cache_dir="${FSTAR_CACHE:-${TMPDIR:-/tmp}/apitf-fstar-cache}"

command -v "$fstar_bin" >/dev/null
command -v "$z3_bin" >/dev/null
mkdir -p "$out_dir" "$cache_dir"

"$fstar_bin" --odir "$out_dir" --cache_dir "$cache_dir" \
  "$(dirname "$0")/migration_fence.fst"
