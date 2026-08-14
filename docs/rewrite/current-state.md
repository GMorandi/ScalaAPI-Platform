# ScalaAPI Rewrite Current State

Audit date: 2026-08-14 (Europe/Vienna). This is the authoritative current-state
summary for the greenfield ScalaAPI rewrite.

ScalaAPI is an independent new product with its own approved capability contract.
Sub2API is only non-normative read-only research input for discovering possible
capability families; its changes never create requirements automatically. There is
deliberately no compatibility contract for its API paths, request or error envelopes,
database schema, migration numbers, IDs, keys, state values, Redis layout,
configuration, deployment, or data.
No Sub2API repository, service, image, database, cache, or secret is a build or
runtime dependency.

## Executive result

The implementation is broad, but the repository is not currently release-ready and
the previous `65/65 implemented` / `project complete` conclusion is withdrawn.
Current evidence proves that many source slices exist and that the local unit/build
surface is healthy. It does not prove the complete product from an empty database or
through a coordinated two-repository release.

The most material release-blocking contradictions reproduced on the current commits
are:

1. The greenfield migrator commits Orleans plus product migrations 001-053, then
   migration 055 fails with `42P01: relation "users" does not exist`. The
   later search and voice migrations refer to `users` and `api_keys`, while the
   product schema owns `user_accounts` and `user_api_keys`.
2. Platform's canonical `dispatch.capnp` contains `audioTts @12` and `audioStt @13`,
   but Gateway's vendored schema does not. The cross-repository contract gate fails;
   Gateway currently compiles only because its C++ request enum duplicates the
   numeric values by hand.
3. A no-database `dotnet test` reports 502/502, but 123 test methods directly return
   when `GREENFIELD_SCHEMA_CONNECTION` is absent. That run is useful unit evidence,
   not integration evidence.
4. The required Scheduler benchmark exits 1. All four cases produce no valid report
   because the benchmark Silo cannot resolve `ISlotLeaseStore`; both ordinary and
   greenfield CI invoke this broken gate.
5. Several features marked complete are explicit scaffolds: scheduled channel
   monitoring elects every process leader and simulates checks; quota refresh only
   rewrites seeded snapshots; scheduled backup never creates a backup; offsite
   backup marks an upload complete without transferring bytes.
6. The stress harness queries nonexistent PostgreSQL tables and can tolerate a dead
   child or settlement timeout, so its previous one-hour completion claim is invalid.
7. The Platform release workflow has no paired Gateway checkout and pushes four image
   families including `latest` before its later clean rebuild.
8. The nominal greenfield workflow invokes `verify-contracts.sh` without a Gateway
   path, so it never reaches the cross-repository comparison. A second tag-triggered
   Docker workflow can publish `latest` without any build/test/schema/contract gate,
   and `deploy/release.sh` tags both repositories after checking only Gateway's local
   digest while writing unexecuted tests/benchmarks/rebuilds as successful.
9. Realtime dispatch omits the initial WebSocket request body and raw-relays later
   client/Provider frames. Platform therefore evaluates empty request content and
   Gateway never runs the HTTP response-policy callback on realtime responses.
10. Gateway accepts request bodies up to 32 MiB, but Platform closes Cap'n Proto
    frames above 1 MiB and Gateway has no matching preflight. Large multipart/media
    input can fail before a durable scheduling decision.
11. Gateway emits `output_started` only after the first client write through a
    one-shot RPC whose failure is log-only, and its reporter deletes unacknowledged
    events classified non-retryable. The durable evidence contract is therefore
    weaker than the accounting lifecycle requires.
12. Gateway can keep a process alive after dependency or listener setup failure;
    `/ready` checks only the dispatch UDS, not Garnet, usage durability or every
    per-core listener. Its own tag workflow can also publish `latest` without a
    paired Platform, database, cross-repository contract or container gate.

## Repository snapshots

All three repositories were fetched with `git fetch --prune` before inspection.

