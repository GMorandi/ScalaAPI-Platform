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

## Progress checkpoint (2026-08-08, platform `c180136`, gateway `1ec32e3`)

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
  `12/12` projections written with zero errors.
- Completed in `1ec32e3`: malformed provider usage is rejected before settlement;
  the live mock probe returned `502`, an aborted lease, a released hold, and zero
  ledger rows.
- Completed in `adbccdc`, `a8b53c3`, and `c180136`: redeem-code effects use a
  PostgreSQL row lock, unique redemption and ledger keys, and a stable replayable
  User Grain balance effect. Current-image runtime evidence is first-request `200`,
  duplicate `409`, one redemption row, one ledger row, and recovery after a Silo
  contract restart; concurrent HTTP and audit-event tests remain.
- Still open: PostgreSQL aggregate repositories/foreign keys, fixed-precision RPC
  schema generation, crash/restart settlement scenarios, provider failure matrix,
  empty-volume CI automation, and Garnet flush/stale-version/TLS/multi-client evidence.

## Stage 2 objective

Deliver this path from an empty environment:

```text
register/login -> create API key/group/provider account -> Gateway request
-> Platform dispatch/lease/hold -> Provider mock JSON or SSE
-> usage outbox -> idempotent settlement -> Admin usage/ledger query
```

### 1. Authority and numeric contracts

- Keep the completed decimal conversion, then replace the remaining Cap'n Proto
  Float64 money/rate fields with an explicit fixed-scale integer or canonical decimal
  text. PostgreSQL remains `NUMERIC`.
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
  durable hold to one lease before upstream forwarding. The current implementation
  rejects duplicate synchronous/streaming dispatches; it does not yet persist and
  replay a completed response body.
- Commit hold release/debit, usage event, ledger entry, and outbox acknowledgement
  transactionally or through replay-safe unique effects. The current completion
  transaction covers usage, ledger debit, lease finalization, and outbox enqueue;
  hold-state reconciliation and crash injection are still required.
- Make duplicate completion, abort, expiry, and outbox replay return the stored
  terminal result without applying money twice.

Depends on: decimal contracts and repositories. Exit: crash/retry tests prove no
double charge, lost charge, negative available balance, or orphan hold.

### 4. OpenAI Chat Provider vertical

- Finish JSON and SSE request/response golden fixtures at Gateway; the live current
  image now proves both response paths and usage extraction.
- Route only through the revision-1 RPC contract and a provider-adapter interface;
  the mock is the first adapter target.
- Preserve request IDs, bounded streaming/backpressure, usage parsing, provider
  status, retry limits, cancellation, and safe error mapping.
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
  auth projections from product registry plus Orleans state. Add explicit flush,
  stale-version, and restart recovery tests.
- Add authenticated multi-client and TLS integration tests with one Platform and at
  least two Gateway clients. Cache loss must affect readiness/routing but never lose
  durable settlement work.

Depends on: authoritative repositories. Exit: flush, restart, stale projection, and
Garnet outage/recovery tests pass without a billable request failing open.

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
