#!/usr/bin/env bash
set -euo pipefail

repo_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
cd "$repo_root"

pattern='debezium|migration_fence|migrationwritegate|microsoft\.garnet|redis|legacy[_-]|sub2api|subdata'
allowed_passkey_note='deploy/migrations/031-passkeys.sql:-- the only durable authenticator material. No Sub2API identity data is reused.'
scan_paths=(src deploy)
if (( $# > 0 )); then
    scan_paths=("$@")
fi
failed=0

while IFS= read -r match; do
    [[ -z "$match" ]] && continue
    if [[ "$match" == "$allowed_passkey_note" ]]; then
        continue
    fi
    echo "Retired dependency reference: $match" >&2
    failed=1
done < <(rg --no-heading --with-filename --no-line-number -i "$pattern" \
    "${scan_paths[@]}" --glob '!deploy/stack/README.md' || true)

if (( failed != 0 )); then
    exit 1
fi

echo "Retired dependency scan: OK"