| Repository | Authoritative ref | Worktree | Role |
| --- | --- | --- | --- |
| Platform | `master` / `origin/master@bc083d18c6b0ad9474df3d609527e0a2f72cf981` | Clean before documentation edits | C#/.NET 10, Orleans control plane, PostgreSQL authority, Admin/User APIs, Provider mock, two SolidJS applications, deployment and canonical contracts |
| Gateway | `master` / `origin/master@b6e4e02061074158159aaefd00d2bc7b44782e2a` | Clean | C++ edge, HTTP/WebSocket protocols, conversion, Provider transport, local SQLite usage outbox, vendored contracts |
| Sub2API research input | `origin/main@fbfdcef8184ae4b2e224d5cfc47cf1d0e3742710` (`v0.1.176-5-gfbfdcef81`) | Local `main@43ec48d`, clean, 1 ahead / 283 behind | Non-normative discovery input; never a compatibility runtime or automatic requirements source |

The Platform and Gateway remote tips already include the 2026-08-13 implementation
series that the old documents described as future work. Sub2API's `origin/main` did
not move after the prior fetch. Its separate `cla-signatures` branch moved and is
irrelevant to product requirements.

## Current surface

Counts below are reproducible static breadth signals, not completion percentages:

| Surface | Current count |
| --- | ---: |
| Platform tracked C# source files under `src` | 174 |
| Platform tracked C# test files | 89 |
| Platform xUnit `[Fact]` / `[Theory]` declarations | 466 |
| Platform direct Admin API `Map*` calls | 189 |
| Platform product SQL migration files | 65 (001-053, then 055-066; 054 is absent) |
| Platform Admin Web TS/TSX files | 34 |
| Platform User Web TS/TSX files | 26 |
| Gateway production C++ source/header files | 52 |
| Gateway test/benchmark sources | 12 |
| Gateway discovered CTest cases | 159 |
| Sub2API production Gin route registrations | 668 at pinned `origin/main`, excluding tests |
| Sub2API Ent schema directory | 42 Go files: 39 entities, 2 mixins and 1 test at pinned `origin/main` |
| Sub2API Vue files / TS files | 297 / 426 |
| Sub2API SQL migrations | 259 |

The 65-row feature inventory remains a useful scope index. Its status column now
means implementation maturity, not compatibility with Sub2API and not release
certification. See [feature-gap-report.md](feature-gap-report.md).

## Architecture that exists

The intended ownership model is sound and must remain fixed:

- Gateway owns client protocol parsing, bounded conversion, streaming/WebSocket
  lifecycle, Provider transport, target compilation inputs, retries and a durable
  local usage outbox. It does not own money, provider secrets, or product state.
- Platform owns identity, API keys, provider accounts, scheduling, immutable price
  snapshots, leases, holds, usage settlement, ledger effects, reconciliation,
  media metadata, policy, commercial state and operator/user APIs.
- PostgreSQL is the business and monetary authority. Orleans coordinates aggregates
  and concurrency; it is not a second ledger. Garnet is a rebuildable projection
  and cache. S3-compatible storage owns media bytes.
- Platform owns the single Cap'n Proto source. Gateway may vendor an identical copy
  for independent builds, but a contract revision is one atomic cross-repository
  change. No backward-compatibility branch is required for this greenfield product.

This architecture is described in [architecture.md](architecture.md). The current
schema drift is an implementation/release defect, not a reason to introduce
version negotiation or retain obsolete fields.

## Implemented capability groups

Static source inspection and current unit tests show substantial implementations:

- OpenAI Chat/Responses/Embeddings/Images/video/realtime routes, Anthropic Messages
  and token count, Gemini generation/catalogue, xAI/Grok-shaped text fixtures,
  search, TTS and STT routing, protocol conversion, stream terminal checks,
  cancellation and conservative unknown-charge handling.
- PostgreSQL-backed API-key policy, groups, account health, slot leases, request
  idempotency, decimal pricing snapshots, holds, ledger, usage/outbox settlement,
  subscription quota state and operator reconciliation.
- Password/session/OAuth/TOTP/Passkey flows, captcha/domain quota components,
  content policy, audit, configuration revisions, payments/refunds, subscriptions,
  redemption, referrals, announcements, exports and retention stores.
- Async media metadata, item ownership, S3 signing/reconciliation, passive-monitor
  rollups, backup/restore primitives, Admin Web and User Web page surfaces.
