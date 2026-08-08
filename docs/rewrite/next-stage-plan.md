# ScalaAPI Next Stage Plan

## Baseline reached

The greenfield bootstrap is now usable: ScalaAPI naming is active, migration-only
runtime code is removed, the empty schema is checksum-idempotent, official Garnet
replaces Redis and the embedded RESP server, the Provider mock and object storage
are in the stack, current-source images start independently, and test or benchmark
child failures propagate a non-zero result.

This does not close the product rewrite. The next stage is one authoritative,
billable OpenAI Chat Completions vertical slice. Work outside that slice stays
`partial`, `skeleton`, or `missing` in the inventory.

## Progress checkpoint (2026-08-08, platform `7613b92`, gateway `c807dc8`)

- Completed in `b266e17`: business balances, quotas, costs, limits, and routing
  multipliers use `decimal`; precision projections cover User, Group, and API key.
- Completed in `7f6c855`: Admin discovery is backed by the product `entity_registry`
  migration and no active listing path reads Orleans internal storage.
- Completed in `227623f`: password/OAuth login issues rotating database-backed
  sessions; refresh replay, logout, and per-session revocation are now API contracts.
- Completed in `06adeb9`: Photon now frames Gateway SSE responses as chunked bodies;
  current JSON and SSE provider paths are observable in the isolated runtime.
- Completed in `8a3850b`: lease completion writes a unique NUMERIC usage debit to
  `balance_ledger` in the same transaction as usage and outbox state.
- Completed in `bea5cbb`: lease creation and terminal settlement persist an
  idempotent `balance_holds` row with `active`, `committed`, and `released` states.
- Completed in `05e9300` and `f1ed79e`: non-media request idempotency is durable,
  fingerprint-aware, checked before scheduling, and race-safe at lease creation.
- Completed in `0e6b535`, `c4d7c6e`, `334b507`, and `62bbacf`: Admin settlement
  queries, provider-mock seed, one-time API-key rotation, and Admin-to-user key
  projection have current-image runtime evidence.
- Completed in `2607006`: optional API-key policy arrays are normalized at the grain
  boundary, so omitted Admin JSON cannot turn authentication into a null-reference
  failure.
- Completed in `7f755f3`, `5a3de2a`, and `76452e0`: Garnet uses the versioned
  `scalaapi:v1` keyspace, bounded projection TTLs, authenticated TCP clients, and
  a protected Platform projection rebuild. Current-image rebuild evidence is
  `15/15` projections written with zero errors.
- Completed in `1ec32e3`: malformed provider usage is rejected before settlement;
  the live mock probe returned `502`, an aborted lease, a released hold, and zero
  ledger rows.
- Completed in `adbccdc`, `a8b53c3`, and `c180136`: redeem-code effects use a
  PostgreSQL row lock, unique redemption and ledger keys, and a stable replayable
  User Grain balance effect. Current-image runtime evidence is first-request `200`,
  duplicate `409`, one redemption row, one ledger row, and recovery after a Silo
  contract restart; concurrent HTTP and audit-event tests remain.
- Completed in `b42eeba` and `93f0f14`: all monetary and pricing fields in the
  revision-1 Cap'n Proto contract use signed 1e8 fixed-scale integers. Platform
  encodes decimal rates and holds at the boundary, Gateway decodes them without
  Float64 wire fields, and a Host precision round-trip test passes; CI generation
  comparison remains open.
- Completed in `df93623` and `f8b6761`: provider failover assigns unique internal
  lease IDs for each retry while retaining the public idempotency key; matching
  keys can reopen only after an aborted or expired lease, and active/completed keys
  still replay or reject fingerprint changes. Live 500/429 exhaustion probes now
  return `503 provider_unavailable` with one terminal lease per retry and no ledger
  debit.
- Completed in `12836e8` and `809c19b`: password recovery is a native single-use
  token flow with hash-only storage, fifteen-minute expiry, session revocation, and
  enumeration-safe request responses. Current-image evidence covers one successful
  confirmation and token replay rejection; mail delivery and verification remain.
