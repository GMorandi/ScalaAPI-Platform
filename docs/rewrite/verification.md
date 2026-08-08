# ScalaAPI Rewrite Verification

## Current evidence

| Gate | Result | Interpretation |
| --- | --- | --- |
| Gateway build and CTest | Clean local build; 91/91, exit 0 | Includes nested Anthropic start/final usage regression, TCP/TLS client, bounded response replay, malformed-usage/non-SSE guards, incomplete successful payload rejection, terminal usage-outbox retirement, and Garnet invalidation flush recovery |
| Platform tests | 91/91, exit 0 | 57 Grain tests, 24 PostgreSQL-connected Host tests, 4 Admin tests, and 6 Provider mock selector tests; includes ordered accounting, replay/conflict, SQL holds, versioned projection, Provider faults, TTL unknown-charge preservation, safe reconciliation repair, durable incident resolution, and late exactly-once settlement |
| Platform Release build | Passed, 0 warnings and 0 errors | Includes Platform Host, Admin API, migrator, Provider mock, and benchmark assembly |
| Admin Web | Typecheck and production build passed | Blocking CI gate exists; browser tests are not configured |
| Scheduler benchmark dry run | 4/4, exit 0; no-match negative probe exits 1 | Dependency injection and child-result propagation pass; not performance evidence |
| Contract generation and digest | Canonical and Gateway vendor schemas match; fixed-scale pricing round-trip passed; official Cap'n Proto 1.0.2 commit `1a0e12c0` plus local `capnpc-csharp` 1.3.118 regenerated all three C# files byte-identically; an intentional drift probe exited 1 with a unified diff | Platform's single-repository generated-output gate is blocking; atomic cross-private-repository schema release coordination remains |
| PostgreSQL migrator | Repository gate started a brand-new PostgreSQL volume, applied current image `4bfd2824cf1f` migrations 000-019, then observed all twenty as `skip` on an explicit second run, exit 0 | No source database, CDC, compatibility table, snapshot, or old key used |
| Empty-volume Compose gate | `deploy/stack/smoke.sh` passed from Platform `fddba62` and Gateway `dc69269` in unique Podman project `scalaapi-smoke-reconcile1`; source-built images were Platform `5fff97c3a6b0`, Admin API `51b7dafe9556`, Gateway `c8cad6a93949`, Provider mock `7c191e178d59`, migrator `4bfd2824cf1f`, and Admin Web `34a3e7f330ce`. Product APIs created and funded a zero-balance user, configured key/groups/prices, settled/replayed Chat, replaced Platform and Gateway independently, passed five isolated no-charge faults, ran comprehensive reconciliation with zero open incidents, authenticated Garnet, and downloaded the 67-byte MinIO object | Source-owned gate proves ordered accounts, holds, ledger/projection drain, and zero-incident reconciliation from empty volumes. Hosted CI, normalized error contracts, client/SSE cancellation, exact-boundary crash injection, operator incident resolution, and multi-instance evidence remain |
| Garnet smoke | Auth, PING, SET/GET, PX, INCR, DEL passed | Official digest; no Redis or embedded server |
| Garnet outage/recovery | Platform readiness 503 then 200 | Automatic TCP reconnect verified |
| Garnet projection rebuild | `discovered=15`, `written=15`, `deleted=0`, `errors=0`; immediate `scalaapi:v1:auth:*` read succeeded; Gateway CTest covers version change and deleted-version flush/recovery | TLS and multi-client assertions remain |
| Provider mock | Health, OpenAI Chat/Responses/models/embeddings, Anthropic Messages/count-tokens/SSE, Gemini models/generation/SSE, synchronous media, and asynchronous image/video task contracts are source-owned; normalized Chat input selects deterministic faults and five independent seed groups isolate scheduler state. Non-stream OpenAI 429, 500, malformed usage, upstream disconnect, and timeout all abort/release without usage or debit; final Anthropic SSE settlement stored 32 input/5 output tokens and `0.00017100` cost | Client disconnect, partial-SSE disconnect, bounded retry assertions across every protocol, real adapters, and golden fixtures remain |
| Gateway dispatch smoke | Seeded OpenAI Chat, Responses, models, embeddings, synchronous image, and asynchronous image/video requests returned success; independent Anthropic and Gemini groups returned 200 and settled against their own price versions; protocol-native JSON-on-stream injection returned bounded 503, four aborted leases/released holds, zero usage/debits, and no Photon overflow | Full cross-protocol conversion/failure matrix and empty-stack automation for non-Chat protocols remain |
| Media lifecycle smoke | Image and video create calls returned durable `med_*` IDs; Platform polling copied provider bytes to MinIO, persisted `object_status=stored`, object key/ETag/size, and returned one-hour SigV4 URLs that downloaded `image/png` or `video/mp4`; batch `delete_outputs` returned 200, removed the object (old URL 404), cleared metadata, and terminal operation delete returned 204; a signature failure remained retryable without settlement | Object-vs-database reconcile/restore, cancel/failure/restart, and batch create coverage remain |
| Billable settlement smoke | JSON and SSE completed; SQL-authoritative hold committed; usage outbox processed; one versioned NUMERIC ledger debit per successful lease; account balance equalled ledger sum, max version equalled account version, versions were contiguous/distinct, and projection backlog reached 0. Clean requests after independent Platform/Gateway replacement settled once, five non-stream faults had zero charge, and a comprehensive Admin reconciliation passed with zero open incidents | Precise dispatch/output evidence, operator incident resolution, client cancellation, partial SSE, and exact-boundary crash injection remain |
| Ordered accounting and reconciliation | All current money effects use one per-user serialized store. Real-database tests prove 20 concurrent versions, replay/conflict, hold oversubscription, protected debit, final account/ledger equality, safe terminal-hold and projection repair, mismatch/unknown-charge incidents, retained unknown-charge hold/idempotency, late settlement, and later incident resolution. A PostgreSQL advisory lock serializes scheduled and Admin runs | Add multi-Silo lock evidence, audited operator resolution, alerts, and deterministic crash hooks; subscription/affiliate effects must adopt the same authority contract |
| Administrative balance adjustment | New users started at zero; the first authenticated adjustment returned balance 100 and ledger version 1, exact replay returned `duplicate=true`, changed replay returned 409, and an excessive debit returned 409. PostgreSQL held one `admin_adjustment` NUMERIC row and one `balance.adjust` actor audit. The real-database store test also covered an active hold that prevents a debit | Browser assertions and authorization matrix remain |
| Request idempotency smoke | Concurrent same-key calls produced one 200 and one active-lease 409; after settlement a matching retry returned the original body; different fingerprint produced 409. Real-database expiry coverage proves `reconciliation_needed` blocks redispatch and late completion finalizes the original key once | Persisted dispatch/output evidence, exact-boundary crash recovery, and streaming replay semantics remain |
| Price snapshot smoke | Lease persisted `runtime-v1` and NUMERIC input/output rates; changing the in-memory price to `runtime-v2` before completion left the original cost unchanged; Admin published/closed a version, rebuilt Platform loaded `stage2-live-1786199990`, and mock embedding/image/video leases stored their active database versions and NUMERIC rates | Media-unit pricing, historical backfill, and provider price adapters remain |
| Quota projection coherence | A low-quota key completed one current-image request; after settlement and projection rebuild, the next request returned `401 authentication_error` with `Quota exhausted` instead of using a stale Gateway auth cache | Subscription entitlements, grant lifecycle, and distributed concurrent reservation remain |
| Payment webhook state machine | Current Admin image created order `id=2`, accepted signed success once, replayed it as `duplicate=true`, accepted signed refund, and persisted `paid -> refunded` plus one `payment_credit` and one `payment_refund` ledger effect; a seeded pending event was claimed on attempt 1 and recovered to `applied` with zero pending events | Provider-specific adapters, reconciliation UI, and crash injection at the exact SQL/cluster boundary remain |
| Admin settlement queries | Ledger, lease, and hold endpoints returned current PostgreSQL rows with user filters | Pagination/export and browser assertions remain |
| Reconciliation incident lifecycle | A real PostgreSQL test deliberately corrupted one account, one terminal hold, and one Grain projection while expiring a second unknown-charge lease. The first run repaired only the safe hold/projection and persisted critical account/unknown-charge incidents while retaining the unknown hold. After explicit account repair and late usage, the second run passed with zero open incidents and resolved both records | Add authenticated operator decision flow, alert delivery, multi-Silo concurrency, and dispatch/crash evidence |
| Auth lifecycle smoke | Refresh replay and logout revocation returned 401 on the current image | Concurrent rotation and multi-device HTTP tests remain |
| Redeem-code settlement smoke | First redemption returned 200; repeat returned 409; after a Silo contract restart a committed redemption remained 409 and replayed its balance effect; one redemption and one NUMERIC ledger row were observed | Concurrent HTTP contention and audit-event assertions remain |
| Provider failover idempotency | Matching external idempotency keys reopened after proven aborted leases; active/completed keys retain replay/conflict semantics, while unknown TTL outcomes stay blocked in `reconciliation_needed`; Host coverage passed | Persisted dispatch/output evidence and failover response replay remain |
| Password recovery | Explicit local debug mode issued a one-time token; first confirmation returned 204, token replay returned 400, and new-password login succeeded | Real mail provider delivery and browser recovery flow remain |
| Email verification | Explicit local debug mode issued a one-time token; first confirmation succeeded, replay returned 400, and PostgreSQL persisted `email_verified=true` with a timestamp | Real mail provider delivery and browser verification flow remain |
| Self-service account lifecycle | Fresh user registered; profile read/update returned 200/204, password change returned 204, old password and revoked refresh token returned 401, new password login succeeded, and DELETE `/user/account` returned 204; database retained `deleted` account and three revoked sessions | Concurrent session tests, API-key revocation fixture, retention policy, and browser coverage remain |
| Subscription lifecycle | Plan creation returned a generated identity; purchase returned `active`, exact replay returned `duplicate=true`, a second active purchase returned conflict, cancel and renew each replayed idempotently, and forcing expiry made the next listing `expired`; PostgreSQL held one event for each transition | Payment-provider coupling, quota consumption/grant application to API keys, renewal worker, and browser coverage remain |