- A checked-in 2-Silo/2-Gateway Compose topology, smoke/fault/load scripts and
  separate Platform/Gateway CI files.

These statements describe source present at the pinned commits. Provider-specific
and operational claims still require the closure evidence below.

## Material incomplete or misleading slices

| Area | Current evidence | Required before `verified` |
| --- | --- | --- |
| Greenfield schema | Migration 053 succeeds, then 055 references nonexistent `users` / `api_keys`; 056 repeats those names | Make all 65 migrations apply twice to an empty PostgreSQL 17 database using only product-owned names; run the database suite after that exact schema |
| Contract release | Platform/Gateway `dispatch.capnp` differ at audio endpoints | Update canonical schema, Gateway vendor, generated C#, digests and both builds atomically; make CI compare sibling artifacts or a signed release manifest |
| Database tests | No-DB run is 502/502; 46 files inspect `GREENFIELD_SCHEMA_CONNECTION`, with 123 direct early returns | Report true skips or fail when integration prerequisites are required; publish database-enabled totals |
| Scheduler benchmark | Four Scheduler cases fail during Orleans activation because `ISlotLeaseStore` is not registered in the benchmark Silo | Register the production-equivalent dependency set; require valid reports and threshold assertions in both CI paths |
| Grok/xAI | Dedicated labels, catalogue/credential/quota storage and OpenAI-compatible goldens exist | Native account/OAuth/quota/media/search behavior and Provider-owned failure matrix; do not describe generic Bearer/OpenAI shape as full native xAI support |
| Realtime content policy | WebSocket dispatch sends no request body and raw-relays later text/binary frames | Define bounded request/response frame evaluation before Provider/client delivery; prove block/fail-closed/audit/settlement behavior |
| Request-body sizing | Gateway accepts 32 MiB; Platform RPC rejects frames above 1 MiB | Use a shared generated limit or send bounded metadata/object references; reject oversize input explicitly before lease/dispatch |
| Output/usage durability | `forwarded` is acknowledged before Provider I/O, but `output_started` occurs after client output with log-only failure; non-retryable unacknowledged usage is deleted | Queue every financially relevant transition durably; retain or incident unacknowledged evidence; prove crash/retry/restart convergence |
| Gateway startup/readiness | Construction can tolerate failed dependencies or bind/listen; `/ready` checks only dispatch UDS | Fail startup on unusable listeners; make readiness prove every required per-core listener, dispatch, Garnet and usage-durability dependency |
| Provider target enforcement | Auth headers are bounded, but target path is concatenated, unknown methods become POST, general headers are only hop-by-hop filtered, and TLS profile fields are decoded but unused | Validate a generated method/path/header/TLS contract before outbound I/O and reject unknown values |
| Models/Search edge behavior | Anonymous models returns HTTP 200 empty on Garnet failure; Search is registered stream-capable but not selected by the handler's chat-only streaming predicate | Distinguish unavailable from authoritative empty catalogues and make advertised Search streaming reachable or remove it |
| Initial setup | Sub2API research has a setup status/dependency/install UI, while the selected ScalaAPI inventory previously folded bootstrap into deployment | Decide and implement one product-native dependency/first-admin bootstrap contract under DEP-03; never import reference state/default secrets |
| Upstream error policy | The research input has durable rewrite/pass-through/monitor-suppression rules; ScalaAPI has protocol error normalization but no explicit equivalent product decision | Define the safe ScalaAPI-native error exposure/redaction/monitoring contract under GW-05/SEC-01, or record it out of scope |
| In-app update | `/admin/system/update` fetches a manifest and returns “downloaded” without downloading or installing bytes | Remove/disable the endpoint under paired immutable deployment, or integrate it with a verified external deployment controller; never report false success |
| Search | Routes, mock, history store and price unit exist | Fix schema names; implement/live-prove real Web/X adapters, settlement and history authorization |
| TTS/STT/voices | Gateway validation, mock, stores and pricing unit exist | Fix contract/schema drift, multipart/audio transport and object lifecycle E2E; prove character/time settlement and owner isolation |
| Provider quota | PostgreSQL CAS store and scheduler input exist | Refresh worker must enumerate real accounts and call bounded provider adapters; current worker only bumps seeded rows |
| Active channel monitor | Tables, APIs, retry shell and UI exist | Real distributed leadership and actual bounded provider probes; current worker sets `IsLeader = true` in every process and simulates success |
| Backup/offsite | Local pg_dump/restore, checksum, crypto/key and policy primitives exist | Scheduler must create a backup; offsite path must transfer and verify bytes. Current upload records success without I/O |
| Long stress | Load/fault scripts exist | Repair script/schema mismatches, run the actual 3600-second gate, retain commit/image/metrics artifacts and prove cleanup |
| Browser workflows | Both Web builds pass; some Playwright paths exist in smoke | Run authenticated mutation/authorization/error workflows against the source-built stack in blocking CI |
| Hosted/local release | Greenfield CI omits the Gateway argument; `docker.yml` publishes directly on tags; `release.yml` publishes before clean rebuild; `deploy/release.sh` verifies only Gateway-local digest then records unexecuted gates as passed | Use one non-bypassable paired workflow; run all gates before tags/images; generate reports only from captured command results |

