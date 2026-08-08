# ScalaAPI Rewrite Current State

This document is the active baseline for the new ScalaAPI product as of
2026-08-09. ScalaAPI reimplements the useful product capabilities catalogued in
Sub2API, but it does not preserve Sub2API APIs, internal contracts, schemas, IDs,
keys, state mappings, deployment processes, or data. The Sub2API repository is a
read-only requirements reference and is excluded from builds and runtime.

## Source snapshot

| Repository | Commit | Worktree | Role |
| --- | --- | --- | --- |
| `gateway` | `be90413` | clean | C++ HTTP/WebSocket edge, protocol parsing/conversion, streaming, strict Provider media contracts, transport/evidence, charge-aware failover, durable usage delivery, authenticated Garnet projections, deterministic fault boundaries, and fail-closed cancellation/partial-SSE handling |
| `platform` | `e5c2fb8` | clean | C# Orleans control plane, PostgreSQL accounting/product authority and reconciliation, identity, scheduling, evidence-backed leases/holds/ledger, audited operator resolution, media lifecycle, Admin API/Web, Provider mock, migrations, deterministic fault boundaries, and deployment gates |
| `sub2api` | `43ec48d` | read-only clean | Requirements catalogue only; never a runtime or compatibility dependency |

The current tracked inventory is:

- Gateway: 52 production C++ source/header files, 10 test source files, and 98
  CTest cases.
- Platform: 81 hand-written production C# files, 3 generated Cap'n Proto C#
  files, 26 test/benchmark C# files, and 95 tests: 57 Grain, 28 Host, 4 Admin,
  and 6 Provider mock tests.
- Product surface: 116 direct Admin API route declarations, 43 product tables,
  20 SQLSugar entity types, 23 Admin Web TypeScript/TSX files, and 11 page views.
- Reference scope: approximately 612 Sub2API route registrations, 39 concrete
  Ent schemas, 82 Vue view/component files, and 240 migrations. These are scope
  signals, not parity percentages or migration targets.

The 58-domain inventory remains 2 `implemented`, 37 `partial`, 10 `skeleton`,
and 9 `missing`. A route, table, mock response, or manual probe does not promote a
domain; promotion requires a defined contract/state machine, automated tests, and
current-source runtime evidence.

## Architecture now implemented

- Gateway and Platform are independent repositories joined by one revision-3
  Cap'n Proto contract. Platform owns the canonical schema; Gateway vendors an
  identical copy. Digest and deterministic C# generation gates reject drift.
- PostgreSQL is authoritative for product and accounting state. Orleans
  coordinates aggregate concurrency. `entity_registry`, rather than Orleans
  storage internals, is used for business discovery and administrative listing.
- Each user has one `accounting_accounts` row containing an authoritative NUMERIC
  posted balance and monotonically increasing ledger version. Every current
  money-mutating path uses the same per-user transaction lock and append rule;
  Orleans holds only a versioned projection and cannot authorize spending.
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
- Gateway durably changes a lease from `held` to `forwarded` before opening HTTP or
  realtime Provider transport. The first response bytes successfully written to a
  streaming client record `output_started`. If evidence persistence fails before
  transport, Gateway fails closed without contacting the Provider.
- Only an actual Provider response with a 4xx/5xx status proves an explicit
  no-charge rejection and permits release/failover. Transport loss, a synthesized
  502, malformed usage, conversion failure, and media persistence failure are
  unknown-charge outcomes and do not fail over.
- Successful payload-bearing 2xx responses are checked before usage extraction.
  Body read failure, an empty 2xx payload, or malformed JSON becomes a retryable
  protocol error; an upstream disconnect can no longer escape as a zero-token
  200 response. Streaming requests reject a non-SSE success before client output.
  SSE completion now requires the source protocol's terminal event (`[DONE]`,
  `message_stop`, `response.completed`, or a finish reason); EOF/timeout before
  that event is an incomplete Provider stream and retains the charge hold. A
  client write returning zero or an error is treated as cancellation, never
  retried, and records a bounded disconnect/cancellation reason for usage
  evidence.
- Gateway's local usage outbox survives restart, replays retryable reports, and
  retires non-retryable terminal reports instead of blocking forever.
- Gateway and Platform expose opt-in, one-shot deterministic fault hooks at
  dispatch, Provider completion, settlement commit, and outbox acknowledgement.
  Hooks persist a claim marker so a restarted process does not crash repeatedly.

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

