# Sub2API -> Platform + Gateway functional parity audit

Audit date: 2026-08-04

## Executive decision

Platform + Gateway cannot replace and shut down Sub2API at the audited commits.

- Strict production-replacement completion: **2.0%** (`3 / 147` weighted points).
- Engineering readiness: **33.5%** (`49.20 / 147` weighted points).
- Capability groups: **1 Complete, 21 Partial, 19 Skeleton, 17 Missing**.
- Open P0 capability blockers: **34**.
- Remaining strict replacement scope: **98.0%** of weighted acceptance points.
- Remaining engineering work: approximately **66.5%** of weighted implementation maturity.

The low strict percentage does not mean that only 2% of the code exists. It means
that a capability counts as complete only when it is behaviorally compatible,
persistent and secure, concurrency/idempotency safe, tested end to end, and
deployable with production evidence. Most target foundations exist, but almost all
product capabilities still miss at least one of those closure conditions.

The detailed source of truth is
[functional-parity-matrix.csv](functional-parity-matrix.csv). This report summarizes
that matrix; percentages must not be edited independently.

## Scope and baseline

| Repository | Audited commit | Working-tree qualification |
| --- | --- | --- |
| Sub2API | `682c4fe0e61b851508fa976ac693e0f68a0639eb` | Two pre-existing untracked CDC migration files were observed and not modified |
| Platform | `1c13045bfd07d26c48bc5d4abf0df7f3d07970cc` | Clean before this audit |
| Gateway | `2d2cc280a19db91fb80f8df4882ab7e1d5e86d05` | Clean |

The denominator is the current Sub2API product surface, grouped into 58 stable
capabilities rather than raw endpoint count. Static inventory found 599 non-test
route registrations in the seven Sub2API route modules, 151 frontend view files,
and 962 top-level service files (529 tests). The target has 89 Minimal API route
registrations, 11 administration page files, and a Gateway router centered on four
implemented generation protocol families. These raw counts are supporting evidence,
not a percentage calculation, because one route may represent either a trivial read
or a complete commercial lifecycle.

### Explicit exclusions

- **Sub2API data migration is not required.** No source rows, credentials, balances,
  orders, or history need to be copied to the target.
- Sub2API itself is read-only audit input and is not changed by this work.
- CDC, Debezium, dual-write, and database cutover are optional infrastructure and
  contribute zero required parity points under the target-only/greenfield decision.
- A feature may be intentionally removed, but it remains incomplete until that
  product-scope removal is explicitly approved. Silent omission is not parity.

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
| Gateway / protocol | 12 | 34 | 0.0% | 32.4% | 10 |
| Authentication / identity | 7 | 16 | 0.0% | 21.9% | 2 |
| Core control plane | 6 | 17 | 0.0% | 55.9% | 5 |
| Usage / billing | 5 | 14 | 0.0% | 37.5% | 4 |
| Commercial | 5 | 9 | 0.0% | 36.7% | 1 |
| Administration / operations | 5 | 11 | 0.0% | 32.3% | 2 |
| Security / risk | 5 | 13 | 0.0% | 29.2% | 3 |
| Frontend | 6 | 13 | 0.0% | 13.8% | 2 |
| Deployment / reliability | 7 | 20 | 15.0% | 37.5% | 5 |
| **Total** | **58** | **147** | **2.0%** | **33.5%** | **34** |

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

This explains the 55.9% engineering-readiness score for the core control plane.

### Gateway gaps are release-blocking

Gateway routing implements Anthropic Messages, OpenAI Chat Completions, a basic
OpenAI Responses route, and Gemini generate/stream. It explicitly returns `501` for
embeddings and images, and every WebSocket upgrade returns `501`. Video and the
remaining image asynchronous/batch lifecycle are absent.

Protocol conversion is incomplete at a deeper level than route coverage:

- Request bodies are converted, but non-stream upstream responses are returned raw.
- Cross-protocol stream direction and Gemini compatibility are incomplete.
- The forwarder always creates POST operations and forwards only provider auth plus
  JSON content type, rather than preserving the required method/header/error contract.
- Proxy URL is used, but the dispatched TLS fingerprint setting is not applied.
- Token usage extraction uses textual key search, which is unsafe as a billing
  authority for arbitrary provider payloads.

Passing Gateway tests do not close these gaps. Two passing tests specifically assert
that embeddings and images return `501`.

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
2. Finish required Gateway endpoints: embeddings, images, realtime, full Responses,
   Gemini, and all required cross-protocol response conversions.
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

The following checks were executed against the frozen target commits:

| Check | Result |
| --- | --- |
| Platform Release build | Passed, 0 warnings and 0 errors |
| Platform tests | Passed, 81/81 (50 grain + 31 host) |
| Admin web typecheck and production build | Passed |
| Gateway build and CTest | Passed, 64/64 |
| Gateway benchmark smoke | Passed; parsing/conversion/cache/outbox benchmarks executed |
| F* + Z3 migration fence verification | Passed; all verification conditions discharged |
| Fresh PostgreSQL migration | Passed; 8 migrations and 43 public tables |
| Migrator idempotency | Passed; second run skipped all 8 checksummed migrations |
| Existing full container stack | PostgreSQL, silo, Admin API, and Gateway healthy after about two days; direct ready/live/metrics and web probes passed |

These results establish build health and a credible engineering base. They do not
override the missing production behaviors listed in the matrix.

## Final conclusion

The migration is in a **foundation-complete / product-parity-incomplete** phase. The
core Orleans/dispatch/lease architecture is worth continuing, but the current target
is not close to a safe legacy shutdown when measured against all existing Sub2API
functions. Planning should use **33.5% engineering readiness** for effort forecasting
and **2.0% strict replacement completion** for release governance. Sub2API must remain
available until the P0 gates are closed; no Sub2API data migration is required to do
that.
