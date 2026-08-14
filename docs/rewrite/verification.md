# ScalaAPI Rewrite Verification

Audit date: 2026-08-14 (Europe/Vienna). Results below are from the current
component heads unless a check explicitly says `ScalaAPI pair`. No check tests
Sub2API compatibility or imports Sub2API state.

## Source refs and worktrees

| Object | Ref | Worktree/evidence |
| --- | --- | --- |
| Platform | `30d82d01c2daed1ff0460fa020cad5d9ff434cdd` | `master == origin/master`, clean before audit documentation edits |
| Gateway | `98c62fdec99836929f1ab47412ef46c7f2c67683` | `master == origin/master`, but two uncommitted files are present |
| ScalaAPI pair | `032721b65a3960171ce66a390451b98364f4b94a` | pins Platform `e73a5d8` and Gateway `777278e`; clean and structurally valid |
| Sub2API research | `origin/main@fbfdcef8184ae4b2e224d5cfc47cf1d0e3742710` | non-normative snapshot; local branch is intentionally divergent |

## Platform build and database

Commands run from Platform:

```bash
dotnet restore ScalaAPI.Platform.slnx
dotnet build ScalaAPI.Platform.slnx --no-restore -c Release
```

Result: PASS, all projects built with zero warnings and errors.

An isolated PostgreSQL 17 database was created without modifying the existing
`platform` database. The migration directory contained `000-orleans.sql` plus
`deploy/migrations/*.sql`:

```bash
dotnet run --project src/Db.Migrator/Db.Migrator.csproj --no-build -c Release -- "$migration_dir"
dotnet run --project src/Db.Migrator/Db.Migrator.csproj --no-build -c Release -- "$migration_dir"
```

Result: first run applied 66 records; second run skipped 66 records. No missing
`users`/`api_keys` references remain in the greenfield chain.

With `GREENFIELD_SCHEMA_CONNECTION` pointing at that database:

```bash
dotnet test ScalaAPI.Platform.slnx --no-build -c Release --verbosity minimal
```

Result: PASS, 502/502: Provider.Mock 99, Grains 80, Admin 65, Host 258.

As a negative control, running Host.Tests with the variable removed fails with
113 visible dependency failures rather than silently skipping integration work.
That is the intended behavior for a database-required test assembly.

## Platform benchmarks and generated contracts

```bash
dotnet run --project test/Platform.Benchmarks -c Release --no-build -- \
  --filter '*' --job dry
```

Result: PASS. BenchmarkDotNet discovers six cases (two dispatch and four
Scheduler), and all six benchmark processes exit zero. Dry jobs emit only the
minimum-iteration-time advisory.

```bash
CAPNP_COMPILER=/path/to/capnp-1.0.2 scripts/verify-generated-contracts.sh
(cd contracts/capnp && sha256sum --check SHA256SUMS)
scripts/verify-contracts.sh ../gateway
scripts/verify-retired-dependencies.sh
bash -n deploy/stack/*.sh scripts/*.sh
```

Result: generated C# output, Platform and Gateway contract digests, retired
dependency scan and shell syntax all pass. The generated-contract check used the
Cap'n Proto 1.0.2 binary built in the local audit workspace.

## Web applications and dependency scan

For both `admin-web` and `user-web`:

```bash
npm ci
npm run typecheck
npm run build
npm audit
```

Typecheck and production build pass for both applications. `npm audit` reports
one high-severity `nanoid` advisory (`<3.3.18`) in each dependency tree; this is
an open release-security item, not a build failure.

## Gateway

The current worktree has these uncommitted changes:

```text
src/server/gateway_handler.cpp
test/unit/test_protocol.cpp
```

Two independent builds were used so the remote commit and user worktree were not
conflated.

A clean `git archive` of `98c62fdec99836929f1ab47412ef46c7f2c67683` was
configured and built in Release mode. The audit host's newer CMake needs
`-DCMAKE_POLICY_VERSION_MINIMUM=3.5` to configure the repository's bundled
Cap'n Proto dependency. From that clean archive:

