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

## Progress checkpoint (2026-08-08, platform `966be26`, gateway `60f99a0`)

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
- Completed in `66ef4a2`: migration 016 gives media operations native object
  metadata. Successful provider polls download bytes into MinIO through a SigV4
  client, store key/ETag/size/status, and return one-hour presigned URLs; a failed
  upload leaves the operation retryable and does not settle usage. Image and video
  Compose probes downloaded the stored bytes. Deletion, reconciliation, restore,
  and lifecycle cleanup remain release work.
- Completed in `0d5284f`: media `delete_outputs`, terminal delete, and expiry cleanup
  now call idempotent S3 DELETE before clearing PostgreSQL metadata. Gateway batch
  deletion returned 200, the old presigned URL returned 404, metadata was cleared,
  and terminal deletion returned 204. Object-vs-database reconciliation and
  backup/restore remain release work.
- Completed in `c5f5923`: `/admin/seed/provider-mock-suite` idempotently creates
  separate OpenAI, Anthropic, and Gemini accounts/groups with explicit model sets.
  Repeated calls returned the same IDs. Anthropic and Gemini non-stream requests
  traversed Gateway, independent scheduling groups, immutable price snapshots,
  usage settlement, and returned 200; native SSE fixtures now exist for both.
- Completed in `e66ee8c`: the Anthropic JSON-on-stream fault can be selected through
  standard `metadata.user_id`, which survives Gateway model mapping and makes the
  non-SSE protocol guard reproducible through the full stack. Final-image evidence
  is bounded 503, four aborted leases, four released holds, zero usage/ledger rows,
  no Photon overflow, and Gateway usage backlog zero.
- Completed in Gateway `6d8ddee`: successful stream responses are accepted only
  with `text/event-stream`. A deliberately JSON-on-stream upstream response now
  fails before any client body write instead of overflowing Photon's bounded writer.
- Completed in Gateway `7ded81d`: durable usage records receiving terminal
  non-retryable lease results are removed with a failure audit log, while retryable
  transport failures remain queued. This prevents one expired lease from blocking
  all later usage reports after restart.
- Completed in Gateway `60f99a0`: StreamPipe reads Anthropic input usage from
  `message_start.message.usage` and merges it with final output usage. The new
  regression raises Gateway CTest to 88 cases. Current image `cd7013f2` settled
  one Anthropic SSE request with 32 input tokens, 5 output tokens, immutable price
  version `stage2-anthropic-v1`, and NUMERIC cost `0.00017100`.
- Completed in `3493d0d`: administrative user replacement now applies the balance
  already present in its public request contract. The new Grain scenario covers a
  zero-balance registration followed by initial funding and routing configuration;
  the full Platform suite is now 83/83.
- Completed in `80cdad4`: `deploy/stack/smoke.sh` is the source-owned greenfield
  gate for Docker and Podman. A unique empty project applied migrations 000-016,
  skipped all seventeen on the second migrator run, configured only through new
  product APIs, settled and replayed Chat exactly once, authenticated against
  Garnet, initialized an empty MinIO bucket, stored an asynchronous image, and
  downloaded 67 signed bytes before scoped cleanup.
- Completed in `25b3834`: Platform pins `capnpc-csharp` 1.3.118 and the official
  Cap'n Proto 1.0.2 compiler commit, regenerates all three C# artifacts in a temporary
  directory, and rejects byte drift. The positive comparison passed and an intentional
  drift probe returned exit 1 with a unified diff.
- Completed in `966be26`: the empty-volume gate independently replaces Platform and
  Gateway with new container identities while retaining named volumes. A fresh
  billable request after each replacement settled with one completed lease, committed
  hold, usage event/log, matching NUMERIC debit, and zero Platform/Gateway outbox
  backlog. Exact dispatch/report/outbox crash injection remains open.
- Still open: PostgreSQL aggregate repositories/foreign keys, cross-repository schema
  release coordination, boundary crash settlement scenarios, full provider failure matrix,
  object-store reconciliation/restore, hosted execution of the cross-repository
  empty-volume gate, and Garnet TLS/multi-client evidence.

## Next execution order

1. Extend the Compose gate with explicit 429, 500, timeout, malformed usage, and
   client-disconnect scenarios, followed by replacements at dispatch, report, and
   outbox boundaries. Each must
   assert terminal leases, released/committed holds, outbox drain, and exact debit
   cardinality.
2. Add protocol golden fixtures for OpenAI Chat/Responses, Anthropic Messages, and
   Gemini JSON/SSE paths, including retry and provider-error mappings.
