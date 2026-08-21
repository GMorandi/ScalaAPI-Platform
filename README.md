# ScalaAPI Platform

**English** | [简体中文](README.zh-CN.md)

[![ScalaAPI Release](https://github.com/GMorandi/ScalaAPI-Platform/actions/workflows/release.yml/badge.svg)](https://github.com/GMorandi/ScalaAPI-Platform/actions/workflows/release.yml)
[![.NET Build](https://github.com/GMorandi/ScalaAPI-Platform/actions/workflows/dotnet.yml/badge.svg)](https://github.com/GMorandi/ScalaAPI-Platform/actions/workflows/dotnet.yml)
[![Gateway Build](https://github.com/GMorandi/ScalaAPI-Platform/actions/workflows/gateway.yml/badge.svg)](https://github.com/GMorandi/ScalaAPI-Platform/actions/workflows/gateway.yml)
[![Image Build and Integration](https://github.com/GMorandi/ScalaAPI-Platform/actions/workflows/stack.yml/badge.svg)](https://github.com/GMorandi/ScalaAPI-Platform/actions/workflows/stack.yml)

The business and monetary authority of the
[ScalaAPI](https://github.com/GMorandi/ScalaAPI) LLM API platform: a .NET 10
service cluster that owns accounts, credentials, routing, quotas, leases,
settlement, billing, and operations — fronted by a C++23 edge gateway over a
canonical Cap'n Proto IPC contract.

> **Status**: active development, not yet release-certified. The gateway lives
> in-tree under `gateway/`; releases are cut from this repository with a single
> tag.

## Why ScalaAPI

API-relay products live or die by billing correctness. ScalaAPI is built around
one rule: **PostgreSQL is the only authority for business and monetary state**.
Everything else — Orleans actors, the Garnet cache, the gateway itself — is a
rebuildable projection or an untrusted edge.

- **Exactly-once settlement** — every billable request carries one idempotent
  lease with an immutable price snapshot and a bounded balance hold. An unknown
  upstream charge state becomes operator-visible reconciliation work instead of
  silent loss.
- **Evidence-driven dispatch** — the gateway must obtain a durable `forwarded`
  acknowledgement before contacting a provider, and records `output_started` no
  later than the first client write.
- **Protocol translation at the edge** — OpenAI Chat Completions / Responses,
  Anthropic Messages, and Gemini generateContent (JSON and SSE), plus
  embeddings, audio (TTS/STT), realtime WebSocket sessions, and media
  generation.
- **Fail-closed content policy** — bounded pre-provider and pre-client
  evaluation with audited rules and explicit classifier-outage behavior.
- **Operations built in** — backup/restore with offsite verification, provider
  quota refresh, channel monitoring, and a source-built smoke/stress/fault gate
  suite.

## Architecture

```text
Clients
  │  product-native HTTP / SSE / WebSocket
  ▼
┌────────────────────────────────────────────────┐
│ Gateway (C++23, Photon coroutines)             │
│ protocol validation · translation · streaming  │
└────────────────────────────────────────────────┘
  │  Cap'n Proto RPC over a Unix domain socket
  ▼
┌────────────────────────────────────────────────┐
│ Platform (.NET 10, Orleans virtual actors)     │
│ identity · policy · scheduling · leases ·      │
│ pricing · accounting                           │
└────────────────────────────────────────────────┘
  ├──▶ PostgreSQL 17   durable business & accounting authority
  ├──▶ Garnet          rebuildable projections/cache, never authoritative
  ├──▶ S3 (MinIO)      media & backup bytes
  └──▶ Providers       catalogues, credentials, quota, inference
```

The reference deployment runs **two platform silos and two gateways**;
background workers (backup scheduler, provider quota refresh, channel monitor,
reconciliation) elect a single leader through PostgreSQL claims and advisory
locks. See [docs/architecture.md](docs/architecture.md) for the full
architecture document.

## Repository layout

```
src/
  Platform.Host/     Orleans silo + Cap'n Proto RPC host + background workers
  Admin.Api/         Admin/user HTTP API, backup/restore store, first-admin setup
  Data/              Persistence: accounting, quotas, backups, provider state
  Grains(.Interfaces)/ Orleans grain contracts and implementations
  Security/          Redaction and certificate tracking
  Db.Migrator/       Ordered migration runner
  Provider.Mock/     Deterministic upstream provider mock (all four protocols)
  ObjectStorage.FaultProxy/  Fault-injection proxy for object-storage drills
admin-web/           Admin console (SolidJS)
user-web/            User portal (SolidJS)
gateway/             C++23 edge gateway (in-tree subtree, history preserved)
contracts/capnp/     Canonical Cap'n Proto contract (consumed directly by the gateway build)
deploy/migrations/   Greenfield schema migrations 001–068 (no 054)
deploy/stack/        Compose topology, smoke/stress/fault gates
test/                Host/Admin/Grains/Provider-Mock test suites plus benchmarks
```

## Tech stack

| Layer | Technology |
| --- | --- |
| Backend | .NET 10, ASP.NET Core, Orleans (ADO.NET clustering/storage/reminders) |
| Gateway | C++23, PhotonLibOS coroutines, Cap'n Proto 1.0.2 |
| Data | PostgreSQL 17 (authority), Garnet (projections), S3-compatible object storage |
| Frontend | SolidJS, Vite, Tailwind CSS, Playwright e2e |
| Contract | Cap'n Proto schemas with pinned digests and generated bindings |
| Release | Single-tag releases, pinned container images, evidence manifest |

## Getting started

### Prerequisites

- .NET 10 SDK
- Docker or Podman with Compose (for the full stack)
- Cap'n Proto 1.0.2 toolchain (only when regenerating contract bindings)

### Build and test

```sh
dotnet build
```

Most tests are database-backed: they need a migrated schema and read the
connection string from `GREENFIELD_SCHEMA_CONNECTION` (unset, they fail fast):

```sh
export GREENFIELD_SCHEMA_CONNECTION="Host=localhost;Database=platform;Username=platform;Password=..."
dotnet test test/Host.Tests test/Admin.Tests
```

The gateway builds and tests with CMake/CTest from the repository root:

```sh
cmake -S gateway -B gateway/build -DCMAKE_BUILD_TYPE=Release -DCMAKE_POLICY_VERSION_MINIMUM=3.5
cmake --build gateway/build -j"$(nproc)"
ctest --test-dir gateway/build --output-on-failure
```

### Run the full stack

The reference topology (PostgreSQL, Garnet, MinIO, provider mock, two platform
silos, two gateways, Admin API, admin/user web) is defined in
[deploy/stack/docker-compose.yml](deploy/stack/docker-compose.yml). Several
environment variables (secrets, ports) are required — see
[deploy/stack/README.md](deploy/stack/README.md) and `smoke.sh` for the
authoritative provisioning. With the variables exported (or a `dev.env` next to
the Compose file):

```sh
deploy/stack/start.sh
```

The script auto-detects the container runtime (Docker Compose or Podman
Compose; override with `CONTAINER_CLI`) and builds every component from
source.

| Service | URL |
| --- | --- |
| Admin console | `http://localhost:3000` |
| User portal | `http://localhost:3001` |
| Gateway | `http://localhost:8080` |

Production deployments pin release images via `GATEWAY_IMAGE`,
`PLATFORM_SILO_IMAGE`, `ADMIN_API_IMAGE`, `MIGRATOR_IMAGE`, and
`PROVIDER_MOCK_IMAGE`, for example
`GATEWAY_IMAGE=ghcr.io/gmorandi/scalaapi-platform/gateway:<tag>` (see
[deploy/stack/README.md](deploy/stack/README.md)).

### Verify the deployment gate

`deploy/stack/smoke.sh` builds everything from source into a throwaway Compose
project and exercises the full acceptance contract: migrations (applied, then
skipped on rerun), chat settlement, idempotent replay, realtime sessions,
provider fault matrices, media storage under injected failures, and
cross-process restarts. `deploy/stack/garnet_tls_smoke.sh` runs the same gate
with Garnet TLS enabled.

## Contract discipline

`contracts/capnp/` is the single canonical copy of the gateway↔platform
contract; the gateway compiles these schemas directly (`gateway/CMakeLists.txt`
references `../contracts/capnp`). Any schema change must update the schemas,
`SHA256SUMS`, the generated C# output, and the gateway protocol fixtures in the
same commit. At the repository root, `scripts/verify-contracts.sh` checks the
recorded digests, and `scripts/verify-generated-contracts.sh` regenerates the
C# output with the pinned compiler and compares it byte-for-byte:

```sh
CAPNP_COMPILER=/path/to/capnp-1.0.2 scripts/verify-generated-contracts.sh
```

## Documentation

- [docs/architecture.md](docs/architecture.md) — system boundary, ownership
  rules, billable request lifecycle, durable data rules, release discipline
- [gateway/README.md](gateway/README.md) — gateway build, runtime, and
  environment variables
- [contracts/capnp/README.md](contracts/capnp/README.md) — contract layout and
  verification
- [deploy/stack/README.md](deploy/stack/README.md) — topology, environment
  provisioning, and the acceptance gates
