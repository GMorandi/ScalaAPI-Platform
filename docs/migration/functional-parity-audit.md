# Sub2API -> Platform + Gateway functional parity audit

Audit date: 2026-08-05

## Executive decision

Platform + Gateway cannot replace and shut down Sub2API at the audited commits.

- Strict production-replacement completion: **2.0%** (`3 / 147` weighted points).
- Engineering readiness: **40.1%** (`58.95 / 147` weighted points).
- Capability groups: **1 Complete, 28 Partial, 16 Skeleton, 13 Missing**.
- Open P0 capability blockers: **34**.
- Remaining strict replacement scope: **98.0%** of weighted acceptance points.
- Remaining engineering work: approximately **59.9%** of weighted implementation maturity.

The low strict percentage does not mean that only 2% of the code exists. It means
that a capability counts as complete only when it is behaviorally compatible,
persistent and secure, concurrency/idempotency safe, tested end to end, and
deployable with production evidence. Most target foundations exist, but almost all
product capabilities still miss at least one of those closure conditions.

The detailed source of truth is
[functional-parity-matrix.csv](functional-parity-matrix.csv). This report summarizes
that matrix; percentages must not be edited independently.

## Scope and baseline

| Repository | HEAD inspected | Working-tree qualification |
| --- | --- | --- |
| Sub2API | `682c4fe0e61b851508fa976ac693e0f68a0639eb` | Two untracked CDC outbox files are present; they were observed read-only and are not part of target scoring |
| Platform | `ec2951c` | 22 modified/untracked files contain the current Cap'n Proto, media, usage, pricing and test work; changes are not committed or deployed |
| Gateway | `2d2cc28` | 25 modified/untracked files contain the current protocol, forwarding, WebSocket, media billing and test work; changes are not committed or deployed |

This audit distinguishes two states: the current working tree is used to assess
implementation readiness, while runtime/deployment claims require an image and
database created from that same tree. A passing local test therefore does not imply
that the long-running Compose stack contains the same code or schema.

The denominator is the current Sub2API product surface, grouped into 58 stable
capabilities rather than raw endpoint count. The current source inventory found
604 rough route registrations in the route modules, 968 backend service Go files,
1,001 Go test files, and 151 frontend view files. The target has 10 Platform
migrations, 83 C#/test source files, 57 Gateway C++ source/header files, and a
Gateway capability registry spanning the four generation protocol families plus
embeddings, media, models, search, and realtime entry points. These raw counts are
supporting evidence, not a percentage calculation, because one route may represent
either a trivial read or a complete commercial lifecycle.

### Explicit exclusions

- **Sub2API data migration is not required.** No source rows, credentials, balances,
  orders, or history need to be copied to the target.
- Sub2API itself is read-only audit input and is not changed by this work.
- CDC, Debezium, dual-write, and database cutover are optional infrastructure and
  contribute zero required parity points under the target-only/greenfield decision.
- The observed Sub2API `migration_cdc_outbox.sql` working-tree addition is not
  applied to the target database and does not count as target implementation.
- A feature may be intentionally removed, but it remains incomplete until that
  product-scope removal is explicitly approved. Silent omission is not parity.

## Current runtime snapshot

The long-running Compose stack is healthy at the process level, but it is stale
relative to the current working trees:

- Gateway `/live`, `/ready`, and `/metrics` returned HTTP 200; Admin Web returned
  HTTP 200. Platform and Admin API host ports are not exposed on the host, so their
  host-side probes were not equivalent to the container healthchecks.
- The running images were created on 2026-08-01/02 and have been up for about three
  days. They were not rebuilt from the current uncommitted Platform/Gateway files.
- The active PostgreSQL `schema_migrations` table contains `000-orleans` through
  `007-schema-parity-and-cdc-ordering`; current working-tree migrations `008-010`
  are not present in the running database.
- Compose contains one Gateway and one Platform silo, no S3-compatible object
  storage, no provider mock, no ingress TLS/backup service, and no multi-instance
  Gateway test topology.

## Scoring method

Priorities are weighted P0=3, P1=2, P2=1. Status scores are:

