# ScalaAPI Rewrite Current State

This is the active baseline for the new ScalaAPI product. It is not a Sub2API
compatibility statement. The Sub2API repository is a read-only requirements
reference and is excluded from builds, runtime configuration, schemas, seeds, and
release artifacts.

## Source snapshot

| Repository | Commit | Worktree | Responsibility |
| --- | --- | --- | --- |
| `gateway` | `c807dc8` | clean | C++ HTTP/WebSocket edge, protocol conversion, chunked streaming, Provider transport, versioned Garnet client, malformed-usage guard, fixed-scale Cap'n Proto client, unique failover lease IDs, bounded non-stream upstream timeout, bounded response replay, invalidation flush recovery |
| `platform` | `0d5284f` | clean | C# Orleans control plane, PostgreSQL persistence, leases, NUMERIC ledger, durable holds/idempotency, usage, media lifecycle, Admin API, signed payment webhooks with pending-event recovery, versioned Admin pricing lifecycle with live Host refresh, idempotent subscription purchase/renewal/cancellation/expiry, versioned Garnet rebuild, replayable balance effects, fixed-scale RPC contract, retryable terminal idempotency leases, bounded response persistence/replay, password recovery, email verification, self-service profile/password/account deletion, deterministic multi-protocol Provider mock with pollable media tasks, S3-compatible media ownership with SigV4 presigned access and deletion, restart-safe settlement outbox recovery, immutable lease price snapshots, durable ledger reconciliation endpoint, deterministic rolling quota policy, usage-triggered auth invalidation |
| `sub2api` | `43ec48d` | read-only clean reference | Functional requirements catalogue only |

The current source inventory is 50 tracked Gateway source files, 87 CTest cases,
68 hand-written Platform production C# files plus 3 generated Cap'n Proto files,
19 tracked Platform test/benchmark C# source files, 82 Platform test cases, 138 mapped
Admin API route declarations, 34 product tables, 20 SQLSugar entity types, and 31 Admin Web
source files with 11 page views. Admin Web has no browser test runner yet.

The reference inventory is 612 route registration calls, 39 concrete Ent schemas,
82 Vue view/component files, and 240 SQL migrations. These numbers describe scope,
not implementation parity or a migration target.

## Current implementation

- Gateway has protocol routing and conversion for Anthropic, OpenAI Chat/Responses,
  Gemini, embeddings, images, videos, model discovery, token counting, realtime,
  proxy/TLS hooks, and a durable local usage outbox.
- Platform has Orleans grains for users, API keys, groups, accounts, scheduling,
  pricing, leases, usage, media operations, and invalidation, plus an Admin API.
- Business and routing contracts use `decimal`; PostgreSQL monetary and pricing
  columns use `NUMERIC`. Cap'n Proto monetary and pricing fields now use signed
  integers in a documented 1e8 fixed scale; conversion is isolated at the RPC
  serializer and covered by a precision round-trip test.
- Admin discovery uses the product-owned `entity_registry` table rather than
  Orleans internal storage. Registration, CRUD creation/deletion, OAuth identity
  creation, and API-key self-service maintain registry membership.
- Password and OAuth logins issue database-backed sessions with hashed rotating
  refresh tokens. JWT validation checks the active session row; user and admin
  logout plus per-session revoke are available.
- New user registration and OAuth identity creation read back the PostgreSQL
  identity before creating the Orleans aggregate and registry entry; new users
  no longer alias to id 0.
- Password recovery uses a product-owned single-use token table. Only SHA-256
  token hashes are stored, outstanding tokens are invalidated per user, tokens
  expire after 15 minutes, and successful confirmation revokes all active sessions.
  The anonymous request response does not reveal whether an email exists; a local
  smoke-only configuration can return the raw token for deterministic testing.
- Email verification uses a separate product-owned token table with SHA-256-only
  storage, per-user invalidation, twenty-four-hour expiry, and exactly-once
  consumption. The verified timestamp is durable on `user_accounts`; local smoke
  environments can explicitly expose a token while production remains
  enumeration-safe.
