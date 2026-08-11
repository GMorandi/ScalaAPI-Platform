# ScalaAPI Feature Implementation Gap Report

This report is the concise result of the 2026-08-11 re-investigation. It compares
the new ScalaAPI product with the useful capability catalogue in read-only
`sub2api@43ec48d`; it does not require API, schema, identifier, state, deployment,
or data compatibility with Sub2API.

## Baseline

| Item | Current evidence |
| --- | --- |
| Platform | Documentation baseline `e7bcf09`; production implementation through `651a786`; clean at investigation start |
| Gateway | `04ec18c`; clean at investigation start |
| Sub2API | `43ec48d`; clean and read-only |
| ScalaAPI code surface | 132 production C# files, 72 test/benchmark C# files, 142 direct Admin API route declarations, 62 product tables, 22 SQLSugar entity types, 28 Admin Web TS/TSX files with 16 pages, and 21 User Web TS/TSX files with 14 pages |
| Gateway surface | 52 production C++ source/header files, 11 test source files, one revision-3 Cap'n Proto contract set, and 127 CTest cases |
| Reference scope signal | 647 production Gin route declarations in the primary route/handler surface, 39 Ent schemas, 289 Vue files including 59 lazy router imports, and 240 SQL migrations |
| Inventory result | 58 domains: 2 implemented, 55 partial, 1 skeleton, 0 missing |
| Priority result | P0: 35 total, 2 implemented and 33 partial; P1: 19 total, 18 partial and 1 skeleton; P2: 4 partial |
| Risk result | 31 tracked risks: 3 open, 25 partial, and 3 controlled; the database test false-positive risk is now P0/open |

The reference counts are breadth signals only. ScalaAPI promotion is based on its
own contract, state machine, automated tests, and current-source runtime evidence,
not a route-count parity percentage.

## Verification Status

| Gate | Result |
| --- | --- |
| Platform Release build | Pass, 0 warnings and 0 errors |
| Platform ordinary test run | 294/294 pass, but 33 files return early without `GREENFIELD_SCHEMA_CONNECTION`; not sufficient integration evidence |
| Fresh PostgreSQL 17 migrator | Pass: 51 records applied, then all 51 skipped on replay |
| Fresh database-enabled solution | Fail: 292 passed, 2 failed |
| Failing integration tests | Invalid 1+1 worker-claim assertion in `ConcurrentWorkersSerializeClaimsAndPublishEachRevisionOnce`; generated media rows not removed before lease cleanup in `BatchListIsOwnerScopedAndReturnsDurableOperations` |
| Gateway | Build pass and CTest 127/127 |
| Web | Admin Web and User Web typecheck/build pass |
| Contracts and dependency retirement | Digests pass; generated C# matches Cap'n Proto 1.0.2 / capnpc-csharp 1.3.118; retired dependency scan passes |
| Benchmark integrity | Four Scheduler Dry children execute and exit zero; Dry results are integrity evidence, not performance evidence |
| Latest complete runtime matrix | `scalaapi-credential-cancel-0811d` passed from empty volumes on Platform `651a786` / Gateway `04ec18c`; current release promotion remains blocked by the fresh database-test failures |
| Investigation cleanup | The disposable PostgreSQL audit container and migration directory were removed; `podman ps -a` is empty |

## Blocking Gap Order

1. Restore a truthful database-enabled CI gate. Fix the two deterministic Host
   tests, isolate shared database state, and stop reporting integration tests as
   passes when prerequisites are absent.
2. Finish the one-hour two-Silo media contention/rejoin gate and deployment-scale
   HA/offsite object lifecycle evidence.
3. Complete OpenAI Responses mutation semantics and the full video lifecycle.
4. Replace mock-only confidence with live Provider profiles, provider-owned pricing,
   tokenizers/catalogues, outbound proxy/TLS fingerprint application, and secret
   rotation drills.
5. Close identity, commercial, operations, and UI workflows with real browser,
   notification, payment, abuse, recovery, and authorization evidence.
