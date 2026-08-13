#!/usr/bin/env bash
set -euo pipefail

platform_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
gateway_root="${GATEWAY_ROOT:-$(cd "$platform_root/../gateway" && pwd)}"
sub2api_root="${SUB2API_ROOT:-$(cd "$platform_root/../sub2api" 2>/dev/null && pwd || echo "")}"

record_commits() {
    echo "=== Commit Baseline ==="
    echo "Platform: $(cd "$platform_root" && git rev-parse HEAD)"
    if [[ -d "$gateway_root/.git" ]]; then
        echo "Gateway:  $(cd "$gateway_root" && git rev-parse HEAD)"
    else
        echo "Gateway:  not available at $gateway_root" >&2
    fi
    if [[ -n "$sub2api_root" && -d "$sub2api_root/.git" ]]; then
        echo "Sub2API:  $(cd "$sub2api_root" && git rev-parse HEAD)"
    else
        echo "Sub2API:  reference not available"
    fi
    echo ""
}

require_cmd() {
    if ! command -v "$1" >/dev/null 2>&1; then
        echo "ERROR: required command '$1' not found ($2)" >&2
        return 1
    fi
}

step() {
    local name="$1"
    shift
    echo "=== $name ==="
    if "$@"; then
        echo "  -> OK"
    else
        echo "  -> FAILED: $name" >&2
        return 1
    fi
    echo ""
}

failures=0
run_or_record() {
    local name="$1"
    shift
    echo "=== $name ==="
    if "$@"; then
        echo "  -> OK"
    else
        echo "  -> FAILED: $name" >&2
        failures=$((failures + 1))
    fi
    echo ""
}

echo "========================================"
echo " ScalaAPI Platform Baseline Verification"
echo "========================================"
echo ""

record_commits

require_cmd dotnet ".NET 10 SDK required" || exit 2

echo "--- Step 1: Platform Release Build ---"
step "Platform Release build" \
    dotnet build "$platform_root/ScalaAPI.Platform.slnx" -c Release --no-incremental

echo "--- Step 2: Platform C# Tests ---"
if [[ -n "${GREENFIELD_SCHEMA_CONNECTION:-}" ]]; then
    run_or_record "Platform tests (with database)" \
        dotnet test "$platform_root/ScalaAPI.Platform.slnx" -c Release --no-build
else
    run_or_record "Platform tests (without database)" \
        dotnet test "$platform_root/ScalaAPI.Platform.slnx" -c Release --no-build
fi

echo "--- Step 3: Cap'n Proto Contract Digest ---"
run_or_record "Contract SHA256 digest" \
    bash "$platform_root/scripts/verify-contracts.sh"

echo "--- Step 4: Cap'n Proto Generated Code ---"
if command -v capnp >/dev/null 2>&1 || [[ -n "${CAPNP_COMPILER:-}" ]]; then
    run_or_record "Generated contract comparison" \
        bash "$platform_root/scripts/verify-generated-contracts.sh"
else
    echo "=== Generated contract comparison ==="
    echo "  -> SKIP: capnp compiler not found (set CAPNP_COMPILER)"
    echo ""
fi

echo "--- Step 5: Retired Dependency Scan ---"
if command -v rg >/dev/null 2>&1; then
    run_or_record "Retired dependency scan" \
        bash "$platform_root/scripts/verify-retired-dependencies.sh"
else
    echo "=== Retired dependency scan ==="
    echo "  -> SKIP: ripgrep (rg) not found"
    echo ""
fi

echo "--- Step 6: Gateway C++ Build and Tests ---"
if [[ -d "$gateway_root/build" ]] || [[ -d "$gateway_root/CMakeLists.txt" ]] || [[ -f "$gateway_root/CMakeLists.txt" ]]; then
    if command -v cmake >/dev/null 2>&1; then
        gateway_build_dir="$gateway_root/build"
        if [[ ! -d "$gateway_build_dir" ]]; then
            mkdir -p "$gateway_build_dir"
            run_or_record "Gateway CMake configure" \
                cmake -S "$gateway_root" -B "$gateway_build_dir" -DCMAKE_BUILD_TYPE=Release
        fi
        run_or_record "Gateway C++ build" \
            cmake --build "$gateway_build_dir" -j "$(nproc)"
        if [[ -f "$gateway_build_dir/CTestTestfile.cmake" ]] || command -v ctest >/dev/null 2>&1; then
            run_or_record "Gateway CTest" \
                ctest --test-dir "$gateway_build_dir" --output-on-failure
        else
            echo "=== Gateway CTest ==="
            echo "  -> SKIP: no CTest configuration found"
            echo ""
        fi
    else
        echo "=== Gateway C++ build ==="
        echo "  -> SKIP: cmake not found"
        echo ""
    fi
else
    echo "=== Gateway C++ build ==="
    echo "  -> SKIP: gateway source not found at $gateway_root"
    echo ""
fi

echo "--- Step 7: Web Frontend Build ---"
for web_dir in admin-web user-web; do
    web_path="$platform_root/$web_dir"
    if [[ -d "$web_path" ]] && [[ -f "$web_path/package.json" ]]; then
        if command -v npm >/dev/null 2>&1; then
            run_or_record "$web_dir build" \
                bash -c "cd '$web_path' && npm ci --silent && npm run build"
        else
            echo "=== $web_dir build ==="
            echo "  -> SKIP: npm not found"
            echo ""
        fi
    fi
done

echo "--- Step 8: Migration Double-Run ---"
if [[ -n "${ConnectionStrings__Postgres:-}" ]]; then
    run_or_record "Migration first run" \
        dotnet run --project "$platform_root/src/Db.Migrator" -- "$platform_root/deploy/migrations"
    run_or_record "Migration second run (idempotent)" \
        dotnet run --project "$platform_root/src/Db.Migrator" -- "$platform_root/deploy/migrations"
else
    echo "=== Migration double-run ==="
    echo "  -> SKIP: ConnectionStrings__Postgres not set"
    echo ""
fi

echo "========================================"
if (( failures > 0 )); then
    echo " BASELINE FAILED: $failures step(s) failed"
    echo "========================================"
    exit 1
else
    echo " BASELINE PASSED"
    echo "========================================"
    exit 0
fi