- Authenticated users can read and update their profile, change their password while
  revoking other sessions, and delete their account after password and confirmation
  checks. Deletion soft-deletes the account, revokes API keys and sessions, removes
  registry/Orleans projections, and preserves billing history.
- Lease completion writes the usage event, `balance_ledger` `usage_debit`, terminal
  lease state, and durable outbox entry in one PostgreSQL transaction. The ledger
  key `(lease_token, entry_type)` is unique and all amounts remain `NUMERIC`.
- Lease creation writes an `active` row to `balance_holds` in that same transaction.
  Completion marks it `committed`; abort and expiry mark it `released`, using
  idempotent active-only updates.
- Lease creation also persists the model price version and every NUMERIC unit rate.
  Settlement uses that immutable snapshot even if runtime configuration changes;
  a legacy lease without a snapshot fails retryably instead of being repriced.
- Admin pricing publishes and closes validated `pricing_versions` with UTC effective
  intervals and immutable version identities. Platform Host refreshes currently
  effective rows into new dispatches; existing leases still settle from their
  stored snapshot, while historical backfill remains open.
- API-key absolute quota and 5-hour, daily, and weekly spend windows use one
  deterministic policy. Expired windows reset independently; absolute quota wins
  rejection precedence, then the shortest rolling window. Zero limits remain
  unlimited and the policy is covered by Grain tests.
- API-key usage settlement publishes the same invalidation event as key rotation;
  Gateway no longer serves a stale quota projection after a completed charge.
- Payment webhooks verify provider HMAC signatures over the raw body, deduplicate
  `(provider, event_id)`, validate exact order amount/currency, and apply paid or
  refunded transitions with unique NUMERIC ledger effects. Balance projection
  retries use stable effect IDs after the SQL transaction commits.
- A dedicated Admin background worker claims pending webhook events with
  `SKIP LOCKED`, applies stable balance effects after a process restart, records
  attempt/error metadata, and uses bounded exponential backoff.
- Subscription plans now have a native user lifecycle: purchase, list, renewal,
  cancellation, automatic expiry, one-active-subscription enforcement, and
  user/idempotency event records. Entitlement periods and quota grants are stored
  as NUMERIC values; API-key quota consumption and external payment coupling remain
  separate release work.
- Settlement outbox claims expire after 30 seconds, so a process restart can
  reclaim work. Failed financial effects use bounded exponential backoff without
  automatic dead-lettering; startup requeues any unprocessed rows left by an older
  process version that did dead-letter them.
- Non-media requests persist `(api_key_id, idempotency_key, request_fingerprint)`
  in `request_idempotency` with the lease. Completed non-stream successes store a
  bounded status, content type, and response body in the same settlement path;
  matching retries after settlement return that body without a new lease or charge.
  Existing keys are checked before scheduling and return replay or fingerprint
  conflict; an active lease remains a deterministic 409 until its completion report
  is durable. The create transaction remains the race-safe fallback. Media
  operations retain their own lifecycle table and idempotency contract.
- OpenAI Chat JSON and SSE pass the current Gateway -> Cap'n Proto -> Platform ->
  Provider mock path. Photon streaming responses use explicit chunked framing and
  provider usage is captured for settlement.
- Non-streaming upstream calls have a 30-second hard timeout, while streaming calls
  retain their separate long-lived budget. Once the request retry budget is spent,
  Gateway aborts the lease and returns a deterministic provider error instead of
  holding the client socket open indefinitely.
- Provider usage counters are validated as bounded non-negative integers. A malformed
  provider usage response returns `502 provider_error`, aborts the lease, releases
  its durable hold, and suppresses usage/ledger settlement. Provider failover keeps
  the public idempotency key stable while assigning each internal retry a unique
  lease request ID; 500/429 exhaustion now ends as `503 provider_unavailable`
  instead of incorrectly replaying a terminal 409.
- Redeem-code redemption locks the PostgreSQL code row, records one user redemption,
  increments usage, and appends one `redeem_bonus` ledger effect transactionally. A
  unique `(code_id, user_id)` redemption and partial `(reference, entry_type)` ledger
  key make retries deterministic; the User Grain applies a stable effect id exactly
  once and can replay a committed ledger effect after a projection failure.
