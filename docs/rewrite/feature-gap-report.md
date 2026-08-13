# ScalaAPI Feature Implementation Gap Report

This report is the concise result of the 2026-08-13 static re-investigation. It
compares the new ScalaAPI product with the useful capability catalogue in fetched,
read-only `sub2api origin/main@fbfdcef`; it does not require API, schema, identifier,
state, deployment, or data compatibility with Sub2API. Per investigation scope,
no build, test, benchmark, service, container, or runtime probe was executed.

The execution-oriented backlog is maintained in
[`implementation-task-list.zh-CN.md`](implementation-task-list.zh-CN.md). It converts
the gaps below into dependency-ordered task cards with implementation scope,
state-machine constraints, commands, and completion evidence for small iterative
agents. This report remains the concise inventory authority; the task list is the
work queue and does not promote a domain by itself.

## Baseline

| Item | Current evidence |
| --- | --- |
| Platform | Documentation baseline `c8a59d7`; production implementation through `651a786`; clean at investigation start; local `master` is 9 commits ahead of `origin/master@d70a993` |
| Gateway | `04ec18c`; clean at investigation start; local `master` is 40 commits ahead of `origin/master@60f99a0` |
| Sub2API requirement baseline | Fetched `origin/main@fbfdcef` (`v0.1.176`) without checkout; local clean worktree remains `43ec48d`, one local commit ahead and 283 commits behind |
| ScalaAPI code surface | 132 production C# files, 72 test/benchmark C# files, 142 direct Admin API route declarations, 62 product tables, 22 SQLSugar entity types, 28 Admin Web TS/TSX files with 16 pages, and 21 User Web TS/TSX files with 14 pages |
| Gateway static surface | 52 production C++ source/header files, 11 test/benchmark sources, 3 tracked Cap'n Proto schemas with revision-3 dispatch, and 127 discoverable GoogleTest declarations |
| Reference scope signal | 661 production Gin route registrations, 42 Ent schemas, 297 Vue files plus 424 TS/TSX files, 59 lazy router imports, and 259 SQL migrations at the fetched upstream ref |
| Inventory result | 65 domains: 2 implemented, 56 partial, 5 skeleton, 2 missing |
| Priority result | P0: 37 total, 2 implemented, 34 partial, 1 skeleton; P1: 24 total, 18 partial, 4 skeleton, 2 missing; P2: 4 partial |
| Risk result | 33 tracked risks: 4 open, 26 partial, and 3 controlled |

The reference counts are breadth signals only. ScalaAPI promotion is based on its
own contract, state machine, automated tests, and current-source runtime evidence,
not a route-count parity percentage.

## Material Reference Delta

The 283 upstream commits after the local reference worktree materially change the
requirements catalogue. The largest additions are a dedicated Grok/xAI account and
protocol stack, Grok 4.6 and subscription-tier quota handling, native Web/X Search,
TTS/STT/custom voices, group model and long-context pricing, response-model-aware
billing, passive Channel Monitor V2, captcha/domain registration controls, and
cluster-singleton scheduled backup behavior. These are now explicit inventory rows
rather than being hidden inside generic Provider, billing, or monitor gaps.

Two boundaries must stay explicit:

1. A generic `grok`/`xai` label and Bearer token in Platform are scaffolding, not a
   Grok implementation. Gateway has no dedicated adapter, native routes, catalogue,
   account quota flow, or source-owned fixture.
2. The local Sub2API-only CDC migration uses number 194, while fetched upstream has
   reused that migration range for usage and Channel Monitor V2. It is not mergeable
   migration history and is not a ScalaAPI dependency; DEP-02 remains intentionally
   clean-room.

The reference repository also contains its own documentation and deployment risks,
including stale Voice/Search and payment documentation, a systemd `PrivateTmp`
socket mismatch, permissive Compose defaults, and checksum-optional installation.
Those findings inform risk review but do not count as ScalaAPI implementation.

## Verification Status

| Evidence | Result |
| --- | --- |
| 2026-08-13 investigation | Static source, route, schema, migration, configuration, documentation, and Git-history inspection only; no runtime gate was run |
| Current source movement | Platform production remains `651a786`; Gateway remains `04ec18c`; only the Sub2API requirement reference advanced |
| Historical 2026-08-11 gates | Release build, ordinary 294/294 Platform run, 127/127 Gateway run, Web builds, contracts, and the empty-volume matrix were previously recorded; none was rerun or revalidated here |
| Historical database gate | Previously red at 292 passed / 2 failed: invalid one-row-per-worker claim expectation and media cleanup FK leakage |
| Promotion interpretation | Historical evidence remains attributable to its exact commits, but current status is not promoted from static inspection |

## Blocking Gap Order

1. Restore a truthful database-enabled CI gate. Fix the two deterministic Host
   tests, isolate shared database state, and stop reporting integration tests as
   passes when prerequisites are absent.
