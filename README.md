# ScalaAPI Platform

**English** | [简体中文](README.zh-CN.md)

The business and monetary authority of the
[ScalaAPI](https://github.com/GMorandi/ScalaAPI) LLM API platform: a .NET 10
service cluster that owns accounts, credentials, routing, quotas, leases,
settlement, billing, and operations, fronted by a C++ gateway over a Cap'n Proto
IPC contract.

> Status: active development, not yet release-certified. Deploy only as part of
> the paired release described in the [ScalaAPI superproject](https://github.com/GMorandi/ScalaAPI).

## Architecture at a glance

- **PostgreSQL** is the single business/monetary authority (68 ordered
  greenfield migrations; no imported history).
- **Orleans** virtual actors coordinate aggregate execution in the platform silo.
- **Garnet** holds rebuildable projections/caches (catalogue, config).
- **S3-compatible storage (MinIO)** owns media and backup bytes.
- **Cap'n Proto RPC** over a Unix domain socket serves gateway dispatch:
  request leases, balance holds, usage settlement, aborts, content policy, and
  chunked blob upload for large request bodies.
- The stack runs two silos + two gateways; background workers (backup scheduler,
  provider quota refresh, channel monitor, reconciliation) elect a single leader
  through database claims and advisory locks.

See [docs/architecture.md](docs/architecture.md) for the full document.

## Layout

```
src/
  Platform.Host/     Orleans silo + Cap'n Proto RPC host + background workers
  Admin.Api/         Admin/user HTTP API, backup/restore store, first-admin setup
  Data/              Persistence: accounting, quotas, backups, provider state
  Grains(.Interfaces)/ Orleans grain contracts and implementations
  Security/          Crypto, JWT, redaction, master-key operations
  Db.Migrator/       Ordered migration runner
  Provider.Mock/     Deterministic upstream provider mock (all four protocols)
  ObjectStorage.FaultProxy/  Fault-injection proxy for object-storage drills
  admin-web/         Admin console (Vue)
  user-web/          User portal (Vue)
contracts/capnp/     Canonical Cap'n Proto contract (gateway holds a vendored copy)
deploy/migrations/   001–068 greenfield schema migrations
deploy/stack/        Compose topology, smoke/stress/fault gates
test/                Host/Admin/Grains/Provider-Mock test suites
```

## Build and test

Requires .NET 10 SDK.

```sh
dotnet build
dotnet test test/Host.Tests            # unit tests run without a database
```

Database-backed tests need a migrated schema and read the connection string
from `GREENFIELD_SCHEMA_CONNECTION`:

```sh
export GREENFIELD_SCHEMA_CONNECTION="Host=localhost;Database=platform;Username=platform;Password=..."
dotnet test test/Host.Tests test/Admin.Tests
```

Generated Cap'n Proto C# output must match the pinned compiler exactly:

```sh
CAPNP_COMPILER=/path/to/capnp-1.0.2 scripts/verify-generated-contracts.sh
```

## Run the full stack

The reference topology (PostgreSQL, Garnet, MinIO, provider mock, two platform
silos, two gateways, admin/user web) is defined in
[deploy/stack/docker-compose.yml](deploy/stack/docker-compose.yml). Several
environment variables (secrets, ports) are required — see
[deploy/stack/README.md](deploy/stack/README.md) and `smoke.sh` for the
authoritative provisioning. With the variables exported:

```sh
docker compose -p scalaapi-dev --env-file dev.env -f deploy/stack/docker-compose.yml up -d --build
```

Admin console: `http://localhost:3000` · User portal: `http://localhost:3001` ·
Gateway: `http://localhost:8080`.

## Contract discipline

`contracts/capnp/` is canonical; the gateway repository vendors a byte-identical
copy, and `validate-pair.sh` in the superproject rejects drifting pairs. Schema
changes are atomic paired changes across both repositories.
