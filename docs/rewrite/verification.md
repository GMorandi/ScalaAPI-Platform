# ScalaAPI Rewrite Verification

Audit date: 2026-08-14 (Europe/Vienna).

This file separates commands executed against current source from historical logs.
Only the first category is current evidence. All checks target the greenfield
ScalaAPI product; none tests Sub2API compatibility or imports Sub2API state.

## Pinned source

| Repository | Ref | Fetch/worktree result |
| --- | --- | --- |
| Platform | `bc083d18c6b0ad9474df3d609527e0a2f72cf981` | `master == origin/master`, clean before docs edits |
| Gateway | `b6e4e02061074158159aaefd00d2bc7b44782e2a` | `master == origin/master`, clean |
| Sub2API research snapshot | `origin/main@fbfdcef8184ae4b2e224d5cfc47cf1d0e3742710` | Local `main@43ec48d`, clean, 1 ahead / 283 behind; non-normative only |

All remotes were refreshed with `git fetch --prune`. Sub2API describes itself as
version `v0.1.176-5-gfbfdcef81`; its `origin/main` did not move during this refresh.

## Current command evidence

### Gateway build and tests

```bash
cmake -S . -B build -DCMAKE_BUILD_TYPE=Release \
  -DGATEWAY_BUILD_TESTS=ON -DGATEWAY_BUILD_BENCHMARKS=ON
cmake --build build --parallel "$(nproc)"
ctest --test-dir build --output-on-failure
./build/test/benchmarks --benchmark_min_time=0.1s
```

Result: exit 0; Release build succeeded; 159/159 CTest cases passed in 1.50s and
the 16 registered benchmark routines completed their smoke run. This covers current
C++ unit/protocol and microbenchmark behavior. It does not exercise Platform,
PostgreSQL, a live Garnet/Cap'n Proto/Provider connection, object storage, browser
clients or a source-built Gateway container.

Gateway's test CMake defines one unit executable plus the benchmark executable, not
an integration/E2E target. Router tests use unreachable Garnet/UDS clients and mostly
assert that known routes are not 404/501. No test invokes the production
`GatewayHandler`, `HttpServer`, realtime bridge or `UsageReporter` against live
dependencies.

### Platform Release build

```bash
dotnet build ScalaAPI.Platform.slnx -c Release --no-restore
```

Result: exit 0. This proves the current .NET source builds; it does not repair the
database, benchmark, paired-contract or runtime failures below.

### Platform tests without database prerequisites

```bash
dotnet test ScalaAPI.Platform.slnx -c Release --no-build \
  --logger 'console;verbosity=minimal'
```

Result: exit 0, reported 502/502:

| Project | Reported |
| --- | ---: |
| Grains.Tests | 80 |
| Host.Tests | 258 |
| Admin.Tests | 65 |
| Provider.Mock.Tests | 99 |

Interpretation: unit/non-database paths pass. This is not a 502-test integration
pass. Static inspection found 46 test files that read
`GREENFIELD_SCHEMA_CONNECTION` and 123 test methods with a direct early return when
it is absent. VSTest reports these as passed rather than skipped.

### Scheduler benchmark gate

```bash
dotnet run --project test/Platform.Benchmarks/Platform.Benchmarks.csproj \
  -c Release --no-build -- --job Dry --filter '*Scheduler*'
```

Result: exit 1. All four Scheduler benchmark cases have no valid report because
Orleans cannot activate `AccountGrain`: the benchmark Silo does not register
`ISlotLeaseStore`. The runner correctly treats missing/failed reports as a gate
failure. Both ordinary CI and `greenfield-verification.yml` invoke this command.

### Web applications

In both `admin-web` and `user-web`:

```bash
npm ci --silent
npm run typecheck
npm run build
```

Result: both commands exit 0. Admin Web transformed 51 modules; User Web transformed
45. This proves compilation/type consistency, not authenticated workflow behavior.

### Contract checks

Gateway repository-local check:

```bash
(cd gateway/proto && sha256sum --check SHA256SUMS)
```

Result: exit 0 for all three vendored schemas.

