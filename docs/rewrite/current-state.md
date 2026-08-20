# ScalaAPI Rewrite Current State

Audit date: 2026-08-14 (Europe/Vienna). This document is the current evidence
snapshot for the greenfield rewrite. It distinguishes the latest component heads
from the immutable pair currently pinned by the ScalaAPI superproject.

Sub2API is non-normative, read-only research input. It does not define ScalaAPI
API paths, error envelopes, schema, migrations, identifiers, keys, cache state,
deployment, upgrade or data-import behavior. ScalaAPI is a new product and has no
compatibility or migration path to Sub2API.

## Executive result

The implementation has moved materially beyond the previous red audit. The
greenfield migration chain, canonical/vendor Cap'n Proto contract, database test
gate and Scheduler benchmark gate now pass on the latest Platform head. The
component repositories have also added real readiness, evidence durability,
provider quota, model catalogue, channel monitor and backup implementations.

The product is still not release-certified. A clean archive of Gateway
`98c62fd` builds and passes all 161 CTest cases plus its 16 benchmark entries,
but the shared Gateway worktree contains two uncommitted user changes which do
not compile (`LeaseAbortDisposition::Safe` does not exist). The ScalaAPI
superproject remains pinned to an older but structurally valid pair, so the
latest standalone component work is not yet a published paired release. Browser
E2E, live Provider evidence and the 3600-second fault/load gate have not been
executed in this audit.

## Repository refs

| Repository | Ref and worktree | Role |
| --- | --- | --- |
| Platform | `master == origin/master@30d82d01c2daed1ff0460fa020cad5d9ff434cdd`; clean before audit documentation edits | C#/.NET 10, Orleans control plane, PostgreSQL authority, Admin/User APIs, Provider mock, web applications, migrations and canonical contracts |
| Gateway | `master == origin/master@98c62fdec99836929f1ab47412ef46c7f2c67683`; worktree dirty in `src/server/gateway_handler.cpp` and `test/unit/test_protocol.cpp` | C++ edge, HTTP/WebSocket protocols, conversion, Provider transport, SQLite usage outbox and vendored contracts |
| ScalaAPI supported pair | superproject `032721b65a3960171ce66a390451b98364f4b94a`; `platform@e73a5d806000722e3b3abe7ee25c7075b4007687`, `gateway@777278ea8b38491a19f585b3c026f28da7726c0f` | Immutable release/compatibility authority; pair validation passes, but it does not contain the latest component commits |
| Sub2API research snapshot | `origin/main@fbfdcef8184ae4b2e224d5cfc47cf1d0e3742710`; local checkout `main@43ec48da` is one commit ahead and 283 behind | Non-normative capability discovery only |

The latest standalone Platform/Gateway contract files are identical. The current
superproject pair also passes `scripts/validate-pair.sh` and has a 66-entry
migration manifest, but its component pins must be deliberately advanced and
revalidated before it can represent the latest implementation.

## Current surface

These are breadth signals, not completion percentages.

| Surface | Count or result |
| --- | ---: |
| Platform tracked C# files under `src` | 181 |
| Platform tracked C# test files | 90 |
| Platform xUnit declarations | 466 |
| Platform product SQL migrations | 65 (`001-053`, `055-066`; `054` is absent) plus `000-orleans.sql` at runtime |
| Platform direct Admin API mappings | 189 |
| Admin/User Web TS/TSX source files | 28 / 21 |
| Gateway production C++ source/header files | 52 |
| Gateway test/benchmark C++ files | 12 |
| Sub2API `origin/main` non-test Gin route registrations | 1,043 |
| Sub2API `origin/main` Ent schema files | 42 |
| Sub2API `origin/main` SQL migrations | 259 |
| Sub2API `origin/main` Vue/TypeScript files | 723 |
| Sub2API `origin/main` backend Go files | 2,340 |

Reference-tree counts are discovery signals only. They never create automatic
ScalaAPI requirements.

## Architecture and ownership

- Gateway owns public protocol parsing, bounded conversion, HTTP/SSE/WebSocket
  lifecycle, Provider transport, retries and a durable local usage/evidence outbox.
- Platform owns identity, API keys, policy, scheduling, leases, pricing snapshots,
  holds, usage settlement, ledger effects, reconciliation, media metadata,
  operations and Admin/User APIs.
- PostgreSQL is the business and monetary authority. Orleans coordinates aggregate
  execution; Garnet is a rebuildable projection/cache; S3-compatible storage owns
  media and backup bytes.