- Lease creation checks the SQL-authoritative posted balance minus active holds,
  then transactionally creates a `held` lease, durable balance hold, idempotency
  record, and immutable lease event. Its strict state machine is `held -> forwarded
  -> output_started -> completed`, with terminal `aborted`, `expired`, or
  `reconciliation_needed` branches. Completion transactionally records usage, a
  unique versioned `usage_debit`, terminal lease/hold state, and outbox records.
  Explicit no-charge abort releases idempotently; unknown abort preserves the hold,
  blocks redispatch, and emits reconciliation evidence. TTL releases only a
  never-forwarded `held` lease. Expired `forwarded` or `output_started` work enters
  `reconciliation_needed` and still accepts one late usage completion.
- Completed non-stream requests persist a bounded response for exact replay.
  Matching settled requests return the stored response without a second lease or
  debit; active duplicates and fingerprint conflicts are deterministic.
- Settlement outbox claims expire and can be reclaimed after process failure.
  Financial effects use stable IDs and bounded retry rather than silent loss.
- User create/configuration contracts cannot set balance. Administrative credits,
  payment credits/refunds, redeem bonuses, and usage debits append through one
  `AccountingStore`; stable effect identity provides exact replay/conflict
  semantics and every accepted effect advances one per-user ledger version.
  Administrative debits additionally reject active-hold overdraft and persist an
  actor/reason audit. A latest-snapshot SQL outbox retries Orleans projection;
  stale versions cannot overwrite a newer Grain balance.
- A globally serialized scheduled reconciler checks account balance/version and
  ledger contiguity, usage/debit equality, lease/hold terminal state, and Grain
  projection state. It repairs only provably safe terminal holds and stale
  projections; unsafe mismatches and unknown charges persist as incidents. Admin
  exposes run/incident queries and Platform exposes open-count, unknown-charge,
  oldest-age, and last-success metrics.

### Provider mock, media, and commercial foundations

- The source-owned Provider mock implements deterministic OpenAI, Anthropic, and
  Gemini JSON/SSE paths, models, embeddings, token count, sync media, pollable
  image/video tasks, and faults for 429, 500, timeout, disconnect, malformed usage,
  invalid stream content, and a delayed stream used to prove downstream client
  cancellation.
- Normalized OpenAI Chat input can select a fault without private headers. A
  protected seed endpoint creates seven independent fault accounts/groups so one
  scheduler cooldown cannot mask another scenario.
- Media polling copies Provider bytes to S3-compatible storage and persists object
  ownership metadata. Signed downloads, output deletion, and terminal operation
  deletion work; reconciliation, restore, and restart coverage remain open.
- Signed payment webhooks, order paid/refunded transitions, stable ledger effects,
  pending-event recovery, subscription purchase/cancel/renew/expiry, and
  transactional redeem-code effects exist as partial commercial foundations.

### Bootstrap and deployment

- The active migrator applies Orleans support plus migrations 001-021 to an empty
  PostgreSQL database and rejects checksum drift. A second execution skips all 22
  files. No source database, snapshot, old key, CDC table, or compatibility mapping
  is required.
- `deploy/stack` independently starts PostgreSQL, authenticated Garnet, MinIO,
  Provider mock, Platform, Gateway, Admin API, and Admin Web. Image digests pin the
  infrastructure services.
- The stack uses `restart: on-failure`; the Podman Compose 1.6 smoke harness also
  explicitly starts the exited Platform container when a fault hook is enabled.
  `Orleans:SingleSiloRecovery` is an explicit development-only mode which retires
  stale membership rows before a replacement silo joins; multi-silo defaults remain
  unchanged.
- CDC consumers, Debezium configuration, migration fences/write gates, cutover
  endpoints, and CDC-only product tables are absent from active runtime code.
  `docs/migration/README.md` is only a pointer to historical material under
  `docs/archive/migration`.

## Current verification evidence

At Platform `e5c2fb8` and Gateway `be90413`:

- Gateway built locally and passed 99/99 CTest cases, including deterministic
  fault-hook claim/repeat behavior, terminal SSE detection, provider EOF
  classification, incomplete chunked-body disconnect classification, and
  zero-length client-write cancellation.
- Platform Release test/build passed with 0 warnings and 0 errors: 95/95 tests,
  including 28 Host tests against a fresh real PostgreSQL schema. Host coverage
  includes deterministic fault-hook configuration plus atomic operator
  settle/release, replay/conflict behavior, and concurrent resolution serialization.
- Admin Web typecheck and production build passed.
- Scheduler benchmark integrity dry run executed all 4 selected child benchmarks
  and returned zero. It is a failure-propagation check, not performance evidence.