6. Make the cross-repository empty-volume matrix blocking in hosted CI and complete
   long load/backpressure, rolling replacement, backup/offsite restore, and rollback
   drills.

## Gateway And Protocol

| ID | Priority | Status | Implemented now | Gap to `implemented` |
| --- | --- | --- | --- | --- |
| GW-01 | P0 | partial | Native Anthropic JSON/SSE, count tokens, auth isolation, usage/terminal handling, fault matrix, terminal OAuth revoke/recovery, and Provider-side cancellation evidence | Live Anthropic credentials, provider pricing/tokenizer fixtures, multi-Silo refresh contention, and long-stream/load evidence |
| GW-02 | P0 | partial | OpenAI Chat JSON/SSE, strict terminal/media checks, timers, normalized errors, durable replay, late usage, and several crash boundaries | Replay after replacement across every remaining crash boundary, live Provider/TLS evidence, and long backpressure soak |
| GW-03 | P0 | partial | Responses root JSON/SSE, compact, get, input-items, cancel, delete, replay, usage, and malformed-success handling | Remaining mutation lifecycle, full provider fault/runtime matrix, cross-protocol fixtures, and live adapter evidence |
| GW-04 | P0 | partial | Native Gemini JSON/SSE/catalogue, auth isolation, usage/terminal handling, fault matrix, terminal OAuth revoke/recovery, and Provider-side cancellation evidence | Live Gemini credentials, provider pricing/tokenizer fixtures, multi-Silo refresh contention, and long-stream/load evidence |
| GW-05 | P0 | partial | Sixteen pairwise request/response conversions, error conversion, same-format passthrough, and representative stream goldens | Malformed/header edge semantics and runtime E2E for every provider pair |
| GW-06 | P1 | partial | OpenAI/Gemini model validation, Gemini list/detail, Anthropic count tokens, bounded control operations, and no-charge release | Versioned catalogue authority, provider tokenizer goldens, live runtime errors, and production refresh policy |
| GW-07 | P0 | partial | Bounded Embeddings validation, float/base64 results, Jina/Gemini profiles, price-aware settlement, and malformed-response reconciliation | Live adapters and provider-specific production fidelity/runtime evidence |
| GW-08 | P0 | partial | Sync/async/batch image lifecycle, S3 item/archive storage, cancellation, retention, reconciliation, partial-write recovery, partitions, and short two-Silo contention | Fix the database cleanup test, run the full 3600-second contention gate, and prove deployment-scale HA/offsite lifecycle |
| GW-09 | P1 | partial | Video create/edit/extend, durable polling, MP4 storage metadata, signed access, and object verification foundations | Cancel/delete semantics, unit pricing, restart/restore, retention/orphan cleanup, reconciliation, and complete E2E |
| GW-10 | P0 | partial | Realtime WebSocket/calls dispatch, Provider handshake/usage, exact settlement, retry policy, and a four-session three-second soak | Long load/backpressure, replay after process replacement, shutdown behavior, and wider Provider evidence |
| GW-11 | P0 | partial | API-key auth, failover, durable lease/hold/outbox, immutable evidence, late settlement, operator resolution, cross-Gateway idempotency, and many crash hooks | Remaining exact-boundary crashes, TLS and cache outage under load, broader multi-instance recovery, and long soak |
| GW-12 | P0 | partial | Provider-native method/header/status fixtures, bounded target headers, proxy/TLS profile administration, and Anthropic/Gemini fault groups | Live outbound adapters, actual proxy and TLS fingerprint application, Provider production evidence, rotation, and scans |

## Identity

