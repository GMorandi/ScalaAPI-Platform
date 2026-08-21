#!/usr/bin/env bash
# Start the ScalaAPI stack with Docker or Podman (auto-detected).
#
# Usage:
#   start.sh [--env-file FILE] [--build|--no-build]
#
# Environment variables are read from --env-file (default: dev.env next to the
# Compose file) when it exists, otherwise from the exported environment or a
# .env file next to the Compose file; without any of these, the script lists
# the missing required variables and stops. The default builds every component
# from source; pass --no-build to deploy pinned release images (the *_IMAGE
# variables in the env file) instead. Set CONTAINER_CLI=docker|podman to
# override runtime detection.

# Re-exec with bash when invoked through another shell (e.g. sh start.sh).
if [ -z "${BASH_VERSION:-}" ]; then
    exec bash "$0" "$@"
fi
set -Eeuo pipefail

stack_dir="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
compose_file="$stack_dir/docker-compose.yml"
env_file="$stack_dir/dev.env"
build=1

while (($#)); do
    case "$1" in
        --env-file)
            env_file="${2:?--env-file requires a path}"
            shift 2
            ;;
        --env-file=*) env_file="${1#*=}"; shift ;;
        --build) build=1; shift ;;
        --no-build) build=0; shift ;;
        -h|--help) sed -n '2,13p' "$0"; exit 0 ;;
        *) echo "unknown argument: $1" >&2; exit 2 ;;
    esac
done

container_cli="${CONTAINER_CLI:-}"
if [[ -z "$container_cli" ]]; then
    if command -v docker >/dev/null 2>&1 && docker compose version >/dev/null 2>&1; then
        container_cli=docker
    elif command -v podman >/dev/null 2>&1 && podman compose version >/dev/null 2>&1; then
        container_cli=podman
    else
        echo "Docker Compose or Podman Compose is required" >&2
        exit 2
    fi
fi
if ! command -v "$container_cli" >/dev/null 2>&1; then
    echo "Container CLI '$container_cli' was not found" >&2
    exit 2
fi

compose=("$container_cli" compose -f "$compose_file")
if [[ -f "$env_file" ]]; then
    compose+=(--env-file "$env_file")
elif [[ "$env_file" != "$stack_dir/dev.env" ]]; then
    echo "Environment file not found: $env_file" >&2
    exit 2
elif [[ ! -f "$stack_dir/.env" ]]; then
    # No env file at all: fail fast unless every required variable is exported.
    missing=()
    while IFS= read -r name; do
        [[ -n "${!name:-}" ]] || missing+=("$name")
    done < <(grep -oE '\$\{[A-Za-z_][A-Za-z0-9_]*:\?[^}]*\}' "$compose_file" \
        | sed -E 's/^\$\{([A-Za-z_][A-Za-z0-9_]*):\?[^}]*\}$/\1/' | sort -u)
    if ((${#missing[@]})); then
        echo "No environment file at $env_file and these required variables are not exported:" >&2
        printf '  %s\n' "${missing[@]}" >&2
        echo "Copy $stack_dir/.env.example to $env_file and fill in every placeholder first." >&2
        exit 2
    fi
fi

args=(up -d)
if (( build )); then
    args+=(--build)
fi

echo "Starting the ScalaAPI stack with $container_cli compose"
"${compose[@]}" "${args[@]}"