| Status | Strict credit | Readiness credit | Meaning |
| --- | ---: | ---: | --- |
| Complete | 100% | 1.00 | Production-compatible closure with tests and deployment evidence |
| Partial | 0% | 0.60 | Main path exists, but material behavior or production evidence is missing |
| Skeleton | 0% | 0.25 | Schema/CRUD/stub exists without the operational lifecycle |
| Missing | 0% | 0.00 | No usable target implementation |

Strict completion counts only Complete rows. Any incomplete P0 row blocks legacy
shutdown. Engineering readiness is a planning signal, not permission to promote
traffic.

## Results by domain

| Domain | Groups | Weight | Strict completion | Engineering readiness | P0 blockers |
| --- | ---: | ---: | ---: | ---: | ---: |
| Gateway / protocol | 12 | 34 | 0.0% | 57.9% | 10 |
| Authentication / identity | 7 | 16 | 0.0% | 21.9% | 2 |
| Core control plane | 6 | 17 | 0.0% | 55.9% | 5 |
| Usage / billing | 5 | 14 | 0.0% | 45.0% | 4 |
| Commercial | 5 | 9 | 0.0% | 36.7% | 1 |
| Administration / operations | 5 | 11 | 0.0% | 32.3% | 2 |
| Security / risk | 5 | 13 | 0.0% | 29.2% | 3 |
| Frontend | 6 | 13 | 0.0% | 13.8% | 2 |
| Deployment / reliability | 7 | 20 | 15.0% | 37.5% | 5 |
| **Total** | **58** | **147** | **2.0%** | **40.1%** | **34** |

## Implementation state

### What is substantially implemented

The target is not a demo shell. Its strongest area is the control-plane foundation:

- C++ Gateway request parsing, API-key authentication, Cap'n Proto dispatch,
  account failover, streaming plumbing, metrics, and a durable local usage outbox.
- Orleans grains for users, keys, groups, accounts, scheduling, rate/concurrency
  controls, sticky routing, and invalidation.
- Durable PostgreSQL request leases, usage events, settlement outbox, retries, and
  dead-letter storage.
- Administration APIs and UI for basic accounts, groups, users, API keys, dashboard,
  and configuration.
- Email registration/login, GitHub/Google callback handling, and TOTP APIs.
- An idempotent target schema migrator with checksum protection.
- CI definitions for Platform, Gateway, schema verification, and an F*/Z3 migration
  fence proof.
- Append-only Cap'n Proto v2 dispatch/target/usage fields with v1 request/response
  compatibility, provider capability filtering, and durable `media_operations`
  persistence foundations.
- Gateway capability registry coverage for Responses subpaths, Gemini model routes,
  Alpha Search, embeddings, image/video task paths, Codex/Live routes, and realtime
  WebSocket upgrades.
- Structured provider usage extraction, response-header/status passthrough, basic
  cross-protocol non-stream conversion, bidirectional Photon WebSocket bridging, and
  image/video/realtime usage fields in the Platform settlement path.
- Durable image/video operation lifecycle with request fingerprint idempotency,
  PostgreSQL polling claims, bounded provider polling, expiry, terminal-state
  protection, local cancellation, and settlement-before-success ordering.
- Configuration-backed decimal token/media/video/realtime prices; requests for an
  unpriced model are rejected before account selection or lease creation instead of
  using a fabricated fallback rate.

This explains the 55.9% engineering-readiness score for the core control plane.

### Gateway progress and remaining release blockers

The previous `501` stubs have been removed from the approved route surface. The
capability registry now validates method/path combinations before dispatch, and the
Gateway/Platform contract carries protocol, operation, headers, upstream method,
media-task, realtime-session, and extended usage metadata. Responses now have a
basic non-stream conversion path; SSE conversion handles arbitrary line endings and
provider usage JSON; WebSocket upgrades use Photon's server/client APIs and bridge
frames in both directions.

This is an integration foundation, not production parity. The remaining blockers are:

- Responses safe POST subpaths, Alpha Search, Gemini model list/detail, and query
  preservation are routed, but full compact/input-items and Gemini response/tool/
  safety contracts are not complete.
