# ScalaAPI Rewrite Current State

This document is the active baseline for the new ScalaAPI product as of
2026-08-08. ScalaAPI reimplements the useful product capabilities catalogued in
Sub2API, but it does not preserve Sub2API APIs, internal contracts, schemas, IDs,
keys, state mappings, deployment processes, or data. The Sub2API repository is a
read-only requirements reference and is excluded from builds and runtime.

## Source snapshot

| Repository | Commit | Worktree | Role |
| --- | --- | --- | --- |
| `gateway` | `dc69269` | clean | C++ HTTP/WebSocket edge, protocol parsing/conversion, streaming, Provider transport, failover, durable usage delivery, and authenticated Garnet projections |
| `platform` | `cea9519` | clean | C# Orleans control plane, PostgreSQL authority, identity, scheduling, leases/holds/ledger, media lifecycle, Admin API/Web, Provider mock, migrations, and deployment gates |
| `sub2api` | `43ec48d` | read-only clean | Requirements catalogue only; never a runtime or compatibility dependency |

The current tracked inventory is:

- Gateway: 50 production C++ source/header files, 9 test source files, and 91
  CTest cases.
- Platform: 70 hand-written production C# files, 3 generated Cap'n Proto C#
  files, 20 test/benchmark C# files, and 89 tests: 58 Grain, 22 Host, 3 Admin,
  and 6 Provider mock tests.
- Product surface: 143 direct Admin API route declarations, 34 product tables,
  20 SQLSugar entity types, 23 Admin Web TypeScript/TSX files, and 11 page views.
- Reference scope: approximately 612 Sub2API route registrations, 39 concrete
  Ent schemas, 82 Vue view/component files, and 240 migrations. These are scope
  signals, not parity percentages or migration targets.

The 58-domain inventory remains 2 `implemented`, 37 `partial`, 10 `skeleton`,
and 9 `missing`. A route, table, mock response, or manual probe does not promote a
domain; promotion requires a defined contract/state machine, automated tests, and
current-source runtime evidence.

## Architecture now implemented

- Gateway and Platform are independent repositories joined by one revision-1
  Cap'n Proto contract. Platform owns the canonical schema; Gateway vendors an
  identical copy. Digest and deterministic C# generation gates reject drift.
- PostgreSQL is authoritative for product and accounting state. Orleans
  coordinates aggregate concurrency. `entity_registry`, rather than Orleans
  storage internals, is used for business discovery and administrative listing.
- Garnet is the only distributed cache/projection service. Both products use
  authenticated external TCP clients with optional TLS and no embedded RESP
  server, Microsoft.Garnet package, Redis process, image, or fallback.
- S3-compatible storage owns media bytes. PostgreSQL owns media metadata,
  authorization, object keys, ETags, sizes, and lifecycle state.
- All business money is `decimal`; PostgreSQL uses `NUMERIC`; the RPC boundary
  uses signed integers at a documented 1e8 fixed scale. No billing path relies on
  binary floating point.

## Product capabilities now present

### Gateway and Provider transport

- Capability-driven routes cover OpenAI Chat/Responses/Embeddings/Images/video,
  Anthropic Messages and token counting, Gemini generation, model discovery,
  realtime WebSocket entry points, and approved media subresources.
- OpenAI, Anthropic, and Gemini JSON/SSE normalization and cross-protocol
  conversion exist, including OpenAI, Gemini, and nested Anthropic streaming
  usage extraction.
- Non-stream Provider requests have a 30-second boundary and bounded retries.
  Failover retains the public idempotency key while allocating a unique internal
  request/lease ID per attempt.
- Successful payload-bearing 2xx responses are checked before usage extraction.
  Body read failure, an empty 2xx payload, or malformed JSON becomes a retryable
  protocol error; an upstream disconnect can no longer escape as a zero-token
  200 response. Streaming requests reject a non-SSE success before client output.
- Gateway's local usage outbox survives restart, replays retryable reports, and
  retires non-retryable terminal reports instead of blocking forever.

### Identity and control plane

- Registration, password login, OAuth identity records, rotating hashed refresh
  sessions, logout, per-session revoke, password reset, email verification,
  profile/password update, and password-confirmed soft account deletion exist.
- API keys support create, list, rotate, revoke, hash-only persistence, registry
  projection, absolute quota, and independent 5-hour/day/week spend windows.
- Users, groups, Provider accounts, encrypted credentials, scheduling, sticky
  routing, rate/concurrency policy, and versioned pricing are represented as
  Orleans aggregates with PostgreSQL operational records where implemented.
- Admin can publish/close effective price versions. New leases snapshot version
  identity and every NUMERIC unit rate, so mutable configuration cannot reprice an
  existing request.

### Billing and idempotency

- Lease creation transactionally creates an `active` durable balance hold and a
  request-idempotency record. Completion transactionally records usage, a unique
  `usage_debit`, terminal lease/hold state, and an outbox record. Abort and expiry
  release holds idempotently.
- Completed non-stream requests persist a bounded response for exact replay.
  Matching settled requests return the stored response without a second lease or
  debit; active duplicates and fingerprint conflicts are deterministic.