2. Decide and freeze the Grok/xAI product contract, then implement one complete P0
   vertical slice rather than treating a provider label as parity.
3. Extend settlement for observed response model, long-context, per-group model,
   search, speech, and media units before exposing the new routes.
4. Add Web/X Search, speech/custom voice, provider quota-aware scheduling, and
   passive Channel Monitor V2 as explicit bounded state machines.
5. Finish the prior Responses/video/media lifecycle work and replace mock-only
   confidence with live Provider, proxy, TLS, rotation, and long-soak evidence.
6. Close identity, commercial, operations, UI, hosted CI, offsite restore, and
   rolling-release gaps, including captcha/domain abuse controls.

## Gateway And Protocol

| ID | Priority | Status | Implemented now | Gap to `implemented` |
| --- | --- | --- | --- | --- |
| GW-01 | P0 | partial | Gateway has native Anthropic JSON/SSE, count tokens, auth isolation, usage/terminal handling, and cancellation transport; Platform and the historical stack add fault and OAuth revoke/recovery evidence | Live Anthropic credentials, provider pricing/tokenizer fixtures, multi-Silo refresh contention, and long-stream/load evidence |
| GW-02 | P0 | partial | OpenAI Chat JSON/SSE, strict terminal/media checks, timers, normalized errors, durable replay, late usage, and several crash boundaries | Replay after replacement across every remaining crash boundary, live Provider/TLS evidence, and long backpressure soak |
| GW-03 | P0 | partial | Responses root JSON/SSE, compact, get, input-items, cancel, delete, replay, usage, and malformed-success handling | Remaining mutation lifecycle, full provider fault/runtime matrix, cross-protocol fixtures, and live adapter evidence |
| GW-04 | P0 | partial | Gateway has native Gemini JSON/SSE/catalogue, auth isolation, and usage/terminal/cancellation transport; Platform and the historical stack add fault and OAuth revoke/recovery evidence | Live Gemini credentials, provider pricing/tokenizer fixtures, multi-Silo refresh contention, and long-stream/load evidence |
| GW-05 | P0 | partial | Sixteen pairwise request/response/error conversions, same-format passthrough, and representative stream goldens | Tool/multimodal shapes, multiple candidates, identifiers, finish semantics, unknown native fields, malformed/header edges, and runtime E2E |
| GW-06 | P1 | partial | OpenAI/Gemini catalogue response-shape validation, Gemini list/detail, Anthropic count tokens, bounded control operations, and historical no-charge evidence | Requested-model and versioned catalogue authority, empty-cache behavior, provider tokenizer goldens, live runtime errors, and refresh policy |
| GW-07 | P0 | partial | Bounded Embeddings validation, float/base64 results, Jina/Gemini profiles, price-aware settlement, and malformed-response reconciliation | Live adapters and provider-specific production fidelity/runtime evidence |
| GW-08 | P0 | partial | Sync/async/batch image lifecycle, S3 item/archive storage, cancellation, retention, reconciliation, partial-write recovery, partitions, and short two-Silo contention | Fix the database cleanup test, run the full 3600-second contention gate, and prove deployment-scale HA/offsite lifecycle |
| GW-09 | P1 | partial | Video create/edit/extend, durable polling, MP4 storage metadata, signed access, and object verification foundations | Cancel/delete semantics, unit pricing, restart/restore, retention/orphan cleanup, reconciliation, and complete E2E |
| GW-10 | P0 | partial | Realtime WebSocket/calls dispatch, Provider handshake/usage, exact settlement, retry policy, and a four-session three-second soak | Long load/backpressure, replay after process replacement, shutdown behavior, and wider Provider evidence |
| GW-11 | P0 | partial | API-key auth, failover, durable lease/hold/outbox, immutable evidence, late settlement, operator resolution, cross-Gateway idempotency, and many crash hooks | Remaining exact-boundary crashes, TLS and cache outage under load, broader multi-instance recovery, and long soak |
| GW-12 | P0 | partial | Provider-native method/path/status handling, bounded HTTP auth headers, filtered request headers, proxy URL application for HTTP/realtime, and historical Anthropic/Gemini fault evidence | Credentialed proxy decoding/E2E, realtime header validation, actual TLS fingerprint application, live Provider evidence, rotation, and scans |
| GW-13 | P0 | skeleton | Platform recognizes generic `grok`/`xai` labels and generic Bearer credentials | Dedicated Grok/xAI catalogue and transforms, OAuth/account/quota lifecycle, image/video behavior, native routes, fixtures, billing, and runtime evidence |
| GW-14 | P1 | skeleton | `/alpha/search` routing and capability plumbing exist | Web/X Search schemas, provider adapter/mock, source normalization, failure semantics, dedicated usage/pricing, and Admin/User workflows |
| GW-15 | P1 | missing | Generic realtime transport is reusable, but no speech or custom-voice product API exists | TTS, STT, custom voice CRUD/audio, storage/auth, adapters, specialized settlement, cleanup, and operator/user workflows |

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
| AUTH-08 | P1 | missing | Existing PostgreSQL login/registration counters provide adjacent abuse-control infrastructure | Captcha proof lifecycle across auth entry points, provider/CSP configuration, email-domain quotas, Admin controls, audit/metrics, and browser evidence |