- Completed in `f068359`: email verification is a separate single-use token flow
  with hash-only storage, twenty-four-hour expiry, durable verification timestamp,
  and replay rejection. Mail delivery and browser verification remain external
  release gates.
- Completed in `a95786d` and `d066498`: the Provider mock exposes a cancellable
  timeout scenario and Gateway applies a 30-second non-stream boundary plus a
  request retry budget. Current-image evidence shows a 30.3-second `502`, an
  aborted lease, and no usage event; disconnect and restart scenarios remain.
- Completed in `2c511eb` and `3643ec7`: completed non-stream idempotent requests
  persist a bounded response status, content type, and body through Platform and
  the Gateway outbox. After settlement, a matching retry returns the original
  response without a new lease or debit; an active lease remains a deterministic
  409 until its usage report is durable. Runtime evidence shows one completed
  lease, one usage event, one NUMERIC debit, and a delayed 200 replay.
- Completed in `653c908`: settlement outbox claims recover after a 30-second
  worker lease expires; process startup requeues unprocessed legacy dead-letter
  rows, and financial events no longer auto-dead-letter after retry exhaustion.
  Host coverage simulates a crashed claim and 26 failed retries without losing
  the expiry event. Deployment-level Silo crash and hold reconciliation remain.
- Completed in `b90ff11`: every newly created lease stores a price version and
  NUMERIC unit-rate snapshot; settlement no longer reads mutable current pricing.
  Host integration changed the configured price after lease creation and observed
  the original cost. Admin-authoritative price lifecycle and historical backfill
  remain open.
- Completed in `c807dc8`: Gateway treats a deleted Garnet invalidation-version
  key as a flush event, evicts speculative authorization, and re-establishes a
  baseline when the key returns. CTest covers version change, flush, recovery,
  and repeated missing-key polls; TLS, multi-client, and deployment restart
  evidence remain.
- Completed in `e07e5ac`: Admin `/admin/usage/reconcile` compares usage events,
  usage-debit ledger entries, and active holds, then persists a NUMERIC mismatch
  result in `ledger_reconciliation_runs`. The first reused smoke run correctly
  failed on two missing debits and orphan test ledger rows; after an isolated
  usage/ledger reset, a fresh seeded request passed with zero mismatch. Automate
  clean-seed repair and historical backfill before release.
- Completed in `21dfa2c`: API-key quota evaluation is a deterministic domain policy
  with absolute-quota precedence, shortest-window (5h/1d/7d) precedence, independent
  expiry reset, and explicit unlimited zero-limit behavior. Grain tests cover each
  branch; subscription entitlement and quota-grant lifecycle remain open.
- Completed in `3d49e57`: usage settlement now publishes API-key invalidation so
  Gateway cannot authorize a request from a stale quota projection. A current-image
  low-quota probe completed once and then returned `401 Quota exhausted` after the
  projection was rebuilt; distributed concurrent reservation remains open.
- Completed in `5ec2efe` and `08cf00c`: payment providers now have a native signed
  webhook boundary. HMAC verification, provider/event deduplication, exact amount
  and currency checks, `payment.succeeded`/`payment.refunded` state transitions,
  unique NUMERIC credit/refund ledger effects, retryable balance projection, and
  stable order identity are covered by tests and current-image runtime probes.
  Provider-specific adapters, reconciliation UI, and crash recovery remain open.
- Completed in `6a1b77c` and `4987b64`: authenticated users can read/update profile
  data, change passwords while revoking other sessions, and delete accounts after
  password/confirmation checks. Current-image evidence covers old-password and
  revoked-refresh rejection plus soft deletion with three revoked sessions.
  Concurrent session tests, API-key revocation fixtures, retention policy, and
  browser coverage remain open.
- Completed in `03833e7`: subscription plans have a native idempotent purchase,
  listing, cancellation, renewal, and automatic-expiry state machine. PostgreSQL
  enforces one active subscription per user and stores a unique event for each
  transition; current-image probes cover replay and active-conflict behavior.
  Payment-provider coupling, applying quota grants to API-key policy, renewal
  workers, and browser coverage remain open.