- Embeddings validates string/array input, `encoding_format`, `dimensions`, `user`,
  and model before capability-filtered dispatch; provider golden fixtures and live
  E2E are still open.
- Image/video async routes now have durable operation IDs, polling, expiry, local
  cancel/delete control, idempotent replay, and terminal settlement. Upstream
  cancellation, complete batch item semantics, S3-compatible byte storage/download,
  and multi-Gateway E2E remain open.
- Realtime has a bidirectional bridge and usage fields, but no live provider mock,
  fragmentation/backpressure soak, or full lease settlement/disconnect E2E.
- Provider method/query/header/status forwarding is substantially improved; TLS
  fingerprint profile execution, proxy-secret redaction, and live proxy/error
  fidelity remain.
- `count_tokens` still needs provider-native counting plus a versioned tokenizer
  fallback; no token count may be treated as authoritative until that contract is
  implemented.

No current Gateway source or test asserts the old `501` behavior. Passing unit tests
still do not prove a live provider, Platform, Garnet, PostgreSQL, or multi-Gateway
workflow.

### Identity and product workflows are incomplete

Target authentication covers a useful subset, but it has no refresh-token rotation,
logout/session revocation, passkeys, email verification, password reset, complete
profile/identity binding, or equivalent abuse controls. OAuth handles only
GitHub/Google callback exchange, not Sub2API's full provider/start/state/pending/bind
lifecycle.

Platform's commercial and operational endpoint file contains many schemas and CRUD
handlers, but important features are placeholders rather than lifecycles:

- Payments create a pending order and allow manual administrator confirmation; no
  provider adapters, signed webhooks, refunds, retry, or reconciliation exist.
- Subscription APIs only create/list plans and list users; purchase, assignment,
  renewal, expiry, and quota reset are absent.
- Channel monitoring and operations metrics accept manual records instead of running
  collectors, schedules, alerts, and feedback loops.
- Content audit is a standalone substring check and is not enforced in Gateway.
- System update reports what would be downloaded but does not perform a controlled
  update/rollback; backup and restore are absent.
- The only frontend is a compact administration UI. There is no user portal,
  authentication/recovery UI, commercial UI, or operations/risk UI.

## Architecture assessment

### Sound decisions

The high-level split is appropriate for the workload:

```text
client -> C++ Gateway -> Cap'n Proto/UDS -> Orleans control plane -> PostgreSQL
                     \-> durable SQLite usage outbox
admin -> Admin API -> Orleans/PostgreSQL
```

The Gateway can remain focused on low-latency protocol I/O while Orleans owns keyed
coordination and PostgreSQL owns durable business facts. Grain single-threaded
activation, durable request leases, and idempotent settlement tokens are a reasonable
base for distributed scheduling and accounting. Cap'n Proto over a local Unix socket
also keeps the hot-path boundary explicit and versionable.

### Risks that the framework does not solve automatically

Orleans distribution is not equivalent to production correctness by itself:

- Several money/rate contracts still expose `double`, although selected internal
  states use `decimal`. Payment and redemption write a SQL ledger and then adjust an
  Orleans balance across a separate failure boundary.
- Some concurrency, RPM, lease-hold, and active-slot state is activation memory. Its
  behavior across deactivation, restart, duplicate delivery, and rolling deployment
  needs explicit persistence/reconciliation tests.
- Scheduler selection checks a narrow account projection and lacks the full legacy
  account/provider health and rate-limit policy.
- Admin business capabilities are concentrated in one large endpoint module. CRUD
  presence currently hides missing domain state machines and makes authorization,
  transaction, and audit boundaries hard to review.
- The Gateway and Platform have unit tests in separate repositories, but no release
  gate proves their Cap'n Proto/API compatibility or a complete live request.

### CDC and F* under the no-data-migration decision

The F* model is valid for the state machine it describes, and local verification
discharged all proof obligations. It proves legal fence transitions and single-writer
control properties; it does not prove CDC throughput, database performance, billing
correctness, or product parity.

More importantly, the current target schema initializes the fence to
`sub2api/legacy_primary`. That matches a source-data cutover plan but conflicts with
the explicit greenfield requirement. A new target deployment should have a separate,
audited initialization path that starts Platform as the only writer without requiring
a source snapshot, CDC checkpoint, or Sub2API database. Keep the proven cutover model
as an optional future mode, not as a startup dependency or completion gate.

