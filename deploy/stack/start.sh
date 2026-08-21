#!/usr/bin/env bash
# Start the ScalaAPI stack with Docker or Podman (auto-detected).
#
# Usage:
#   start.sh [--env-file FILE] [--build|--no-build] [--demo] [--release [TAG]]
#
# Environment variables are read from --env-file (default: dev.env next to the
# Compose file) when it exists, otherwise from the exported environment or a
# .env file next to the Compose file. Before invoking Compose, every variable
# the Compose file marks as required must have a real value: not unset, not
# empty, and not an .env.example placeholder. Pass --demo to generate any such
# value with openssl, print it to the terminal, and save it into the env file
# for reuse on later runs. The default builds every component from source;
# pass --no-build to deploy pinned release images (the *_IMAGE variables in
# the env file) instead, or pass --release to pull the published ghcr.io
# images directly (without TAG the newest stable tag is looked up from the
# registry). Set CONTAINER_CLI=docker|podman to override runtime detection.

# Re-exec with bash when invoked through another shell (e.g. sh start.sh).
if [ -z "${BASH_VERSION:-}" ]; then
    exec bash "$0" "$@"
fi
set -Eeuo pipefail

stack_dir="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
compose_file="$stack_dir/docker-compose.yml"
env_file="$stack_dir/dev.env"
build=1
demo=0
release_tag=""

while (($#)); do
    case "$1" in
        --env-file)
            env_file="${2:?--env-file requires a path}"
            shift 2
            ;;
        --env-file=*) env_file="${1#*=}"; shift ;;
        --build) build=1; shift ;;
        --no-build) build=0; shift ;;
        --demo) demo=1; shift ;;
        --release)
            if [[ -n "${2:-}" && "${2:-}" != -* ]]; then
                release_tag="$2"; shift 2
            else
                release_tag=auto; shift
            fi
            build=0
            ;;
        --release=*) release_tag="${1#*=}"; build=0; shift ;;
        -h|--help) sed -n '2,18p' "$0"; exit 0 ;;
        *) echo "unknown argument: $1" >&2; exit 2 ;;
    esac
done

if (( demo )) && ! command -v openssl >/dev/null 2>&1; then
    echo "Demo mode requires openssl to generate secrets" >&2
    exit 2
fi

# Resolve a bare --release to the newest stable tag published on ghcr.io.
if [[ "$release_tag" == "auto" ]]; then
    if ! command -v curl >/dev/null 2>&1; then
        echo "--release without a tag needs curl to look up the newest tag; pass it explicitly, e.g. --release v0.1.1" >&2
        exit 2
    fi
    registry_token="$(curl -fsSL --max-time 15 \
        "https://ghcr.io/token?scope=repository:gmorandi/scalaapi-platform/gateway:pull" \
        | sed -n 's/.*"token"[[:space:]]*:[[:space:]]*"\([^"]*\)".*/\1/p')" || true
    if [[ -n "$registry_token" ]]; then
        release_tag="$(curl -fsSL --max-time 15 -H "Authorization: Bearer $registry_token" \
            "https://ghcr.io/v2/gmorandi/scalaapi-platform/gateway/tags/list" \
            | grep -oE '"v[0-9]+\.[0-9]+\.[0-9]+"' | tr -d '"' | sort -V | tail -n 1)" || true
    fi
    if [[ -z "$release_tag" || "$release_tag" == "auto" ]]; then
        echo "Could not resolve the newest release tag; pass it explicitly, e.g. --release v0.1.1" >&2
        exit 2
    fi
    echo "Newest published release: $release_tag"
fi

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
check_file=""
if [[ -f "$env_file" ]]; then
    compose+=(--env-file "$env_file")
    check_file="$env_file"
elif [[ "$env_file" != "$stack_dir/dev.env" ]]; then
    if (( ! demo )); then
        echo "Environment file not found: $env_file" >&2
        exit 2
    fi
    # Demo mode creates the explicit env file with the generated secrets below.
elif [[ -f "$stack_dir/.env" ]]; then
    check_file="$stack_dir/.env"
fi