```bash
ctest --test-dir build --output-on-failure
./build/test/benchmarks --benchmark_min_time=0.1s
```

Result: PASS. CTest passes 161/161 cases. The benchmark binary executes all 16
registered entries, covering protocol parsing/conversion, stream transforms,
key hashing, speculative cache operations and durable usage collection.

The shared Gateway worktree is a different result: compilation reaches the
uncommitted `gateway_handler.cpp` and fails because
`LeaseAbortDisposition::Safe` is not a declared enum value. The associated
uncommitted `test_protocol.cpp` change therefore is not accepted as verified.
This failure is deliberately attributed to user worktree state, not to clean
remote commit `98c62fd`.

## ScalaAPI pair gate

From the superproject:

```bash
scripts/validate-pair.sh
scripts/generate-pair-manifest.sh /tmp/scalaapi-pair-manifest.json
```

Result: PASS for superproject `032721b`; Platform `e73a5d8`; Gateway `777278e`.
The manifest records three identical Cap'n Proto files, contract digest
`736ad7a5da2c760a4e3a8e64b0c61044ed092d910df82116143c580d5d2464e6`, and 66
migration inputs. This proves pair identity and contract integrity, not that the
latest standalone component heads have been released.

## Hosted workflow snapshot

The latest hosted runs were checked on 2026-08-14:

| Workflow | Commit | Result |
| --- | --- | --- |
| [ScalaAPI Paired CI #6](https://github.com/GMorandi/ScalaAPI/actions/runs/31772708393) | `032721b` | PASS; pair, Gateway, Platform greenfield, Web and manifest jobs completed |
| [Gateway CI #5](https://github.com/GMorandi/ScalaAPI-GateWay/actions/runs/31780006907) | `98c62fd` | PASS |
| [Platform .NET Build #256](https://github.com/GMorandi/ScalaAPI-Platform/actions/runs/31780538220) | `30d82d0` | PASS |
| [Platform Greenfield Verification #242](https://github.com/GMorandi/ScalaAPI-Platform/actions/runs/31780538193) | `30d82d0` | FAIL during the second checkout of obsolete `scalaapi/gateway`; build, tests and benchmark steps were skipped |

The obsolete Platform cross-repository workflow has now been removed. Its former
benchmark invocation already quotes `'*'`; the earlier shell-expansion error is
therefore not a current failure in the centralized paired workflow.

Source audit of the release workflow found an evidence limitation. Platform TRX
files are uploaded, but `generate-release-evidence.sh` receives only the pair
manifest and five image metadata files. It emits fixed gate names, `status:
"passed"` and `skipped: []`; it does not parse executed/passed/failed/skipped
totals for Platform, Gateway or Web jobs. Workflow dependency success is a useful
gate, but the generated JSON is not yet the detailed test evidence required by
the target release contract.

## Not run or not promotion evidence

- Source-built two-Silo/two-Gateway Compose deployment.
- Authenticated Admin/User browser workflows.
- Live Provider quota/catalogue/search/audio/xAI contracts.
- Object-store partition, restart and reconciliation drills.
- The complete 3600-second mixed fault/load test.
- A release tag and registry image evidence for the latest component heads.

Script presence, mock-only success, historical logs and a no-database green test
run cannot promote these domains to `verified`.

## Acceptance commands for the next release candidate

1. Reconcile the Gateway worktree changes and require its final commit to repeat
   the clean build, 161-case CTest suite and benchmark smoke.
2. Stage the intended Platform/Gateway gitlinks in ScalaAPI and run pair validation.
3. Re-run Platform empty-schema double migration, database tests, generated
   contracts, six benchmarks and both Web builds from that exact pair.
4. Execute source-built browser, short smoke, object-store fault and one-hour
   fault/load gates; retain command output, test totals, skipped reasons and
   cleanup assertions.
5. Only then create a superproject SemVer tag and generate release evidence.
