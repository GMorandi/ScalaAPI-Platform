# ScalaAPI Rewrite Verification

## Current evidence

| Gate | Result | Interpretation |
| --- | --- | --- |
| Gateway build and CTest | Clean local build; 84/84, exit 0 | Current TCP/TLS client, bounded response replay, and malformed-usage guard included |
| Platform tests | 71/71, exit 0 | 50 grain and 21 host tests; includes response replay, expiry recovery, and idempotent balance-effect replay |
| Platform Release build | Passed, 0 warnings and 0 errors | Includes Platform Host, Admin API, migrator, Provider mock, and benchmark assembly |
| Admin Web | Typecheck and production build passed | Blocking CI gate exists; browser tests are not configured |
| Scheduler benchmark dry run | 4/4, exit 0; no-match negative probe exits 1 | Dependency injection and child-result propagation pass; not performance evidence |
| Contract digest | Canonical and Gateway vendor schemas match; fixed-scale pricing round-trip test passed | CI regeneration and generated-artifact comparison remain pending |
| PostgreSQL migrator | Current image contains 000-011; repeated invocation skipped all twelve recorded migrations, exit 0 | No source database, CDC, or compatibility tables used; a truly empty-volume replay remains a release gate |
| Current-image Compose smoke | All long-running services healthy; migrator exit 0 | Isolated project and new volumes |
| Garnet smoke | Auth, PING, SET/GET, PX, INCR, DEL passed | Official digest; no Redis or embedded server |
| Garnet outage/recovery | Platform readiness 503 then 200 | Automatic TCP reconnect verified |
| Garnet projection rebuild | `discovered=12`, `written=12`, `deleted=0`, `errors=0`; immediate `scalaapi:v1:auth:*` read succeeded | Flush, stale-version, TLS, and multi-client assertions remain |
| Provider mock | Health, JSON success, SSE, malformed-usage `502`, fresh 500/429 exhaustion returned `503 provider_unavailable` with distinct `:retry:N` leases, and cancellable timeout returned `502` after 30.3s with an aborted lease and zero usage events | Disconnect, bounded retry assertions for both protocols, and adapter golden scenarios remain |
| Gateway dispatch smoke | Readiness 200; seeded OpenAI Chat JSON and SSE returned 200 through Provider mock | Failure/retry matrix and clean-environment automation remain |
| Billable settlement smoke | JSON and SSE completed; durable hold committed; usage outbox processed; one NUMERIC ledger debit per lease | Crash/restart, disconnect, and clean-seed automation remain |
| Request idempotency smoke | Concurrent same-key calls produced one 200 and one active-lease 409; after settlement a matching retry returned the original 200 body with `Cache-Control: no-store`; different fingerprint produced 409 conflict; one lease/usage/debit/hold per key | Crash recovery before lease expiry, streaming replay semantics, and expiry reconciliation remain |
| Admin settlement queries | Ledger, lease, and hold endpoints returned current PostgreSQL rows with user filters | Pagination/export and browser assertions remain |
| Auth lifecycle smoke | Refresh replay and logout revocation returned 401 on the current image | Concurrent rotation and multi-device HTTP tests remain |
| Redeem-code settlement smoke | First redemption returned 200; repeat returned 409; after a Silo contract restart a committed redemption remained 409 and replayed its balance effect; one redemption and one NUMERIC ledger row were observed | Concurrent HTTP contention and audit-event assertions remain |
| Provider failover idempotency | Matching external idempotency keys reopened after aborted leases; active/completed keys retain replay/conflict semantics; Host coverage passed | Expiry reconciliation and failover response replay remain |
| Password recovery | Explicit local debug mode issued a one-time token; first confirmation returned 204, token replay returned 400, and new-password login succeeded | Real mail provider delivery and browser recovery flow remain |
| Email verification | Explicit local debug mode issued a one-time token; first confirmation succeeded, replay returned 400, and PostgreSQL persisted `email_verified=true` with a timestamp | Real mail provider delivery and browser verification flow remain |

## Remaining release gates

1. Generate the C# contract from the canonical schemas in CI and compare generated
   artifacts, not only schema digests.
2. Automate the complete versioned Compose smoke from empty volumes, including
   current image IDs and a second explicit migrator invocation.
3. Verify Garnet TLS, cache flush, stale-version recovery, restart, and concurrent
   Gateway/Platform clients. No Redis process, package, image, CLI, or embedded
   fallback may appear in the stack.
4. Run the seeded billable protocol E2E scenarios for JSON, SSE, duplicate idempotency
   including delayed response replay, timeout, upstream 429/500, client disconnect,
   process restart, durable hold reconciliation, and settlement.
5. Run auth-session integration scenarios: refresh-token replay, concurrent rotation,
   logout revocation, expired-session rejection, and multi-device session listing.
6. Run unit, integration, UI, load/soak, failure-recovery, backup/restore, and
   security checks. Any failed child scenario or benchmark makes CI non-zero.

## Evidence rules

Healthy old containers, old databases, route presence, table presence, and swallowed
benchmark failures are not release evidence. Every result must record the current
commit or worktree snapshot, image digests, environment shape, and test exit code.
