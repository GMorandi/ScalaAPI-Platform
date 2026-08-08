# ScalaAPI Rewrite Verification

## Current evidence

| Gate | Result | Interpretation |
| --- | --- | --- |
| Gateway build and CTest | Clean local build; 82/82, exit 0 | Current TCP/TLS client included |
| Platform tests | 63/63, exit 0 | 49 grain and 14 host tests |
| Platform Release build | Passed, 0 warnings and 0 errors | Includes Platform Host, Admin API, migrator, Provider mock, and benchmark assembly |
| Admin Web | Typecheck and production build passed | Blocking CI gate exists; browser tests are not configured |
| Scheduler benchmark dry run | 4/4, exit 0; no-match negative probe exits 1 | Dependency injection and child-result propagation pass; not performance evidence |
| Contract digest | Canonical and Gateway vendor schemas match | Generated C# digest gate remains pending |
| Empty PostgreSQL migrator | Apply then repeated checksum skips, exit 0 for migrations 001-004; 005 applied in Stage-2 runtime | No source database, CDC, or compatibility tables used; clean-image replay of 001-005 remains a release gate |
| Current-image Compose smoke | All long-running services healthy; migrator exit 0 | Isolated project and new volumes |
| Garnet smoke | Auth, PING, SET/GET, PX, INCR, DEL passed | Official digest; no Redis or embedded server |
| Garnet outage/recovery | Platform readiness 503 then 200 | Automatic TCP reconnect verified |
| Provider mock | Health, JSON success, SSE, and 429 passed | Timeout, disconnect, malformed-usage, and adapter golden scenarios remain |
| Gateway dispatch smoke | Readiness 200; seeded OpenAI Chat JSON and SSE returned 200 through Provider mock | Failure/retry matrix and clean-environment automation remain |
| Billable settlement smoke | JSON and SSE completed; usage outbox processed; one NUMERIC ledger debit per lease | Crash/restart and Admin query assertions remain |
| Auth lifecycle smoke | Refresh replay and logout revocation returned 401 on the current image | Concurrent rotation and multi-device HTTP tests remain |

## Remaining release gates

1. Generate the C# contract from the canonical schemas in CI and compare generated
   artifacts, not only schema digests.
2. Automate the complete versioned Compose smoke from empty volumes, including
   current image IDs and a second explicit migrator invocation.
3. Verify Garnet TLS, cache flush, projection rebuild, restart, and concurrent
   Gateway/Platform clients. No Redis process, package, image, CLI, or embedded
   fallback may appear in the stack.
4. Run the seeded billable protocol E2E scenarios for JSON, SSE, duplicate idempotency,
   timeout, upstream 429/500, client disconnect, process restart, and settlement.
5. Run auth-session integration scenarios: refresh-token replay, concurrent rotation,
   logout revocation, expired-session rejection, and multi-device session listing.
6. Run unit, integration, UI, load/soak, failure-recovery, backup/restore, and
   security checks. Any failed child scenario or benchmark makes CI non-zero.

## Evidence rules

Healthy old containers, old databases, route presence, table presence, and swallowed
benchmark failures are not release evidence. Every result must record the current
commit or worktree snapshot, image digests, environment shape, and test exit code.
