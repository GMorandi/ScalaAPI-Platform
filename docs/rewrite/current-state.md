# ScalaAPI Rewrite Current State

This document is the active baseline for the new ScalaAPI product as of
2026-08-09. ScalaAPI reimplements the useful product capabilities catalogued in
Sub2API, but it does not preserve Sub2API APIs, internal contracts, schemas, IDs,
keys, state mappings, deployment processes, or data. The Sub2API repository is a
read-only requirements reference and is excluded from builds and runtime.

## Source snapshot

| Repository | Commit | Worktree | Role |
| --- | --- | --- | --- |
| `gateway` | `297b131` | clean | C++ HTTP/WebSocket edge, protocol parsing/conversion, streaming, strict Provider media contracts, bounded stream header/client timeouts, distinct inter-chunk/total timer tests, normalized Provider availability errors, transport/evidence, charge-aware failover, durable usage delivery, authenticated Garnet projections, deterministic fault boundaries, and late-usage settlement from truncated SSE |
| `platform` | `a0ea559` | clean | C# Orleans control plane, PostgreSQL accounting/product authority and reconciliation, identity, scheduling, evidence-backed leases/holds/ledger, audited operator resolution, media lifecycle, Admin API/Web, Provider mock, migrations, deterministic Platform/Gateway fault boundaries, and Garnet deployment gates |
| `sub2api` | `43ec48d` | read-only clean | Requirements catalogue only; never a runtime or compatibility dependency |

The current tracked inventory is:

- Gateway: 52 production C++ source/header files, 10 test source files, and 102
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
  Streaming Provider calls now bound the response-header wait by the first-token
  deadline and extend the incoming client socket for the configured stream window;
  separate inter-chunk and total-stream timers are independently enforced and
  unit-tested. Failover retains the
  public idempotency key while allocating a unique internal request/lease ID per
  attempt.
- Gateway durably changes a lease from `held` to `forwarded` before opening HTTP or
  realtime Provider transport. The first response bytes successfully written to a
  streaming client record `output_started`. If evidence persistence fails before
  transport, Gateway fails closed without contacting the Provider.
- Only an actual Provider response with a 4xx/5xx status proves an explicit
  no-charge rejection and permits release/failover. Transport loss is exposed as
  `503/provider_unavailable`; bounded timeouts, malformed payloads, conversion
  failure, and media persistence failure use `502/provider_protocol_error` or a
  more specific 502 contract. All remain unknown-charge outcomes and do not fail
  over.
- Successful payload-bearing 2xx responses are checked before usage extraction.
  A Provider connection reset before headers, during a non-stream body, or during
  an incomplete SSE is `503/provider_unavailable`; bounded timeout and malformed
  protocol cases remain `502/provider_protocol_error`. An upstream disconnect can
  no longer escape as a zero-token 200 response. Streaming requests reject a
  non-SSE success before client output.
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
  Hooks persist a claim marker so a restarted process does not crash repeatedly;
  the smoke harness proves Platform pre-commit, post-commit, and pre-ack recovery
  plus Gateway termination after Provider completion with the ambiguous lease
  retained for reconciliation.

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
  invalid stream content, downstream client cancellation, and a truncated stream
  that emits usage before EOF.
- Normalized OpenAI Chat input can select a fault without private headers. A
  protected seed endpoint creates nine independent fault accounts/groups so one
  scheduler cooldown cannot mask another scenario; account credentials also pin
  the mock scenario header so the fault matrix is independent of request-body
  conversion.
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

At Platform `a0ea559` and Gateway `297b131`:

- Gateway built locally and passed 102/102 CTest cases, including deterministic
  fault-hook claim/repeat behavior, terminal SSE detection, provider EOF
  classification, incomplete chunked-body disconnect classification, zero-length
  client-write cancellation, and bounded Provider pre-header stream timeout
  handling plus independent inter-chunk and total-stream timeout scenarios.
- Platform Release test/build passed with 0 warnings and 0 errors: 95/95 tests,
  including 28 Host tests against a fresh real PostgreSQL schema. Host coverage
  includes deterministic fault-hook configuration plus atomic operator
  settle/release, replay/conflict behavior, and concurrent resolution serialization.
- Admin Web typecheck and production build passed.
- The current-source empty-stack project `scalaapi-gateway-recovery-0907` ran with
  `GATEWAY_FAULT_HOOK=gateway.after_provider_completion` and a 15-second lease TTL.
  Gateway returned an empty transport reply, persisted its one-shot marker,
  terminated, and was explicitly started as the same container. Readiness
  recovered, the original lease became `reconciliation_needed` with an active
  hold and no usage/debit, and the marker prevented a repeat crash. The complete
  gate then passed with ten unknown-charge incidents, one audited operator settle,
  and nine remaining open incidents. The cleanup trap removed the temporary
  project; only the named `apitf_*` development resources remain.