| ID | Priority | Status | Implemented now | Gap to `implemented` |
| --- | --- | --- | --- | --- |
| AUTH-01 | P0 | partial | Bounded email/password registration and login, password hashing, duplicate handling, PostgreSQL abuse counters, lockout, and reset | Browser/mail evidence, anti-enumeration, and limits for all public endpoints |
| AUTH-02 | P0 | partial | Hashed refresh rotation, replay/concurrent-loser rejection, logout and immediate session revocation | Multi-device/browser UX, session audit, retention, and cross-instance lifecycle drills |
| AUTH-03 | P1 | partial | PostgreSQL TOTP setup/verify/login/disable, time-step replay rejection, backup codes, and distributed lockout | Recovery-code endpoint/browser UX, notification, and full recovery scenarios |
| AUTH-04 | P1 | partial | Fido2 registration/authentication, one-shot challenges, public credentials, counters, audit, and User Web conversion | Real browser authenticator ceremony, anti-enumeration, and distributed abuse limits |
| AUTH-05 | P1 | partial | Email verification/password reset state, encrypted leased mail outbox, supersession, retry, and User Web action links | Live SMTP/provider TLS/auth, browser receipt/expiry, metrics/alerts, retention, and abuse limits |
| AUTH-06 | P1 | partial | GitHub/Google-style OAuth PKCE, hashed one-shot state, provider-bound callback, mock exchange, and replay rejection | Production redirect allowlists, account-link collision policy, more provider profiles, and browser evidence |
| AUTH-07 | P1 | partial | Profile read/update, password change with other-session revocation, and password-confirmed soft deletion | Concurrent session/API-key revocation, retention and erasure policy, and browser evidence |

## Core Control Plane

| ID | Priority | Status | Implemented now | Gap to `implemented` |
| --- | --- | --- | --- | --- |
| CORE-01 | P0 | partial | Zero-balance user creation and audited, idempotent NUMERIC balance adjustment | Complete role/authorization matrix, bulk administration, and browser workflows |
| CORE-02 | P0 | partial | Scoped/expiring API keys, create/update/rotate/revoke, audit, denied-capability evidence, and no-lease rejection | Multi-instance contention and complete Admin/User browser mutation matrix |
| CORE-03 | P0 | partial | Persisted groups, exact/prefix/wildcard routing, RPM, peak multipliers, fallback chains, and cycle protection | Authenticated CRUD validation, projection/rebuild evidence, and multi-Silo fallback contention |
| CORE-04 | P0 | partial | Encrypted semantic credentials, OAuth refresh CAS, terminal revoke/secret clearing, audit, and explicit replacement recovery | Live Provider profiles, master-key rotation, multi-Silo refresh contention, and operator UI evidence |
| CORE-05 | P0 | partial | Capability/priority/load scheduling, sticky routing, account/user concurrency, RPM, and fallback | Distributed rate-window/lease contention plus HTTP fault/recovery under multiple Silos |
| CORE-06 | P1 | partial | Bounded versioned runtime configuration, secret-key rejection, stale-write conflict, and actor audit | Real dynamic consumers, reload propagation, rollout semantics, and browser controls |

## Billing

| ID | Priority | Status | Implemented now | Gap to `implemented` |
| --- | --- | --- | --- | --- |
| BILL-01 | P0 | partial | PostgreSQL lease/hold/usage/outbox authority, immutable evidence, charge-aware abort, late settlement, reconciliation, operator resolution, and many crash replays | Remaining exact crash/ack boundaries, long multi-Silo soak, and full adapter coverage |
| BILL-02 | P0 | partial | Immutable NUMERIC price snapshots, cache/reasoning token fields, admin precedence, and bounded mock Provider catalogue refresh | Live provider pricing rules/catalogues, tokenizer goldens, alias policy, and multi-provider E2E |
| BILL-03 | P0 | partial | API-key absolute/rolling quotas and transactionally reserved/settled subscription grants | Provider-price coupling, every-protocol invalidation, quota reconciliation, multi-Silo contention, and browser proof |
| BILL-04 | P0 | partial | Per-user NUMERIC authority, monotonic ledger, idempotent effects, hold oversubscription protection, drift incidents, repair, and concurrent operator decisions | Long multi-Silo crash/settlement soak and complete commercial/media effect coverage |
| BILL-05 | P1 | partial | Usage aggregates, scoped queries, exports, retention/cleanup foundations, and dashboard surfaces | Complete aggregation correctness, scheduled cleanup, immutable retention, browser export, and load evidence |