# Fail fast unless every variable the Compose file marks as required has a
# real value in the exported environment or the env file compose will read.
# Demo mode generates such values instead and exports them for the Compose run.
missing=()
placeholders=()
invalid=()
generated=()
while IFS= read -r name; do
    value="${!name:-}"
    if [[ -z "$value" && -n "$check_file" ]]; then
        value="$(sed -nE "s/^[[:space:]]*(export[[:space:]]+)?${name}=(.*)$/\2/p" "$check_file" | tail -n 1)"
        value="${value%\"}"; value="${value#\"}"
        value="${value%\'}"; value="${value#\'}"
    fi
    if (( demo )) && { [[ -z "$value" ]] || [[ "$value" == *replace-with-* ]]; }; then
        case "$name" in
            ADMIN_USERNAME) value=admin ;;
            SECURITY_MASTER_KEY) value="$(openssl rand -base64 32)" ;;
            *) value="$(openssl rand -hex 32)" ;;
        esac
        export "$name=$value"
        generated+=("$name=$value")
        continue
    fi
    if [[ -z "$value" ]]; then
        missing+=("$name")
    elif [[ "$value" == *replace-with-* ]]; then
        placeholders+=("$name")
    elif [[ "$name" == "SECURITY_MASTER_KEY" ]] \
        && [[ "$(printf '%s' "$value" | base64 -d 2>/dev/null | wc -c)" != "32" ]]; then
        invalid+=("$name (must be Base64 for exactly 32 bytes)")
    fi
done < <(grep -oE '\$\{[A-Za-z_][A-Za-z0-9_]*:\?[^}]*\}' "$compose_file" \
    | sed -E 's/^\$\{([A-Za-z_][A-Za-z0-9_]*):\?[^}]*\}$/\1/' | sort -u)

if ((${#missing[@]} + ${#placeholders[@]} + ${#invalid[@]})); then
    if [[ -n "$check_file" ]]; then
        echo "The environment ($check_file plus exported variables) is not ready:" >&2
    else
        echo "No environment file at $env_file and the exported environment is not ready:" >&2
    fi
    if ((${#missing[@]})); then
        echo "  missing values:" >&2
        printf '    %s\n' "${missing[@]}" >&2
    fi
    if ((${#placeholders[@]})); then
        echo "  still set to .env.example placeholders:" >&2
        printf '    %s\n' "${placeholders[@]}" >&2
    fi
    if ((${#invalid[@]})); then
        echo "  invalid values:" >&2
        printf '    %s\n' "${invalid[@]}" >&2
    fi
    if [[ -z "$check_file" ]]; then
        echo "Copy $stack_dir/.env.example to $env_file and fill in every placeholder first." >&2
    else
        echo "Fill in real secrets first, for example with: openssl rand -base64 32" >&2
    fi
    exit 2
fi

if [[ -n "$release_tag" ]]; then
    : "${GATEWAY_IMAGE:=ghcr.io/gmorandi/scalaapi-platform/gateway:$release_tag}"
    : "${PLATFORM_SILO_IMAGE:=ghcr.io/gmorandi/scalaapi-platform/platform-silo:$release_tag}"
    : "${ADMIN_API_IMAGE:=ghcr.io/gmorandi/scalaapi-platform/admin-api:$release_tag}"
    : "${MIGRATOR_IMAGE:=ghcr.io/gmorandi/scalaapi-platform/migrator:$release_tag}"
    : "${PROVIDER_MOCK_IMAGE:=ghcr.io/gmorandi/scalaapi-platform/provider-mock:$release_tag}"
    export GATEWAY_IMAGE PLATFORM_SILO_IMAGE ADMIN_API_IMAGE MIGRATOR_IMAGE PROVIDER_MOCK_IMAGE
    echo "Deploying release images tagged $release_tag from ghcr.io/gmorandi/scalaapi-platform"
fi

if ((${#generated[@]})); then
    persist_file="$check_file"
    if [[ -z "$persist_file" ]]; then
        persist_file="$env_file"
        compose+=(--env-file "$env_file")
    fi
    if [[ ! -f "$persist_file" ]]; then
        printf '# Generated by start.sh --demo\n' > "$persist_file"
        chmod 600 "$persist_file"
    fi
    for pair in "${generated[@]}"; do
        name="${pair%%=*}"
        if grep -qE "^[[:space:]]*(export[[:space:]]+)?${name}=" "$persist_file"; then
            sed -i -E "s|^[[:space:]]*(export[[:space:]]+)?${name}=.*|${pair}|" "$persist_file"
        else
            printf '%s\n' "$pair" >> "$persist_file"
        fi
    done
    echo "Demo mode generated these secrets and saved them to $persist_file:"
    printf '  %s\n' "${generated[@]}"
fi

args=(up -d)
if (( build )); then
    args+=(--build)
fi

echo "Starting the ScalaAPI stack with $container_cli compose"
"${compose[@]}" "${args[@]}"