## Remaining release gates

1. Coordinate canonical Platform schema changes, generated C# artifacts, Gateway
   vendor schemas, and both digest gates as one cross-repository release change.
2. Run the checked-in empty-volume Compose gate in hosted CI and record current image
   IDs and checksums. The two private repositories require a dedicated read token or
   an independent release repository; the default per-repository `GITHUB_TOKEN`
   cannot check out its sibling.
3. Verify Garnet TLS, cache flush, stale-version recovery, restart, and concurrent
   Gateway/Platform clients. No Redis process, package, image, CLI, or embedded
   fallback may appear in the stack.
4. Persist held/forwarded/output-started evidence and add an audited idempotent
   operator resolution command for unknown charges. Normalize the direct-reset 502
   versus post-cooldown 503 outcome into one public
   error contract, then extend the blocking non-stream matrix to SSE, partial
   output, and actual client disconnect. Inject process loss
   at dispatch, Provider completion, report, SQL commit, and outbox acknowledgement;
   require each restart outcome to settle, safely release, or remain a durable
   reconciliation incident without redispatch.
5. Automate the passing Anthropic and Gemini provider-group scenarios in the empty
   stack, add protocol-specific error/disconnect/golden fixtures, then extend the
   S3-compatible media path with cancellation, restart, restore, and metadata/object
   reconciliation assertions.
6. Run auth-session integration scenarios: refresh-token replay, concurrent rotation,
   logout revocation, expired-session rejection, and multi-device session listing.
7. Run unit, integration, UI, load/soak, failure-recovery, backup/restore, and
   security checks. Any failed child scenario or benchmark makes CI non-zero.

## Evidence rules

Healthy old containers, old databases, route presence, table presence, and swallowed
benchmark failures are not release evidence. Every result must record the current
commit or worktree snapshot, image digests, environment shape, and test exit code.
