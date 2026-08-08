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

## Stage 2 objective

Deliver this path from an empty environment:

```text
register/login -> create API key/group/provider account -> Gateway request
-> Platform dispatch/lease/hold -> Provider mock JSON or SSE
-> usage outbox -> idempotent settlement -> Admin usage/ledger query
```

### 1. Authority and numeric contracts

- Replace monetary and rate `double` fields in public DTOs, grain contracts, state,
  and RPC conversion with `decimal`; encode contract money as an explicit fixed-scale
  integer or canonical decimal text. PostgreSQL remains `NUMERIC`.
- Define aggregate ownership for user, API key, group, account, lease, hold, usage,
  and ledger records. Add business repositories and stop using Orleans storage
  internals for list or accounting queries.
- Add a forward migration for missing constraints, foreign keys, immutable price
  versions, idempotency fingerprints, and append-only ledger entries.
- Generate Platform C# RPC artifacts from `contracts/capnp` in CI and compare them
  with the committed output and Gateway schema digest.

Exit: decimal round-trip tests have no float conversion, repository/API reads agree
with PostgreSQL, and contract drift fails CI.

### 2. Identity and control-plane setup

- Complete password registration/login plus rotating refresh sessions, replay
  detection, logout, and revocation.
- Complete API-key create/list/rotate/revoke with one-time plaintext display and
  hash-only persistence.
- Complete group and provider-account creation with encrypted credentials and a
  deterministic Provider mock seed profile.
- Add one idempotent seed command for local/E2E use; production starts without it.

Depends on: authority contracts. Exit: an empty database can be configured entirely
through product APIs and revoked sessions/keys are rejected across Gateway instances.

### 3. Lease, hold, and settlement state machines

- Specify lease states and legal transitions: `created`, `held`, `forwarded`,
  `completed`, `aborted`, `expired`, and `settled`.
- Bind request ID, idempotency key, request fingerprint, account, price version, and
  hold to one durable lease before upstream forwarding.
- Commit hold release/debit, usage event, ledger entry, and outbox acknowledgement
  transactionally or through replay-safe unique effects.
- Make duplicate completion, abort, expiry, and outbox replay return the stored
  terminal result without applying money twice.

Depends on: decimal contracts and repositories. Exit: crash/retry tests prove no
double charge, lost charge, negative available balance, or orphan hold.

### 4. OpenAI Chat Provider vertical

- Finish JSON and SSE request/response golden fixtures at Gateway.
- Route only through the revision-1 RPC contract and a provider-adapter interface;
  the mock is the first adapter target.
- Preserve request IDs, bounded streaming/backpressure, usage parsing, provider
  status, retry limits, cancellation, and safe error mapping.
- Expose Admin request, lease, usage, hold, and ledger queries from PostgreSQL.

Depends on: control-plane seed and settlement state machine. Exit: the full path is
observable from client request through ledger query for JSON and SSE.

### 5. Garnet projection resilience

- Define the `scalaapi:v1` key registry, owner, value schema, TTL, and invalidation
  version for every key used by the vertical slice.
- Add a Platform rebuild command and automatic cache-miss repopulation from
  PostgreSQL; add explicit flush and stale-version recovery tests.
- Add authenticated multi-client and TLS integration tests with one Platform and at
  least two Gateway clients. Cache loss must affect readiness/routing but never lose
  durable settlement work.

Depends on: authoritative repositories. Exit: flush, restart, stale projection, and
Garnet outage/recovery tests pass without a billable request failing open.

### 6. Automated acceptance and operations

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

Work packages 1 and the contract-generation part of 6 can run first. Package 2 then
provides the seed data required by 4. Package 3 must be complete before 4 is treated
as billable. Package 5 follows the repository work and runs before final acceptance.

The stage exits only when all acceptance scenarios run against current-source images
from an empty database. Route presence, mock-only success, or compatibility with
Sub2API behavior and data are not acceptance criteria.

## Later stages

After this vertical closes, expand across all 58 inventory domains: remaining
OpenAI/Anthropic/Gemini protocols, media/realtime, identity and User Web, commercial
flows, security/operations, HA, load/soak, backup/restore, and signed rollback. Every
inventory row still requires a contract, automated tests, and current runtime evidence.