Using F* to model money/order transitions could be valuable after the executable
state machine is specified. It cannot compensate for a missing transactional design,
provider contracts, or load/fault evidence. The best next formal target is a small
ledger/order invariant model, while performance remains the responsibility of the
runtime architecture and benchmarks.

## P0 release gates

The 34 row-level P0 blockers should be managed through these ten release gates:

1. Freeze a protocol contract corpus from the retained Sub2API behavior, including
   status, headers, streaming events, errors, cancellation, and usage accounting.
2. Finish required Gateway behavior: embedding conversion, image/video task
   lifecycles, realtime session settlement, full Responses/Gemini contracts, and all
   required cross-protocol response conversions. Route registration alone is not a
   release gate.
3. Make money and quota contracts decimal, version pricing, and make ledger/order/
   balance transitions atomic or recoverably idempotent.
4. Complete API-key, group, account, scheduler, health, rate-limit, and multi-silo
   semantics under restart and duplicate delivery.
5. Complete email/session authentication and build the user portal needed to operate
   keys, usage, profile, subscriptions, and orders.
6. Implement payment provider/webhook/refund/reconciliation and subscription state
   machines if full Sub2API product parity remains the approved scope.
7. Put moderation, audit, proxy/TLS, secret redaction, and distributed abuse controls
   on actual runtime paths.
8. Replace manual ops records with collectors, alerts, logs, monitors, backup/restore,
   and operator UI.
9. Add a documented target-only bootstrap mode with no Sub2API database, CDC broker,
   connector, snapshot, or fence promotion dependency.
10. Gate releases on full-stack E2E, representative load, node/database/cache failure,
    rolling deployment, restore, and legacy-shutdown rehearsals with measured SLOs.

Legacy shutdown is permitted only when every P0 matrix row is Complete, the strict
weighted score is 100% for the approved product scope, and the final shutdown drill
has a tested rollback that does not depend on restoring Sub2API data.

## Verification evidence

The following checks were executed against the current working tree on 2026-08-05;
historical G0/CDC evidence below remains unchanged and is not reinterpreted as
production parity:

| Check | Result |
| --- | --- |
| Platform Release build | Passed, 0 warnings and 0 errors |
| Platform tests | Passed, 90/90 (50 grain + 40 host) in the current working tree; PostgreSQL media coverage was run separately against a temporary database |
| Sub2API Go backend tests | Not run: `go` is not installed in the current environment |
| Sub2API frontend typecheck/build | Not run: `pnpm` and `frontend/node_modules` are unavailable |
| Gateway build and CTest | Passed, 79/79 |
| Cap'n Proto compatibility | Passed; append-only v1/v2 dispatch, query, media control, pricing reject, and usage fields are serialized |
| Gateway protocol tests | Passed; response conversion, arbitrary-boundary SSE usage, multipart model, embeddings, realtime model, and media billing parsing are covered |
| Media/usage lifecycle | Passed in the current working tree against a temporary PostgreSQL database; the active Compose database has not received migrations `008-010` |
| F* + Z3 migration fence verification | Passed; all verification conditions discharged |
| Fresh PostgreSQL migration | Passed previously for checksummed migrations `001`-`010`; the current running Compose database is only at `007` |
| Migrator idempotency | Passed previously for migrations `001`-`010`; must be repeated after rebuilding current images |
| Existing full container stack | Containers are healthy and Gateway probes pass, but images were created before the current working-tree changes; no live provider or current-tree E2E was executed |

These results establish build health and a credible engineering base. They do not
override the missing production behaviors listed in the matrix.

## Final conclusion

The migration is in a **foundation-complete / product-parity-incomplete** phase. The
core Orleans/dispatch/lease architecture is worth continuing, but the current target
is not close to a safe legacy shutdown when measured against all existing Sub2API
functions. Planning should use **40.1% engineering readiness** for effort forecasting
and **2.0% strict replacement completion** for release governance. Sub2API must remain
available until the P0 gates are closed; no Sub2API data migration is required to do
that.