- Completed in `cb09e34`: pending payment webhooks now have a native recovery
  worker with `SKIP LOCKED` claims, attempt/error metadata, bounded backoff, and
  stable balance-effect replay. A current-image pending event recovered after an
  Admin restart; provider adapters, reconciliation UI, and exact SQL/cluster
  crash injection remain open.
- Completed in `7b63fd2` and `6d725ce`: Admin can publish, query, and close
  validated `pricing_versions` with UTC effective intervals and duplicate
  protection, and Platform Host refreshes active versions into new dispatches.
  Lease settlement remains snapshot-based; provider price adapters and historical
  backfill remain open.
- Completed in `7613b92`: Provider mock now owns deterministic OpenAI
  Chat/Responses/models/embeddings, Anthropic Messages/count-tokens, Gemini
  models/generation, and pollable image/video contracts. OpenAI scheduling admits
  video operations, and media polling preserves `image/png`/`video/mp4` output
  types. Current-image Gateway probes pass for Responses, models, embeddings,
  synchronous images, and durable asynchronous image/video completion. Provider
  groups for Anthropic/Gemini and object-store byte ownership remain open.
- Still open: PostgreSQL aggregate repositories/foreign keys, fixed-precision RPC
  schema generation, crash/restart settlement scenarios, provider failure matrix,
  object-store media ownership, empty-volume CI automation, and Garnet
  flush/stale-version/TLS/multi-client evidence.

## Stage 2 objective

Deliver this path from an empty environment:

```text
register/login -> create API key/group/provider account -> Gateway request
-> Platform dispatch/lease/hold -> Provider mock JSON or SSE
-> usage outbox -> idempotent settlement -> Admin usage/ledger query
```

### 1. Authority and numeric contracts

- Keep the completed decimal conversion and fixed-scale RPC fields. Add canonical
  C# generation in CI and reject schema/output drift. PostgreSQL remains `NUMERIC`.
- Extend the completed `entity_registry` discovery boundary into repositories for
  user, API key, group, account, lease, hold, usage, and ledger records. No Orleans
  storage internals may be queried for business data.
- Add a forward migration for missing constraints, foreign keys, immutable price
  versions, idempotency fingerprints, and append-only ledger entries.
- Generate Platform C# RPC artifacts from `contracts/capnp` in CI and compare them
  with the committed output and Gateway schema digest.

Exit: decimal round-trip tests have no float conversion, repository/API reads agree
with PostgreSQL, and contract drift fails CI.

### 2. Identity and control-plane setup

- Harden the completed password/OAuth session path with replay/concurrent-rotation
  integration tests, session limits, and refresh-token audit events.
- Complete API-key create/list/rotate/revoke with one-time plaintext display and
  hash-only persistence; registry-backed list/revoke exists, rotation and policy
  tests remain.
- Complete group and provider-account creation with encrypted credentials and a
  deterministic Provider mock seed profile.
- Add one idempotent seed command for local/E2E use; production starts without it.

Depends on: authority contracts. Exit: an empty database can be configured entirely
through product APIs and revoked sessions/keys are rejected across Gateway instances.

### 3. Lease, hold, and settlement state machines

- Specify lease states and legal transitions: `created`, `held`, `forwarded`,
  `completed`, `aborted`, `expired`, and `settled`.
- Bind request ID, idempotency key, request fingerprint, account, price version, and
  durable hold to one lease before upstream forwarding. Completed non-stream
  responses now persist a bounded replay payload through migration 011; matching
  retries after settlement do not allocate another lease or debit. Active duplicate
  requests remain 409 until the completion report is durable, and streaming replay
  is intentionally a separate protocol design.
- Commit hold release/debit, usage event, ledger entry, and outbox acknowledgement
  transactionally or through replay-safe unique effects. The current completion
  transaction covers usage, ledger debit, lease finalization, and outbox enqueue;
  outbox claims recover after process restart, and financial events no longer
  auto-dead-letter after a retry threshold. Full deployment-level hold
  reconciliation and crash injection are still required.