- CDC consumers, Debezium configuration, migration fences, migration write gates,
  migration-control endpoints, CDC-only tables, and their tests are removed from
  active code. Their documents remain under `docs/archive/migration`.
- Platform and Gateway use authenticated TCP clients for the external Garnet
  service, support TLS with certificate-name verification, and have no embedded
  cache implementation or Microsoft.Garnet package dependency.
- Garnet projections use the versioned `scalaapi:v1` keyspace with bounded auth,
  account, route/config, sticky-session, and invalidation TTLs. Platform exposes a
  token-protected `/internal/cache/rebuild` operation that reconstructs auth
  projections from `entity_registry` and Orleans aggregates; it never treats cache
  contents as business authority.
- A source-owned Provider mock now supplies deterministic OpenAI Chat/Responses,
  Anthropic Messages/count-tokens, Gemini generation/model metadata, embeddings,
  model discovery, JSON/SSE failure scenarios, and pollable image/video tasks.
  Media polling preserves the provider-declared output content type instead of the
  polling response's JSON content type.
- Platform owns the revision-1 Cap'n Proto schemas under `contracts/capnp`; Gateway
  vendors byte-identical copies and both repositories enforce the recorded SHA-256
  schema digests.
- `deploy/stack` is the versioned empty-environment launcher. PostgreSQL, Garnet,
  MinIO, and the health helper are pinned by image digest.

## Known gaps

- The current baseline creates a clean product schema and is checksum-idempotent,
  but its broad table set has not yet been reviewed against explicit aggregate
  ownership and repository contracts.
- PostgreSQL business state and opaque Orleans storage are still split; the new
  registry removes internal-storage discovery, but full aggregate repositories and
  accounting authority remain to be implemented.
- The fixed-scale Cap'n Proto schema and checked-in C# artifacts are current, but
  CI must still regenerate the C# output from the canonical schema and compare it
  before declaring contract generation complete.
- Full empty-environment migration replay from an actually empty volume is still a
  release gate; the current isolated database has migrations 000-016 and a second
  migrator invocation skipped all seventeen recorded migrations.
- Session concurrent-rotation HTTP tests, crash injection, and API-key policy tests
  remain pending even though replay/logout, rotation, and revoke paths have runtime
  evidence.
- Generated C# Cap'n Proto files are checked in but are not regenerated and digest
  verified by CI yet.
- Garnet key TTL policy, authenticated projection rebuild, and Gateway invalidation
  flush/stale-version recovery now have runtime or unit evidence; TLS and
  multi-client integration tests remain incomplete even though connection
  outage/recovery passes.
- Hold reconciliation after Orleans/process failure, historical price backfill,
  provider adapters beyond the mock, object deletion/reconciliation/restore,
  User Web, commercial workflows, and operational release controls remain partial
  or skeletal.
- Admin `/admin/usage/reconcile` now persists a passed/failed run and detects
  missing usage debits. An initial run against the long-lived smoke database
  caught two missing debits and orphan historical test ledger rows; after an
  isolated usage/ledger reset, a fresh seeded request produced a passed run with
  zero mismatch, zero missing debits, and zero active holds. Automated clean-seed
  repair and historical backfill remain release work.
- Admin Web typecheck/build is now a blocking CI step, but browser coverage is absent.

## Current runtime evidence