- Scheduler benchmark integrity dry run executed all 4 selected child benchmarks
  and returned zero. It is a failure-propagation check, not performance evidence.
- `deploy/stack/smoke.sh` built current sibling sources in isolated Podman
  project `scalaapi-smoke-0830`, created new volumes, applied all 22 migrations,
  and observed all 22 skip on the second migrator run. Image IDs were Platform
  `09a9a0871f09318b414b1fbca2a4de0733b7a2189f7541da12dd8cfa5a6424cf`, Admin API
  `2734567850bfa0df74f099a6c2346465a6d4336d7cfbc0feb4d5dcfce4472eb9`, Gateway
  `6ffc8dd0a8bf7e7597de9cd98a0fe044896565a54a7f5a25f4478dd769b06826`, Provider
  mock `38f69d466c88ab33b1e14f9a9647e0c6c520fc8f48ad67aa27f91b45233ce2e5`,
  migrator `d8c9db33a784faefdf5b3c9dc1c08e50c236554b70e4f4f081eda5a7630090c4`,
  and Admin Web `dfb8a98fe1a8dc32c8c7721743eddcee86dd2f042c4218cb6a9c9c713db02ba2`.
  The smoke intentionally crashed Platform once before settlement commit; the
  harness observed the exit, explicitly started the same container, replayed the
  Gateway usage outbox after reconnect, and preserved one debit. It also replaced
  Platform and Gateway independently, separated explicit non-stream and streaming
  429/500 rejections from nine unknown-charge scenarios, including Provider
  disconnect, disconnect-before-output, malformed usage, timeout before response
  headers, partial SSE disconnect, invalid streaming content type, and a real
  downstream client timeout after the first SSE event. The pre-header timeout
  returned bounded HTTP 502 with `provider_protocol_error`, retained one hold, and
  did not fail over. Direct non-stream and zero-output streaming resets returned
  HTTP 503 with `provider_unavailable`; partial SSE resets are retained as
  unknown-charge even when the client observes a transport-level 000. One incident was settled through the Admin API, replayed as
  `duplicate`, and the next reconciliation retained eight remaining open
  unknown-charge incidents. A dedicated `disconnect_after_usage` stream returned
  HTTP 200 while the Provider ended before `[DONE]`; the Gateway outbox and
  Platform completion transaction recorded exactly one usage event, usage log,
  committed hold, NUMERIC debit, and completed idempotency row.
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
  stack gate intentionally ended with eight open unknown-charge incidents, so the
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
  temporary volumes. No `scalaapi-smoke-0830` container, volume, network, or tagged
  image remained after the run. The host retained only the named `apitf_*` baseline
  volumes and baseline images needed for this development machine.

Detailed gate results and residual coverage are maintained in `verification.md`.

## Known gaps

- PostgreSQL is the only monetary authority and periodic reconciliation now uses
  persisted held/forwarded/output-started evidence to classify expiry and aborts.
  Admin operators can resolve an open unknown-charge incident exactly once through
  an audited, idempotent `settle` or evidence-gated `release` command; subscription
  quota grants and future affiliate effects still need explicit authority contracts.
- Gateway now classifies client cancellation and incomplete SSE as unknown-charge
  outcomes, records disconnect/cancellation reasons, and prevents failover after
  output or partial Provider output. The source-level behavior is covered by 102
  CTest cases; the empty-stack gate now proves Provider partial-SSE disconnect,
  disconnect-before-output, malformed-usage retention, and bounded pre-header
  timeout handling with no usage/debit.
  The empty-stack gate now proves actual downstream socket cancellation as well;
  Gateway commit `297b131` now preserves valid Provider usage observed before
  truncated SSE EOF and settles it through the existing durable outbox path.
  Gateway commit `6c43e5d` normalizes Provider
  connection resets and scheduler exhaustion to `503/provider_unavailable`; bounded
  timeout and malformed protocol cases remain `502/provider_protocol_error`; the
  timer distinctions are pinned by Gateway `18083f9`.
- Source smoke now proves Platform pre-settlement-commit, post-settlement-commit,
  and pre-outbox-acknowledgement crash boundaries, Gateway reconnect/backoff
  recovery, durable usage replay, and exactly-once settlement. The current source
  gate additionally proves Gateway termination after Provider completion and
  retention of its forwarded lease/hold for reconciliation; dispatch/worker
  reclaim, other Gateway boundaries, and multi-instance hook assertions remain.
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