## Commercial

| ID | Priority | Status | Implemented now | Gap to `implemented` |
| --- | --- | --- | --- | --- |
| COM-01 | P0 | partial | Mock and Stripe checkout/refund adapters, signed webhook normalization, pending/retry recovery, partial refunds, ledger effects, and actor audit | More production adapters, secret rotation, real browser payment completion/reconciliation, and exact crash injection |
| COM-02 | P1 | partial | Plan purchase/list/cancel/renew/expiry, one-active rule, quota reservations, renewal worker, and event replay | External payment confirmation, quota reconciliation, failure recovery, and browser workflows |
| COM-03 | P1 | partial | Transactional redeem effects with ordered accounting and replay/conflict protection | Concurrent HTTP contention, promotion policy/expiry/limits, operator audit, and browser workflows |
| COM-04 | P2 | partial | Atomic idempotent Admin referral reward with dual-user locks, one-attribution checks, NUMERIC credit, and audit | Signup attribution, anti-abuse, automatic rebate and transfer lifecycle, exports, and browser workflows |
| COM-05 | P2 | partial | Published/unexpired announcements, per-user read state, idempotent audit, and dashboard display | Targeting, scheduling, delivery/expiry, browser authorization, and commercial audit |

## Operations

| ID | Priority | Status | Implemented now | Gap to `implemented` |
| --- | --- | --- | --- | --- |
| OPS-01 | P0 | partial | Core CRUD, dashboard/config, safe Provider account forms, balance actions, and selected Admin browser routes | Full authorization matrix, bulk/filter workflows, refresh audit UI, and live browser coverage |
| OPS-02 | P0 | partial | Authenticated metrics, fixed-label classifier snapshots, budgets/alerts, multi-process evidence, and Operations UI | Complete collectors, traces/correlation, alert delivery/recovery, credential redaction, long-stream metrics, and live auth |
| OPS-03 | P1 | partial | Authenticated manual channel checks, bounded history, audit, and Admin history/check UI | Scheduled runners, templates, notification/feedback loop, broader history, and live authorization |
| OPS-04 | P1 | skeleton | Local PostgreSQL backup jobs, checksums, isolated restore command, and basic Admin controls exist | Signed/encrypted offsite retention, measured RPO/RTO, restore faults, service restart/update, rolling rollout, and rollback drills |
| OPS-05 | P2 | partial | Bounded user export and audited, idempotent, dry-run maintenance cleanup | Scheduling, immutable retention, object/media cleanup, browser download authorization, and maintenance metrics |

## Security

| ID | Priority | Status | Implemented now | Gap to `implemented` |
| --- | --- | --- | --- | --- |
| SEC-01 | P0 | partial | Request/response policy enforcement, normalization, local/external/OpenAI classifiers, alerts, revision propagation/rebuild, metrics, and Admin UI | Repair the current concurrency test gate, then prove separate-process ordering, credential rotation/redaction, live browser auth, policy-error UX, and long streams |
| SEC-02 | P1 | partial | Actor audit for critical mutations plus bounded recursive-redacted audit list/export | Immutable storage/retention, export authorization/browser tests, safe-error matrix, and security scans |
| SEC-03 | P0 | partial | Encrypted proxy credentials, validated TLS profile metadata, safe projections, and actor audits | Apply proxy/TLS fingerprints in real Provider transport, rotate/retain secrets, prove browser authorization, and scan |
| SEC-04 | P0 | partial | AES-GCM protected credentials, secret-free projections/errors, JWT role/session checks, and redaction tests | Master-key rotation, operator step-up/fresh-role checks, deployment secret lifecycle, and end-to-end hardening scans |
| SEC-05 | P1 | partial | Cross-instance login/registration counters, lockout/reset, bounded Retry-After, and smoke evidence | Limits and anti-enumeration for every public endpoint plus browser and alert evidence |