On 2026-08-08 the isolated `scalaapi-stage2` project used current-source images:
Platform `a2bb71d59804fdb94721e197cb54b78b4a3f00ca4f5d61bfb3a9008877a24cef`,
Admin API `ea0cbe88a5b1bab8dd171b930ff5e6ec31be234b0bf4becf6ac6f4b74d38ca73`,
migrator `8c1b3d4299fa8a2dc20abedcdaa15e2faa42f85d4c9156ca1e4d6919e146d880`,
Gateway `4099c8aa22de23b7db13a114d40e301c0502b033545bcbbf0ed1dd0cbe7d29ca`, and
Provider mock `bfd1a32c4f1dff5efc3d040bd488a4e13db9e8d65a671eb9de14cf3b1cebe2d7`.
The migrator applied the complete 000-016 sequence and a second run skipped all
seventeen migrations. Registration
returned user id 7 and registry id 7. Provider seed was idempotent (same account
and group on two calls); API-key rotation revoked the old key; Admin-created keys
were projected into `user_api_keys`. Seeded JSON and SSE requests both returned
200 through Provider mock. A completed lease settled at `0.00006750` USD with one
`-0.00006750` NUMERIC ledger row and one committed durable hold. The request lease
stored `runtime-v1` with input/output rates `2.50000000`/`10.00000000`; after a
Platform restart the completed request settled once at the same price snapshot.
The Platform
Silo was then force-recreated from the restart-safe outbox image; Gateway readiness
returned `200`, PostgreSQL showed zero unprocessed/dead-lettered outbox rows and
zero active holds, and all 38 seeded leases remained terminal.

For request idempotency, an immediate second call with the same key produced an
active-lease 409; after the usage report settled, a matching retry returned
the original 200 body with `Cache-Control: no-store`. A different fingerprint
produced 409 conflict. The replay key had one completed lease, one usage event, one
`0.00006750` NUMERIC debit, and one committed hold. Admin ledger, lease, and hold
query endpoints returned the corresponding PostgreSQL rows. Refresh-token replay
and logout revocation also returned 401 after the first use.

Provider mock upstream-failure probes created terminal `aborted` leases with
`released` holds and zero ledger rows. A malformed-usage probe returned `502` with
the same no-charge state. Fresh 500 and 429 probes returned `503
provider_unavailable` after account exhaustion; PostgreSQL showed one aborted
initial lease plus distinct `:retry:1`, `:retry:2`, and `:retry:3` leases for each
request, all with released holds and no ledger rows. Reusing a matching external
idempotency key after an aborted lease is now retryable, while active/completed
requests still replay or conflict. Timeout, disconnect, and restart coverage
remain release gates.

Password-reset runtime evidence on the current Admin image returned a debug token
only with the explicit isolated-stack flag, accepted it once with `204`, rejected
the replay with `400`, and allowed login with the new password. The database holds
only the token hash and marks the row used; mail delivery is still an external
adapter requirement.

Email-verification runtime evidence on the same image returned a debug token only
with the explicit isolated-stack flag, accepted it once, rejected the replay with
`400`, and persisted `email_verified=true` plus `email_verified_at`. The migrator
applied 010-012 and skipped all three on subsequent invocations.

The cancellable Provider mock `timeout` scenario held a non-stream request until
Gateway's 30-second boundary; the current image returned `502` at 30.3 seconds,
the latest lease was `aborted` with `upstream_failure`, and no usage event was
created in the following two-minute window.

Redeem-code runtime evidence created a one-use `1.25` code, returned `200` on the
first request and `409` on repeat, then recovered a committed redemption after a
Silo contract restart. PostgreSQL showed `used_count=1`, one redemption row, one
`redeem_bonus` ledger row, and the Admin projection reported the expected decimal
balance. Grain tests cover duplicate balance-effect application.

Authenticated Garnet `PING`, `SET/GET`, PX expiry, `INCR`, and `DEL` passed. Stopping
Garnet changed Platform readiness to 503; restarting it restored readiness to 200.
The protected cache rebuild endpoint returned `discovered=15`, `written=15`,
`deleted=0`, `errors=0`; an immediate authenticated RESP read returned a
`scalaapi:v1:auth:*` projection. The rebuilt current Gateway image returned 200 for
a seeded OpenAI Chat request (`X-Request-ID: 4b54b3d53004943c`) and Platform recorded
one completed lease, one usage event, one `0.00006750` NUMERIC debit, and one
committed hold. A low-quota key completed its first request, then the rebuilt
projection caused the next request to return `401 authentication_error` with
`Quota exhausted`; this verifies usage-triggered auth invalidation in the current
Platform/Gateway stack.