## Core Control Plane

| ID | Priority | Status | Implemented now | Gap to `implemented` |
| --- | --- | --- | --- | --- |
| CORE-01 | P0 | partial | Zero-balance user creation and audited, idempotent NUMERIC balance adjustment | Complete role/authorization matrix, bulk administration, and browser workflows |
| CORE-02 | P0 | partial | Scoped/expiring API keys, create/update/rotate/revoke, audit, denied-capability evidence, and no-lease rejection | Multi-instance contention and complete Admin/User browser mutation matrix |
| CORE-03 | P0 | partial | Persisted groups, exact/prefix/wildcard routing, RPM, peak multipliers, fallback chains, and cycle protection | Authenticated CRUD validation, projection/rebuild evidence, and multi-Silo fallback contention |
| CORE-04 | P0 | partial | Encrypted semantic credentials, OAuth refresh CAS, terminal revoke/secret clearing, audit, and explicit replacement recovery | Live Provider profiles, master-key rotation, multi-Silo refresh contention, and operator UI evidence |
| CORE-05 | P0 | partial | Capability/priority/load scheduling, sticky routing, account/user concurrency, RPM, and fallback | Distributed rate-window/lease contention plus HTTP fault/recovery under multiple Silos |
| CORE-06 | P1 | partial | Bounded versioned runtime configuration, secret-key rejection, stale-write conflict, and actor audit | Real dynamic consumers, reload propagation, rollout semantics, and browser controls |
| CORE-07 | P1 | skeleton | Generic capability/load/sticky/concurrency/RPM/fallback scheduling exists | Provider tier and quota snapshots, freshness/unknown policy, free-tier gates, model cooldowns, fenced refresh, recovery, audit, and UI |

## Billing

| ID | Priority | Status | Implemented now | Gap to `implemented` |
| --- | --- | --- | --- | --- |
| BILL-01 | P0 | partial | PostgreSQL lease/hold/usage/outbox authority, immutable evidence, charge-aware abort, late settlement, reconciliation, operator resolution, and many crash replays | Remaining exact crash/ack boundaries, long multi-Silo soak, and full adapter coverage |
| BILL-02 | P0 | partial | Immutable NUMERIC price snapshots, cache/reasoning token fields, admin precedence, and bounded mock Provider catalogue refresh | Live provider pricing rules/catalogues, tokenizer goldens, alias policy, and multi-provider E2E |
| BILL-03 | P0 | partial | API-key absolute/rolling quotas and transactionally reserved/settled subscription grants | Provider-price coupling, every-protocol invalidation, quota reconciliation, multi-Silo contention, and browser proof |
| BILL-04 | P0 | partial | Per-user NUMERIC authority, monotonic ledger, idempotent effects, hold oversubscription protection, drift incidents, repair, and concurrent operator decisions | Long multi-Silo crash/settlement soak and complete commercial/media effect coverage |
| BILL-05 | P1 | partial | Usage aggregates, scoped queries, exports, retention/cleanup foundations, and dashboard surfaces | Complete aggregation correctness, scheduled cleanup, immutable retention, browser export, and load evidence |
| BILL-06 | P0 | partial | Immutable NUMERIC pricing, service tier, realtime/video rates, and requested/upstream model fields provide foundations | Terminal response-model observation and conservative selection, long-context/group model prices, search/audio/video units, mismatch audit, and no-price-escalation/bypass invariants |

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
| OPS-04 | P1 | skeleton | Local PostgreSQL backup jobs, checksums, isolated restore command, and basic Admin controls exist | Signed/encrypted offsite retention, cluster-singleton scheduled jobs, measured RPO/RTO, restore faults, service restart/update, rolling rollout, and rollback drills |
| OPS-05 | P2 | partial | Bounded user export and audited, idempotent, dry-run maintenance cleanup | Scheduling, immutable retention, object/media cleanup, browser download authorization, and maintenance metrics |
| OPS-06 | P1 | skeleton | Manual channel checks and general operational metrics exist | Isolated V1/V2 mode, passive rollups/watermarks/backfill, matrices and latency histograms, privacy defaults, retention/config APIs, leader fencing, and Admin/User views |

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
currently `implemented`. The 56 partial domains are not placeholders; most contain
working state machines and historical evidence, but each still lacks at least one
required production adapter, lifecycle branch, browser/authorization scenario,
distributed failure case, long-running gate, or hosted release proof. Five domains
remain skeletons and speech plus captcha/domain controls are missing. Static source
inspection alone did not promote any domain.