3. Add a two-Gateway Garnet test with flush/stale-version recovery and authenticated
   TLS. Garnet remains projection-only and must fail closed.
4. Move administrative funding and all remaining balance mutations behind
   idempotent PostgreSQL ledger effects, then add aggregate repositories and foreign
   keys so accounting authority is no longer split across Orleans state and SQL.
5. Execute the source-owned gate in hosted CI after provisioning a read-only sibling
   repository credential or creating a dedicated release repository. A skipped or
   optional cross-repository job is not an acceptable release gate.

## Stage 2 objective

Deliver this path from an empty environment:

```text
register/login -> create API key/group/provider account -> Gateway request
-> Platform dispatch/lease/hold -> Provider mock JSON or SSE
-> usage outbox -> idempotent settlement -> Admin usage/ledger query
```

### 1. Authority and numeric contracts

- Keep the completed decimal conversion and fixed-scale RPC fields. Pinned CI
  regeneration now rejects schema/output drift. PostgreSQL remains `NUMERIC`.
- Extend the completed `entity_registry` discovery boundary into repositories for
  user, API key, group, account, lease, hold, usage, and ledger records. No Orleans
  storage internals may be queried for business data.
- Add a forward migration for missing constraints, foreign keys, immutable price
  versions, idempotency fingerprints, and append-only ledger entries.
- Coordinate generated Platform output and Gateway vendor schema/digest updates as
  one release change; both repository gates must pass before publication.

Exit: decimal round-trip tests have no float conversion, repository/API reads agree
with PostgreSQL, and contract drift fails CI.

### 2. Identity and control-plane setup

- Harden the completed password/OAuth session path with replay/concurrent-rotation
  integration tests, session limits, and refresh-token audit events.
- Complete API-key create/list/rotate/revoke with one-time plaintext display and
  hash-only persistence; registry-backed list/revoke exists, rotation and policy
  tests remain.
- Complete group and provider-account creation with encrypted credentials and real
  provider OAuth/API-key refresh. The idempotent three-provider mock seed is complete;
  production starts without invoking it.

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

- Drive Provider mock 429, 500, timeout, disconnect, malformed usage, and invalid
  stream content type through every supported JSON/SSE protocol, asserting bounded
  retries, terminal lease state, released or committed hold, one usage event, and
  one ledger debit where settlement is valid.
- Inject Gateway and Platform restarts at dispatch, streaming, report, and outbox
  boundaries. Reconcile active holds and idempotency rows after lease expiry without
  reopening a billable request. Clean requests after independent Platform and Gateway
  replacement now pass; boundary-timed injection is still required.

Depends on: lease/hold/idempotency state machines and provider seed. Exit: every
  failure scenario is replay-safe and returns a non-zero test result on assertion
  failure.

### 7. Automated acceptance and operations

- Keep the completed source-owned empty-volume Compose gate blocking locally; run it
  in hosted CI once the private sibling-repository checkout boundary is provisioned,
  and capture image digests, migration checksums, health results, and scenario exit
  codes.
- Add structured correlation for request, lease, idempotency, account, and settlement
  IDs without logging credentials or API keys.
- Make every scenario and benchmark report failure through the top-level process.

Acceptance scenarios: success JSON, success SSE, duplicate request, conflicting
fingerprint, provider 429, provider 500, timeout, malformed usage, client disconnect,
Gateway restart, Platform restart, Garnet outage, and outbox replay. Each scenario
asserts response semantics, one terminal lease, one usage effect, correct hold state,
and exactly one ledger debit.

## Sequencing

The contract-generation gate is complete. Package 1 repository constraints and
package 6 failure/restart automation now run next. Package 2 then provides the seed
data required by 4 and 6. Package 3 is now implemented for the
happy path but must pass the crash/reconciliation controls in 6 before the slice is
treated as billable. Package 5 follows repository work and runs before final
acceptance.

The happy-path stage now runs against current-source images from an empty database.
The stage exits only when the remaining failure/restart scenarios run through that
same gate in hosted CI. Route presence, mock-only success, or compatibility with
Sub2API behavior and data are not acceptance criteria.

## Later stages

After this vertical closes, expand across all 58 inventory domains: remaining
OpenAI/Anthropic/Gemini protocols, media/realtime, identity and User Web, commercial
flows, security/operations, HA, load/soak, backup/restore, and signed rollback. Every
inventory row still requires a contract, automated tests, and current runtime evidence.