Platform's canonical repository-local digest is separately stale: its recorded
`dispatch.capnp` hash is `3f5c49...`, while the current canonical file is
`86a70d...`. Thus the Platform local check fails before the sibling comparison;
Gateway's passing `3f5c49...` digest only confirms its older vendored bytes.

Authoritative cross-repository check:

```bash
cd platform
bash scripts/verify-contracts.sh ../gateway
```

Result: exit 1. `invalidation.capnp` and `types.capnp` match;
`dispatch.capnp` differs. Platform's canonical enum includes:

```capnp
audioTts @12;
audioStt @13;
```

Gateway's vendored schema stops at `antigravity @11`. Gateway's hand-written C++
request enum nevertheless defines numeric values 12/13, so independent compilation
does not make the schema release coherent.

The hosted greenfield workflow invokes `scripts/verify-contracts.sh` without a
Gateway argument and checks out only Platform. After Platform's stale local digest
is fixed it would exit at the script's zero-argument branch without comparing the
sibling repository.

### Retired dependency / compatibility scan

```bash
cd platform
bash scripts/verify-retired-dependencies.sh
```

Result: exit 0. The scanned `src` and `deploy` runtime paths contain no forbidden
Sub2API, SubData, Redis, CDC/Debezium or legacy dependency reference except the
documented greenfield note allowed by the script. Gateway uses SQLite for its local
durable usage outbox; that is product-native and is not Sub2API compatibility.

### Empty PostgreSQL 17 migration

A temporary `postgres:17-alpine` container was started on loopback. The exact
greenfield workflow inputs were copied into a temporary directory:

```bash
cp deploy/orleans-postgres-schema.sql "$migration_dir/000-orleans.sql"
cp deploy/migrations/*.sql "$migration_dir/"
dotnet run --project src/Db.Migrator/Db.Migrator.csproj \
  --no-build -c Release -- "$migration_dir"
```

Result: exit 134. `000-orleans.sql` and product migrations 001-053 committed. The next
file, `055-search-history.sql`, failed with PostgreSQL `42P01`:

```text
relation "users" does not exist
```

The console prints `apply 053-subscription-quota-events.sql` immediately before the
failure because the sorted migration set has no 054. Source inspection confirms:

- 055 references `users(id)` and `api_keys(id)`;
- 056 repeats those foreign-table names;
- the product baseline owns `user_accounts` and `user_api_keys`.

The transaction around 055 rolled back. A second migration pass and the
database-enabled suite could not run. The temporary PostgreSQL container was
stopped by the cleanup trap. No workspace file was changed by the probe.

## Current evidence matrix

| Gate | Status | Evidence strength / limitation |
| --- | --- | --- |
| Gateway Release build | PASS | Current |
| Gateway CTest | PASS, 159/159 | Current unit/protocol |
| Gateway benchmark smoke | PASS, 16 routines | Current microbenchmark execution; no regression threshold or integration behavior |
| Platform Release build | PASS | Current compile evidence |
| Platform no-DB solution tests | PASS, 502 reported | Current but partial; 123 database early returns |
| Platform Scheduler benchmark | FAIL, 4/4 no report | Current blocking evidence; missing `ISlotLeaseStore` registration |
| Platform empty-schema migration | FAIL | Current blocking evidence |
| Platform DB-enabled complete suite | BLOCKED | Cannot start until migration repair |
| Admin Web typecheck/build | PASS | Current compile evidence |
| User Web typecheck/build | PASS | Current compile evidence |
| Gateway local contract digest | PASS | Current but repository-local only |
| Platform local contract digest | FAIL | Current canonical `dispatch.capnp` no longer matches its recorded hash |
| Cross-repository contract | FAIL | Current blocking evidence |
| Retired dependency scan | PASS | Current static boundary evidence |
| Release workflow ordering | FAIL, source audit | Platform and Gateway independently publish tags/`latest`; publication can precede clean rebuild and no job proves the pair |
| Alternate publish/local release evidence | FAIL, source audit | Ungated image publish and hard-coded unexecuted pass claims exist |
| Generated Cap'n Proto reproducibility | NOT RUN | `capnp` compiler absent locally; cross-repo bytes already fail |
| Playwright source-built workflows | NOT RUN | Build does not substitute for browser E2E |
| Empty-volume Compose smoke | NOT RUN | Known migration blocker would prevent valid start |
| Live Provider adapters | NOT RUN | Source mock is not live-provider evidence |
| 3600-second stress/fault gate | NOT RUN | Harness has known invalid table names and fatality gaps |
| Backup/offsite/restore drill | NOT RUN | Source contains no-I/O offsite placeholder |
| Hosted CI status | UNVERIFIED | No authenticated GitHub CLI/API evidence was available locally |