Pricing runtime evidence published `stage2-live-1786199990` for `gpt-4o` with
`9.00000000`/`19.00000000` input/output rates, rebuilt Platform Silo image
`078858c8`, and sent a Gateway Chat request. The resulting lease stored that
version and both NUMERIC rates, proving active database pricing reaches new
dispatches while lease snapshots remain immutable.

Expanded Provider runtime evidence used a seeded OpenAI-compatible account through
Gateway and returned `200` for model discovery, Responses, Chat, embeddings, and
synchronous image generation. Active `pricing_versions` for
`text-embedding-3-small`, `mock-image-1`, and `mock-video-1` were refreshed before
dispatch, and their leases stored the exact NUMERIC version/rates. Asynchronous
image and video creation returned durable `med_*` operation IDs, then Platform
polling transitioned both rows to `succeeded` in one attempt with `image/png` and
`video/mp4` output types. The initial provider-only output URL behavior is now
superseded by the object-storage stage below.

Object-storage runtime evidence then applied migration 016 and rebuilt the Silo with
the SigV4 client. A retried image operation copied 67 bytes to
`media/med_948b989edea74db3a57455328fc353b2.png`, stored its ETag and
`object_status=stored`, and returned a one-hour presigned MinIO URL that downloaded
the exact provider-mock bytes. A video operation copied 62 bytes to its `.mp4` key
with `video/mp4` metadata and the same signed-download proof. A deliberately failed
signature attempt left the operation retryable with `object_status=failed` and no
settlement; the later retry cleared the stale error before marking it stored.

Media deletion evidence used an image batch through Gateway: after downloading the
stored `.json` object, `DELETE /v1/images/batches/{id}/outputs` returned `200`, the
old presigned URL returned `404`, and PostgreSQL cleared the object key/ETag/size with
`object_status=deleted`. Deleting the terminal operation then returned `204` and
removed its metadata row. Object-vs-database reconciliation and backup/restore are
still release gates.

Payment runtime evidence on the current Admin image created order `id=2` with
`7.25 USD`, accepted a signed `payment.succeeded` webhook once, returned
`duplicate=true` for the exact replay, and accepted a signed `payment.refunded`
webhook. PostgreSQL shows order `refunded`, three `applied` webhook events, one
`payment_credit` ledger row, and one `payment_refund` row for `-7.25`. Gateway
readiness returned 200 and an unknown API key traversed the current dispatch path
to a stable 401.

Payment recovery evidence seeded a `pending` `payment.succeeded` event after its
SQL transaction and restarted the current Admin image. The worker claimed it on
attempt `1`, applied the stable `payment:3` balance effect, marked the event
`applied`, and left zero pending webhook events; the recovery log contained the
provider/event identity.

Self-service auth runtime evidence on the rebuilt Admin image registered a fresh
user, read and updated the profile (`204`), changed the password (`204`), rejected
the old password and a revoked refresh token with `401`, and accepted the new
password. Account deletion returned `204`; PostgreSQL retained the account as
`deleted` with a null password hash, and three sessions were marked revoked.

Subscription runtime evidence on the same image created plan `id=2` with a
`25.00` USD quota, purchased subscription `id=1`, replayed the purchase as a
duplicate, rejected a second active purchase, and completed cancellation plus
renewal with stable duplicate responses. Moving its expiry to the current time
and listing subscriptions transitioned it to `expired`; PostgreSQL recorded one
`purchased`, one `cancelled`, and one `renewed` event with no duplicate rows.

## Historical runtime boundary

The old running stack was built on August 1/2 and uses `/var/run/sub2api` and a
separate historical database. Its healthy probes are historical information only.
New ScalaAPI smoke tests must use isolated project names, volumes, an empty
database, the current images, and the external Garnet service.

## Acceptance rule

A feature is `implemented` only when its API or state-machine contract, automated
tests, and current-source runtime evidence all exist. Route registration, a table,
or a placeholder response alone is not implementation evidence. The current Chat
slice remains `partial` until golden fixtures, failure scenarios, and clean-
environment automation are checked in.