## Current verification

Commands were run on 2026-08-14 against the exact refs above:

| Check | Result | Interpretation |
| --- | --- | --- |
| Gateway Release build + CTest | PASS, 159/159 | Current C++ unit/protocol surface is green |
| Gateway benchmark smoke | PASS, 16 routines | Current microbenchmark execution only |
| Platform Release build | PASS, 0 warnings/errors | Current compile evidence |
| Platform `dotnet test ... -c Release` without DB | PASS, 502/502 (80 Grain, 258 Host, 65 Admin, 99 Provider Mock) | Unit/non-DB evidence only; direct early returns make it unfit as integration proof |
| Platform Scheduler benchmark Dry run | FAIL | Four cases have no valid report because `ISlotLeaseStore` cannot be resolved |
| Admin Web typecheck + build | PASS | Current front-end source compiles |
| User Web typecheck + build | PASS | Current front-end source compiles |
| Gateway repository-local schema digest | PASS | Vendored files match Gateway's internally consistent but cross-repo-stale digest |
| Platform retired-dependency scan | PASS | No Sub2API/Redis/CDC compatibility dependency found in the scanned runtime paths |
| Platform-to-Gateway contract comparison | FAIL | `dispatch.capnp` differs at audio endpoint enum values |
| Empty PostgreSQL 17 migration pass | FAIL | 000 plus 001-053 commit; 055 fails with missing `users`; second run and DB tests cannot start |
| Release workflow source audit | FAIL | Platform and Gateway have independent tag publishers; publication can precede clean rebuild and no job proves the paired product |
| Greenfield/release-script source audit | FAIL | Cross-repo comparison is skipped; alternate publish paths lack paired gates; release report contains claims the script never executes |
| Runtime Compose, browser E2E, live Providers, 3600-second stress | NOT RUN | Script presence and historical logs are not current evidence |

See [verification.md](verification.md) for command-level details and retained
historical evidence rules.

## Non-compatibility invariants

The following are product requirements, not optional migration preferences:

1. Bootstrap only from an empty product schema. Never import Sub2API rows,
   migration history, CDC outboxes, Redis state, IDs, hashes, credentials or keys.
2. Define product-native endpoints, DTOs, errors and state machines. Similar public
   provider protocols may be supported because clients need them, but Sub2API's
   private/Admin API is not a contract.
3. Replace the single internal contract when needed. Do not add deprecated aliases,
   dual-read/write, legacy route shims or version negotiation for an unreleased
   greenfield design.
4. Keep monetary decisions in PostgreSQL and immutable lease evidence. Never claim
   capability coverage by copying Sub2API billing state or by adding a second authority.
5. Research may suggest a capability, but only an explicit ScalaAPI product decision
   creates scope. Accept it only against a native contract and its own evidence.

## Release posture

The current status is **implementation in progress, release blocked**. The next
work is evidence-driven repair, not compatibility work: fix the greenfield schema
and contract drift first, make CI truthful, then close the scaffolded Provider and
operations slices in dependency order. The active order is maintained in
[next-stage-plan.md](next-stage-plan.md) and the risk controls in
[risk-register.md](risk-register.md).