- Make duplicate completion, abort, expiry, and outbox replay return the stored
  terminal result without applying money twice.

Depends on: decimal contracts and repositories. Host coverage now proves stale
claim recovery and no retry-threshold loss. Exit: deployment crash/retry tests
also prove no double charge, lost charge, negative available balance, or orphan
hold.

### 4. OpenAI Chat Provider vertical

- Finish JSON and SSE request/response golden fixtures at Gateway; the live current
  image now proves both response paths, usage extraction, and delayed JSON replay.
- Route only through the revision-1 RPC contract and a provider-adapter interface;
  the mock is the first adapter target.
- Preserve request IDs, bounded streaming/backpressure, usage parsing, provider
  status, retry limits, cancellation, safe error mapping, and bounded replay
  headers/body semantics.
- Expose Admin request, lease, usage, hold, and ledger queries from PostgreSQL.
  The current filtered ledger/lease/hold endpoints are a first operator surface;
  add cursor pagination and export before declaring the domain complete.

Depends on: control-plane seed and settlement state machine. Exit: the full path is
observable from client request through an Admin/PostgreSQL ledger query for JSON and
SSE, including duplicate and failure semantics.

### 5. Garnet projection resilience

- Define the `scalaapi:v1` key registry, owner, value schema, TTL, and invalidation
  version for every key used by the vertical slice. The current source has the
  namespace and bounded TTLs for auth, account, route/config, sticky, and
  invalidation keys.
- The protected Platform rebuild command and cache-miss repopulation now rebuild
  auth projections from product registry plus Orleans state. Gateway unit
  coverage handles invalidation-version changes and Garnet flush/recovery;
  add TLS, multi-client, and deployment restart tests.
- Add authenticated multi-client and TLS integration tests with one Platform and at
  least two Gateway clients. Cache loss must affect readiness/routing but never lose
  durable settlement work.

Depends on: authoritative repositories. Exit: flush, restart, stale projection,
and Garnet outage/recovery tests pass without a billable request failing open;
the remaining TLS/multi-client checks must run in the release stack.

### 6. Provider failure and recovery matrix

- Drive Provider mock 429, 500, timeout, disconnect, and malformed usage through
  both JSON and SSE, asserting bounded retries, terminal lease state, released or
  committed hold, one usage event, and one ledger debit.
- Inject Gateway and Platform restarts at dispatch, streaming, report, and outbox
  boundaries. Reconcile active holds and idempotency rows after lease expiry without
  reopening a billable request.

Depends on: lease/hold/idempotency state machines and provider seed. Exit: every
  failure scenario is replay-safe and returns a non-zero test result on assertion
  failure.

### 7. Automated acceptance and operations

- Run the versioned Compose stack in CI from empty volumes and capture image digests,
  migration checksums, health results, and scenario exit codes.
- Add structured correlation for request, lease, idempotency, account, and settlement
  IDs without logging credentials or API keys.
- Make every scenario and benchmark report failure through the top-level process.

Acceptance scenarios: success JSON, success SSE, duplicate request, conflicting
fingerprint, provider 429, provider 500, timeout, malformed usage, client disconnect,
Gateway restart, Platform restart, Garnet outage, and outbox replay. Each scenario
asserts response semantics, one terminal lease, one usage effect, correct hold state,
and exactly one ledger debit.

## Sequencing

Work packages 1 and the contract-generation part of 7 can run first. Package 2 then
provides the seed data required by 4 and 6. Package 3 is now implemented for the
happy path but must pass the crash/reconciliation controls in 6 before the slice is
treated as billable. Package 5 follows repository work and runs before final
acceptance.

The stage exits only when all acceptance scenarios run against current-source images
from an empty database. Route presence, mock-only success, or compatibility with
Sub2API behavior and data are not acceptance criteria.

## Later stages

After this vertical closes, expand across all 58 inventory domains: remaining
OpenAI/Anthropic/Gemini protocols, media/realtime, identity and User Web, commercial
flows, security/operations, HA, load/soak, backup/restore, and signed rollback. Every
inventory row still requires a contract, automated tests, and current runtime evidence.
