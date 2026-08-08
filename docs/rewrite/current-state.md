# ScalaAPI Rewrite Current State

This is the active baseline for the new ScalaAPI product. It is not a Sub2API
compatibility statement. The Sub2API repository is a read-only requirements
reference and is excluded from builds, runtime configuration, schemas, seeds, and
release artifacts.

## Source snapshot

| Repository | Commit | Worktree | Responsibility |
| --- | --- | --- | --- |
| `gateway` | `06adeb9` | clean | C++ HTTP/WebSocket edge, protocol conversion, chunked streaming, Provider transport, Cap'n Proto client |
| `platform` | `f1ed79e` | clean | C# Orleans control plane, PostgreSQL persistence, leases, NUMERIC ledger, durable holds/idempotency, usage, media lifecycle, Admin API |
| `sub2api` | `43ec48d` | read-only clean reference | Functional requirements catalogue only |

The current source inventory is 49 Gateway implementation files, 82 CTest cases,
105 hand-written Platform production C# files plus 3 generated Cap'n Proto files,
31 Platform test or benchmark source files, 65 Platform test cases, 123 mapped
Admin API routes, 33 product tables, 20 SQLSugar entity types, and 24 Admin Web
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
  columns use `NUMERIC`. The current Cap'n Proto generated boundary still has
  Float64 rate fields, with explicit casts isolated in the RPC serializer.
- Admin discovery uses the product-owned `entity_registry` table rather than
  Orleans internal storage. Registration, CRUD creation/deletion, OAuth identity
  creation, and API-key self-service maintain registry membership.
- Password and OAuth logins issue database-backed sessions with hashed rotating
  refresh tokens. JWT validation checks the active session row; user and admin
  logout plus per-session revoke are available.
- New user registration and OAuth identity creation read back the PostgreSQL
  identity before creating the Orleans aggregate and registry entry; new users
  no longer alias to id 0.
- Lease completion writes the usage event, `balance_ledger` `usage_debit`, terminal
  lease state, and durable outbox entry in one PostgreSQL transaction. The ledger
  key `(lease_token, entry_type)` is unique and all amounts remain `NUMERIC`.
- Lease creation writes an `active` row to `balance_holds` in that same transaction.
  Completion marks it `committed`; abort and expiry mark it `released`, using
  idempotent active-only updates.
- Non-media requests persist `(api_key_id, idempotency_key, request_fingerprint)`
  in `request_idempotency` with the lease. Existing keys are checked before
  scheduling and return replay or fingerprint conflict; the create transaction
  remains the race-safe fallback. Media operations retain their own lifecycle
  table and idempotency contract.
- OpenAI Chat JSON and SSE pass the current Gateway -> Cap'n Proto -> Platform ->
  Provider mock path. Photon streaming responses use explicit chunked framing and
  provider usage is captured for settlement.
- CDC consumers, Debezium configuration, migration fences, migration write gates,
  migration-control endpoints, CDC-only tables, and their tests are removed from
  active code. Their documents remain under `docs/archive/migration`.
- Platform and Gateway use authenticated TCP clients for the external Garnet
  service, support TLS with certificate-name verification, and have no embedded
  cache implementation or Microsoft.Garnet package dependency.
- A source-owned Provider mock now supplies deterministic JSON, SSE, failure,
  delay, disconnect, and malformed-usage scenarios. Its image, health endpoint,
  success response, and 429 response passed the isolated Compose smoke.
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
- The Cap'n Proto schema and checked-in generated artifacts still encode money/rate
  fields as Float64; replace them with fixed-scale integer or canonical decimal text
  before declaring the public RPC contract complete.
- Full empty-environment migration replay from an actually empty volume is still a
  release gate; the current isolated database applied 005, 006, and 007 and a
  second migrator invocation skipped all eight recorded migrations.
- Session concurrent-rotation HTTP tests, crash injection, and API-key policy tests
  remain pending even though replay/logout, rotation, and revoke paths have runtime
  evidence.
- Generated C# Cap'n Proto files are checked in but are not regenerated and digest
  verified by CI yet.
- Garnet key TTL policy, projection rebuild, cache-flush recovery, and multi-client
  integration tests remain incomplete even though connection outage/recovery passes.
- Hold reconciliation after Orleans/process failure, pricing-version authority,
  provider adapters beyond the mock, object-byte lifecycle, User Web, commercial
  workflows, and operational release controls remain partial or skeletal.
- Admin Web typecheck/build is now a blocking CI step, but browser coverage is absent.

## Current runtime evidence

On 2026-08-08 the isolated `scalaapi-stage2` project used current-source images:
Platform `212cbcbcd1ecebd42c95330114975d56aeb908c382f104c62c252862deaf13d4`,
migrator `8ea85be1a9ffed1885cf35fbdb54c2813c04384f7c8f6c1d2fe1fbd7ae3fddbf`,
Gateway `64b62db3278040332554748e7a1ab6602d792032bbd69e4635159e4f19b04e99`, and
Provider mock `425e1430cc32f8756a688d176f1d542c9026603c37e0cb609e55b5ee49d6bcb8`.
The migrator applied 005-007 and a second run skipped them all. Registration
returned user id 7 and registry id 7. Provider seed was idempotent (same account
and group on two calls); API-key rotation revoked the old key; Admin-created keys
were projected into `user_api_keys`. Seeded JSON and SSE requests both returned
200 through Provider mock. A completed lease settled at `0.00006750` USD with one
`-0.00006750` NUMERIC ledger row and one committed durable hold.

For request idempotency, two concurrent calls with the same key produced one 200
and one 409 replay; a different fingerprint produced 409 conflict. Each key had
one lease, one usage debit, and one committed hold. Admin ledger, lease, and hold
query endpoints returned the corresponding PostgreSQL rows. Refresh-token replay
and logout revocation also returned 401 after the first use.

Provider mock upstream-failure probes created terminal `aborted` leases with
`released` holds and zero ledger rows. Account cooldown then failed closed with
`provider_unavailable` when no account was available; a complete 429/500 retry and
failover matrix is still a release gate.

Authenticated Garnet `PING`, `SET/GET`, PX expiry, `INCR`, and `DEL` passed. Stopping
Garnet changed Platform readiness to 503; restarting it restored readiness to 200.
Gateway readiness returned 200 and an unknown API key traversed the current dispatch
path to a stable 401. This is bootstrap evidence, not evidence for a successful
billable request or settlement; the seeded request evidence above is the current
source billable path.

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