## Static audit findings that affect verification

### Integration-test reporting

The repository does have a dedicated `greenfield-verification.yml` with PostgreSQL
17 and a database environment. Its migration step currently uses the same ordering
that failed locally, so source inspection predicts it cannot pass at `bc083d1`.
Ordinary `ci.yml`, `dotnet.yml` and release workflows run without a database and can
therefore report direct-return tests as passed. `admin-web.yml` also marks typecheck
`continue-on-error`, although the separate greenfield workflow does not.
Both ordinary and greenfield workflows additionally invoke the currently failing
Scheduler benchmark, so neither source path is green at the pinned commit.
The release workflow does not check out a paired Gateway and pushes four image
families including `latest` before its later clean rebuild; publication therefore
precedes several proofs that should be prerequisites.
An independent tag-triggered `docker.yml` publishes the same four image families and
`latest` without build test schema or contract gates. The local `deploy/release.sh`
checks only Gateway's own digest before tagging/pushing both repos; its report then
states tests benchmarks clean rebuild and no skips even though none are executed.
Gateway has another independent `v*` release workflow. It runs Gateway-local digest,
unit and benchmark checks, pushes the version and `latest` image, and only then does a
clean rebuild. It never checks out Platform or runs the database, cross-repository
contract or source-built container gates.

### Gateway runtime boundary

Static inspection narrows what the green unit suite proves:

- Gateway requires an API key and hashes it, but Platform is the authority that
  validates the key, scope, capability and policy. Production `ApiKeyAuth` is not
  used as a local authorization decision.
- `forwarded` must be acknowledged before Provider I/O. In contrast,
  `output_started` is a one-shot synchronous RPC after the first client write and a
  failure is log-only. `UsageReporter` also deletes unacknowledged records classified
  non-retryable instead of retaining or incidenting them.
- Response policy is enabled only for Messages, Chat, Responses and Gemini Generate.
  Search, Antigravity, audio/media, embeddings and models are outside that predicate;
  realtime additionally omits the initial body and raw-relays later frames.
- Realtime bypasses ordinary query validation and trusted-proxy client-IP resolution.
  The route/model parser tests do not exercise its handshake, relay, durable usage or
  process-replacement behavior.
- Gateway accepts 32 MiB HTTP bodies but Platform rejects RPC frames above 1 MiB.
  There is no sender preflight or stable oversize product response.
- Startup factories can return objects after dependency or bind/listen failure, and
  `/ready` checks only dispatch UDS. It does not prove Garnet, SQLite durability or
  every per-core listener.
- Anonymous `/models` bypasses Platform and returns HTTP 200 with an empty list when
  Garnet is unavailable. Search is registered as streaming-capable, but the handler's
  chat-only streaming predicate does not enable it.
- Provider auth headers are bounded, but path is concatenated, an unknown method
  falls back to POST, general request headers only filter hop-by-hop fields, and
  decoded TLS-fingerprint fields have no outbound consumer.
- Auth invalidation is a two-second Garnet version poll followed by a full per-core
  cache flush. The vendored subscribe/resync RPC is not used at this baseline.

### Verification harness

The checked-in stress path is not yet trustworthy:

- it queries PostgreSQL table `gateway_usage_outbox`, while Gateway's durable outbox
  is a local SQLite `usage_outbox` and no Platform migration creates that table;
- it queries `reconciliation_incidents`, while the product migration creates
  `accounting_reconciliation_incidents`;
- a dead background process can be logged without incrementing a top-level failure;
- settlement timeout emits a warning rather than failing.

These defects invalidate the previous claim that REL-03 was completed merely
because a 3600-second script exists.

### Operational placeholders

Static source is explicit about unfinished behavior:

- `ChannelMonitorService` makes every process leader and simulates checks;
- `PassiveMonitorV2Service` notes that production leadership should use a database
  advisory lock;
- `ProviderQuotaRefreshService` reads only pre-seeded quota rows and rewrites their
  current snapshot instead of calling a Provider;
- `BackupSchedulerWorker` enforces retention and marks a claim complete but does not
  invoke backup creation;
- `BackupService.UploadOffsiteAsync` records a completed URL/checksum without a PUT.
- `/admin/system/update` fetches release metadata and returns a message saying the
  binary was downloaded, but performs no download, checksum, install or external
  deployment transaction.

The corresponding domains remain non-verified until runtime behavior replaces or
explicitly removes these paths.

### Research snapshot scope check

The pinned Sub2API object was inspected without checking out its divergent local
branch. Reproducible breadth is 668 non-test Gin route registrations, 42 Ent schema
directory files (39 entities, two mixins, one test), 297 Vue files, 426 TypeScript
files and 259 SQL migrations. Static source confirms broad identity, commercial,
Provider, monitoring and Admin/User families, but no test was run against that
research tree and none of its behavior is acceptance evidence.

Four signals required explicit inventory treatment: first-run setup maps to DEP-03;
configurable upstream error handling maps to GW-05/SEC-01; GW-10 names only selected
ScalaAPI WebSocket/sideband protocols rather than all reference variants; in-process
binary replacement is excluded in favor of paired immutable deployment. Custom user
attributes, client version/fingerprint policy, training opt-out and compliance views
remain product decisions within existing CORE/SEC/OPS domains.

### Gateway boundary mismatches

Static comparison found two additional current gaps:

- Realtime constructs a dispatch request without `request_body`, then relays later
  client and Provider frames directly. Platform request policy receives empty content
  and Gateway's HTTP/SSE response-policy hooks are not invoked for WebSocket frames.
- Gateway's HTTP limit is 32 MiB and the full body is embedded in dispatch, while
  Platform closes framed RPC payloads above 1 MiB. The sender has no shared-limit
  preflight, so large multipart/media input does not get a stable product response.

Neither boundary has current end-to-end policy/oversize evidence.

## Historical evidence policy

The previous documents contained many named 2026-08-09 through 2026-08-11 smoke
runs, older commit IDs and test totals (127, 288, 294, 304, 308). They remain useful
for identifying intended invariants and regression scenarios, but they are not
current pass results at Platform `bc083d1` / Gateway `b6e4e02`.

Historical evidence may support design only when all of the following are recorded:

1. exact Platform and Gateway commits;
2. source-built image digests and environment shape;
3. command and exit code;
4. migration manifest and database identity;
5. assertions over durable state, not only HTTP status;
6. cleanup result.

When current source contradicts an old result, current source and reproduced output
win. Thus the migration contract and benchmark gates are red regardless of old
“all tasks DONE” text.

## Acceptance commands after repair

The minimum next proof sequence is:

```bash
# 1. Empty PostgreSQL 17: apply Orleans + all product SQL twice.
# 2. With GREENFIELD_SCHEMA_CONNECTION set to that database:
dotnet test ScalaAPI.Platform.slnx --no-build -c Release --verbosity normal

# 3. Canonical/vendor and generated contracts:
bash scripts/verify-contracts.sh ../gateway
bash scripts/verify-generated-contracts.sh

# 4. Scheduler performance gate:
dotnet run --project test/Platform.Benchmarks/Platform.Benchmarks.csproj \
  -c Release --no-build -- --job Dry --filter '*Scheduler*'

# 5. Gateway and Web:
ctest --test-dir ../gateway/build --output-on-failure
npm --prefix admin-web run typecheck && npm --prefix admin-web run build
npm --prefix user-web run typecheck && npm --prefix user-web run build

# 6. After harness fixes, short smoke then the actual release duration:
deploy/stack/stress-test.sh --duration=120
deploy/stack/stress-test.sh --duration=3600
```

The final release evidence must additionally include source-built Playwright, live
configured Provider contracts, backup/offsite/restore and the paired immutable
release manifest described in [next-stage-plan.md](next-stage-plan.md).