- `deploy/stack/smoke.sh` built current sibling sources in the isolated Podman
  project `scalaapi-smoke-contenttype-0809`, created new volumes, applied all 22
  migrations, and observed all 22 skip on the second migrator run. Source-built
  image IDs were Platform `c08eb3863319`, Admin API `ec196250cbb2`, Gateway
  `74834536bd68`, Provider mock `5c1554c552a9`, migrator `99c9d1252d75`, and
  Admin Web `17f11657502d`. The smoke intentionally crashed Platform once at
  `platform.after_settlement_commit`; single-silo membership recovery restarted
  the same service, replayed the durable usage outbox, and preserved one debit.
  It separated explicit non-stream and streaming 429/500 rejections from eight
  unknown-charge scenarios,
  including Provider disconnect, disconnect-before-output, malformed usage,
  timeout, partial SSE disconnect, invalid streaming content type, and a real
  downstream client timeout after the first SSE event. It resolved one incident through the Admin API with
  `settle`, replayed the same command as `duplicate`, and reduced open incidents
  from eight to seven before the second reconciliation run.
- The clean-stack Admin API funded a new zero-balance user once. Exact replay
  returned the same ledger identity, changed replay returned 409, overdraft returned
  409, and PostgreSQL contained exactly one NUMERIC adjustment and one actor audit.
- The empty-stack Chat request settled with one completed lease, one committed
  hold, one usage effect, one versioned NUMERIC debit, and drained Platform,
  accounting-projection, and Gateway outboxes. Exact response replay produced no
  second charge. SQL assertions proved posted balance equals ledger sum and every
  user ledger version is contiguous and unique.
- The real-database reconciliation test corrupted an account and terminal hold,
  repaired only safe hold/projection drift, preserved an unknown charge and active
  hold, accepted late settlement, and resolved both incidents on the next run. The
  stack gate intentionally ended with three open unknown-charge incidents, so the
  comprehensive reconciliation result was `failed` rather than falsely reporting a
  clean account boundary.
- Platform and Gateway were independently replaced; a fresh billable request after
  each replacement settled once.
- Independent 500 and 429 scenarios produced explicit Provider rejections: four 500
  attempts and one 429 attempt ended `aborted` with released holds and no debit.
  Malformed-success, upstream-disconnect, and timeout each made one attempt, did not
  fail over, ended `reconciliation_needed`, retained the hold/idempotency key, and
  created one operator-visible incident without a usage debit. Streaming Provider
  disconnect, disconnect-before-output, malformed-usage, invalid-content-type, and
  downstream client cancellation scenarios did the same; each retained the hold
  with no usage or debit. The client-cancellation request used a short-lived curl, received the
  first SSE event, closed before the delayed second write, and returned transport
  status 000 while the Gateway recorded unknown charge evidence. Streaming 500
  exhausted four accounts and streaming 429 exhausted one account; both returned
  public 503/provider_unavailable responses and released every no-charge hold.
- Garnet authentication returned `PONG`; asynchronous media bootstrapped the empty
  MinIO bucket and a signed URL downloaded the expected 67-byte object.
- The smoke command exited zero and its cleanup trap removed only its containers and
  temporary volumes. The exact `scalaapi-smoke-contenttype-0809_*` image tags were
  then removed explicitly; no project container or temporary image remained. The host
  retained only the three named `apitf_*` baseline volumes and the Garnet base image.

Detailed gate results and residual coverage are maintained in `verification.md`.

## Known gaps

- PostgreSQL is the only monetary authority and periodic reconciliation now uses
  persisted held/forwarded/output-started evidence to classify expiry and aborts.
  Admin operators can resolve an open unknown-charge incident exactly once through
  an audited, idempotent `settle` or evidence-gated `release` command; subscription
  quota grants and future affiliate effects still need explicit authority contracts.
- Gateway now classifies client cancellation and incomplete SSE as unknown-charge
  outcomes, records disconnect/cancellation reasons, and prevents failover after
  output or partial Provider output. The source-level behavior is covered by 98
  CTest cases; the empty-stack gate now proves Provider partial-SSE disconnect,
  disconnect-before-output, and malformed-usage retention with no usage/debit.
  The empty-stack gate now proves actual downstream socket cancellation as well;
  final usage/reconciliation fixtures for a truncated stream remain.
  A direct
  transport reset currently returns 502 while scheduler exhaustion after the same
  reset can return 503; the next error-contract slice must normalize the public
  status and body.
- One source smoke proves the Platform post-settlement-commit crash boundary and
  replay. The remaining dispatch, Provider-completion, pre-commit, Gateway, and
  outbox-acknowledgement hook matrix still needs independent runtime assertions,
  including hold/idempotency reconciliation and multi-instance recovery.
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