- Cap'n Proto has one Platform source and one identical Gateway vendor copy. A
  contract change is an atomic paired change; no old internal revision negotiation
  or compatibility branch is required.

## What is now evidenced

- Platform Release solution build passes with zero warnings/errors.
- A fresh PostgreSQL 17 database accepts `000-orleans.sql` plus all 65 product
  migrations on the first run (66 `apply` records) and produces 66 `skip` records
  on the second run.
- With that schema, the four Platform test assemblies pass 502/502:
  Provider.Mock 99, Grains 80, Admin 65 and Host 258.
- Without `GREENFIELD_SCHEMA_CONNECTION`, Host.Tests fails visibly (113 failures)
  instead of silently reporting integration tests as passing.
- Platform benchmark discovery finds six cases (two dispatch and four scheduler);
  all six processes exit successfully. BenchmarkDotNet still reports the normal
  Dry-job minimum-iteration advisory.
- A clean archive of Gateway `98c62fd` configures and builds in Release mode,
  passes 161/161 CTest cases and executes all 16 benchmark entries. The audit
  host's newer CMake requires `-DCMAKE_POLICY_VERSION_MINIMUM=3.5` for the
  repository's bundled Cap'n Proto dependency.
- Canonical/vendor SHA256 manifests and all three Cap'n Proto files match. The
  checked-in C# output also matches the pinned Cap'n Proto 1.0.2 compiler output.
- Admin Web and User Web typecheck and production builds pass. `npm audit` reports
  zero high-severity advisories after resolving the transitive `nanoid` advisory.
- Retired-dependency scanning and all checked-in shell syntax checks pass.
- The latest Platform source includes PostgreSQL-fenced active/passive monitor
  leadership, real bounded channel probes, Provider quota clients for OpenAI,
  Anthropic, Gemini and xAI with real account discovery and stale tracking,
  model-catalogue refresh, scheduled backup execution with inline pg_dump,
  offsite upload with remote readback verification, blob upload RPC for large
  media bodies (>512KiB), and realtime session caps with safe abort disposition.
  These slices still need source-built multi-process or live-Provider acceptance
  evidence before promotion to `verified`.

## Current release blockers and gaps

1. The paired superproject now pins Platform `1cfbb25` and Gateway `cdc7505`
   with passing pair validation and matched Cap'n Proto contracts. Promotion of
   a newer pair is a release decision that requires both component builds,
   paired CI gates and refreshed evidence.
2. Compose smoke, authenticated browser workflows, live Provider contracts,
   object-store fault recovery and the full 3600-second mixed fault/load run are
   not current evidence. Script presence and unit tests do not promote these
   domains.
3. The quota, catalogue, monitor and backup workers have focused source/tests
   including contract tests, real account discovery, stale tracking, inline
   pg_dump execution and offsite readback verification, but still require
   controlled multi-process, Provider and object-store runtime proof, including
   retry, fencing, outage and reconciliation assertions.
4. The paired release workflow is centralized in ScalaAPI and no longer has the
   old component `latest` bypass, but it currently builds the pinned pair. A
   release manifest must never imply that standalone branch heads were released.
5. The release workflow uploads Platform TRX files, but
   `generate-release-evidence.sh` does not parse them or Gateway/Web results. It
   writes fixed gate names with `status: "passed"` and `skipped: []`, so the
   artifact does not yet contain executed/passed/failed/skipped totals.
6. The former Platform cross-repository Greenfield Verification workflow was
   removed from this component repository. Its last hosted run failed during the
   second checkout because it referenced the obsolete `scalaapi/gateway` repository;
   cross-repository verification now belongs only to ScalaAPI's paired workflow.

## Non-compatibility invariants

1. Bootstrap only from an empty product schema; never import Sub2API rows,
   migrations, CDC history, Redis state, IDs, hashes, credentials or keys.
2. Define product-native endpoints, DTOs, errors and state machines. Public
   provider protocols may be supported for client utility, but Sub2API's private
   API and UI are not contracts.
3. Replace an unreleased internal contract when needed. Do not add deprecated
   aliases, dual-read/write, legacy route shims or version negotiation.
4. Keep monetary decisions in PostgreSQL and durable lease/evidence records.
5. Admit a capability only through a ScalaAPI product decision and current native
   evidence, never by copying reference-project breadth.

## Release posture

Status: **implementation in progress; paired release not certified**. The next
work is to reconcile the Gateway worktree, decide and validate a new
superproject pair, then run the source-built runtime/browser/fault evidence and
security gates. No compatibility work is required or planned.