## Frontend

| ID | Priority | Status | Implemented now | Gap to `implemented` |
| --- | --- | --- | --- | --- |
| UI-01 | P0 | partial | Sixteen Admin pages cover core CRUD, accounts, reconciliation, policy, operations, monitors, backup, and config | Complete authorization/bulk workflows, credential audit and remaining operational pages, and live browser matrix |
| UI-02 | P0 | partial | Fourteen User pages cover dashboard, usage, keys, profile/security, billing, models/status/legal, and auth/recovery | Mutation-heavy key/export/billing/subscription/order workflows and cross-user authorization in a real browser |
| UI-03 | P1 | partial | Registration/login/logout, refresh handling, OAuth callback, recovery/verification, TOTP, and Passkey controls | Backup-code recovery, real mail and WebAuthn ceremonies, notification, revocation, and browser failure paths |
| UI-04 | P1 | partial | Order/provider selection, checkout link, plans, subscription actions, redeem, referral summary, and refund API boundary | Real payment completion, more providers, quota coupling, referral settlement/transfer, audit, and browser matrix |
| UI-05 | P1 | partial | Reconciliation, content policy, Operations, monitor, and backup pages with selected intercepted Playwright tests | Live authorization, operator audit visibility, scheduled-monitor UI, restore/recovery workflow, and end-to-end browser evidence |
| UI-06 | P2 | partial | Public models/status/terms/privacy routes and source-built anonymous browser smoke | Deployment legal configuration, accessibility scans, catalogue failure UX, and production ingress evidence |

## Deployment And Reliability

| ID | Priority | Status | Implemented now | Gap to `implemented` |
| --- | --- | --- | --- | --- |
| DEP-01 | P0 | implemented | Empty PostgreSQL 17 applies Orleans plus product migrations 001-050 and replays all 51 records idempotently | Keep every future forward migration in the same double-run/schema gate |
| DEP-02 | P0 | implemented | Bootstrap has no Sub2API data, CDC, Debezium, snapshot, old key, Redis, or compatibility dependency | Preserve the clean-room invariant on every release |
| DEP-03 | P0 | partial | Compose starts PostgreSQL, migrator, Garnet, MinIO, Provider mock, Platform, Admin API, Gateway, and both Web apps with health/readiness and broad fault probes | Restore the red DB test gate, broaden production metrics/config/fault assertions, and host the full stack gate |
| DEP-04 | P0 | partial | Two active Silos/Gateways, outage/rejoin, media claim fencing, rootless partitions, and short repeated rejoin evidence | Full one-hour contention, primary rolling replacement, quorum/failover measurement, and deployment-scale HA |
| DEP-05 | P0 | partial | Idempotent local backup/restore with SHA-256 and live-authority target rejection | TLS ingress, encrypted/signed offsite backup, key rotation, measured RPO/RTO, restore faults, DR and rollback drills |
| DEP-06 | P1 | partial | Build, contract digest/generated output, retired-dependency, migration, test, benchmark, and Web workflows exist | Fix database false-pass behavior and two failing tests; make sibling-repository source smoke blocking in hosted CI |
| DEP-07 | P0 | partial | Extensive empty-volume protocol/accounting/media/fault/restart/partition smoke and short realtime/media soaks | One-hour load/lifecycle soak, broader multi-instance shutdown/failure matrix, live adapters, and hosted evidence |

## Completion Interpretation

The project has substantial implementation depth, but it is not close to the
defined full-migration exit condition: only the two clean bootstrap domains are
currently `implemented`. The 55 partial domains are not placeholders; most contain
working state machines and tests, but each still lacks at least one required
production adapter, lifecycle branch, browser/authorization scenario, distributed
failure case, long-running gate, or hosted release proof. OPS-04 remains a skeleton
because backup primitives do not yet constitute a signed update/rollback and
measured disaster-recovery system.