- Settlement outbox claims expire and can be reclaimed after process failure.
  Financial effects use stable IDs and bounded retry rather than silent loss.
- Admin exposes lease, hold, ledger, usage, and reconciliation queries. Clean-seed
  reconciliation passes, while historical repair automation remains incomplete.

### Provider mock, media, and commercial foundations

- The source-owned Provider mock implements deterministic OpenAI, Anthropic, and
  Gemini JSON/SSE paths, models, embeddings, token count, sync media, pollable
  image/video tasks, and faults for 429, 500, timeout, disconnect, malformed usage,
  and invalid stream content.
- Normalized OpenAI Chat input can select a fault without private headers. A
  protected seed endpoint creates five independent fault accounts/groups so one
  scheduler cooldown cannot mask another scenario.
- Media polling copies Provider bytes to S3-compatible storage and persists object
  ownership metadata. Signed downloads, output deletion, and terminal operation
  deletion work; reconciliation, restore, and restart coverage remain open.
- Signed payment webhooks, order paid/refunded transitions, stable ledger effects,
  pending-event recovery, subscription purchase/cancel/renew/expiry, and
  transactional redeem-code effects exist as partial commercial foundations.

### Bootstrap and deployment

- The active migrator applies Orleans support plus migrations 001-016 to an empty
  PostgreSQL database and rejects checksum drift. A second execution skips all 17
  files. No source database, snapshot, old key, CDC table, or compatibility mapping
  is required.
- `deploy/stack` independently starts PostgreSQL, authenticated Garnet, MinIO,
  Provider mock, Platform, Gateway, Admin API, and Admin Web. Image digests pin the
  infrastructure services.
- CDC consumers, Debezium configuration, migration fences/write gates, cutover
  endpoints, and CDC-only product tables are absent from active runtime code.
  `docs/migration/README.md` is only a pointer to historical material under
  `docs/archive/migration`.

## Current verification evidence

At Platform `cea9519` and Gateway `dc69269`:

- Gateway built locally and passed 91/91 CTest cases.
- Platform Release test/build passed with 0 warnings and 0 errors: 89/89 tests.
- Admin Web typecheck and production build passed.
- Scheduler benchmark integrity dry run executed all 4 selected child benchmarks
  and returned zero. It is a failure-propagation check, not performance evidence.
- `deploy/stack/smoke.sh` built current sibling sources in the isolated Podman
  project `scalaapi-smoke-fault1`, created new volumes, applied all 17 migrations,
  and observed all 17 skip on the second migrator run.
- The empty-stack Chat request settled with one completed lease, one committed
  hold, one usage effect, one NUMERIC debit, and drained Platform/Gateway outboxes.
  Exact response replay produced no second charge.
- Platform and Gateway were independently replaced; a fresh billable request after
  each replacement settled once.
- Independent 500, 429, malformed-usage, upstream-disconnect, and timeout scenarios
  all passed. Every attempted lease ended `aborted`, every hold was `released`, and
  each scenario produced zero usage events, usage logs, request logs, and ledger
  entries plus one aborted idempotency record.
- Garnet authentication returned `PONG`; asynchronous media bootstrapped the empty
  MinIO bucket and a signed URL downloaded the expected 67-byte object.
- The smoke command exited zero and its cleanup trap removed only its containers and
  temporary volumes. No project container remained afterward.

Detailed gate results and residual coverage are maintained in `verification.md`.

## Known gaps

- PostgreSQL and Orleans still split parts of business authority. Administrative
  funding and some commercial balance mutations must move behind append-only,
  idempotent PostgreSQL ledger effects and aggregate repositories.
- Upstream disconnect is now covered for non-stream OpenAI Chat, but actual client
  cancellation, partial SSE output, unknown Provider billing after cancellation,
  and protocol-wide fault semantics are not closed.
- Process replacement after clean requests passes. Crashes precisely between
  dispatch, Provider completion, usage report, SQL commit, and outbox acknowledgement
  still need deterministic injection and hold/idempotency reconciliation.
- Garnet authentication, outage/reconnect, rebuild, and invalidation flush have
  evidence; TLS plus concurrent multi-Gateway/multi-Silo behavior is not a release
  gate yet.
- Hosted CI cannot currently check out the private sibling repository with the
  default per-repository token. The local cross-repository smoke must become a
  blocking release workflow with a read-only checkout boundary.
- Provider adapters beyond the mock, protocol golden fixtures, User Web, browser
  tests, TOTP hardening, Passkeys, full commercial coupling, audit/observability,
  HA, load/soak, backup/restore, and signed rollback remain partial or missing.
- Admin Web has a blocking type/build gate but no browser runner. There is no User
  Web implementation.

## Historical boundary and acceptance rule

Old containers, historical databases, `/var/run/sub2api`, old image IDs, and manual
long-lived stack observations are not release evidence. New evidence must record the
current commits or worktree, source-built images, empty environment shape, and top
level exit code.

A capability is accepted only for the new ScalaAPI contract and state machine. No
test may require Sub2API data, IDs, keys, internal APIs, database layout, or behavior
compatibility.
