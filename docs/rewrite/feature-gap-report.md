# ScalaAPI Feature Implementation Gap Report

Audit date: 2026-08-14. Baseline: Platform `bc083d1`, Gateway `b6e4e02`, and
read-only Sub2API `origin/main@fbfdcef`.

This report measures implementation maturity for the greenfield ScalaAPI product.
It is not an API parity report and never requires compatibility with Sub2API paths,
schemas, migrations, identifiers, state, data, keys, Redis layout or deployment.
Sub2API is non-normative research used only to discover possible capability families;
its commit delta does not automatically add scope or change any status.

## Status model

- `verified`: the defined slice has its required source/state and current focused
  acceptance evidence. Database process Provider or browser evidence is mandatory
  whenever that slice crosses the corresponding boundary.
- `partial`: meaningful source and tests exist, but a required branch, integration
  gate, Provider boundary or operational proof is absent or contradicted.
- `scaffold`: routes, DTOs, stores, mocks or worker shells exist, but the production
  state machine/adaptor is not complete.
- `missing`: no material implementation exists.
- `blocked`: the slice may be implemented, but a shared release blocker currently
  prevents its required acceptance evidence from running.

Statuses describe this audit, not permanent priorities. A green unit suite does not
promote a database, cross-repository, browser or production-adapter domain.

## Baseline and result

| Item | Current evidence |
| --- | --- |
| Platform | Clean `master == origin/master@bc083d1` before docs edits; 174 tracked source C# files, 89 test C# files, 466 xUnit declarations, 65 product migrations, 189 direct Admin API mappings |
| Gateway | Clean `master == origin/master@b6e4e02`; 52 production C++ source/header files, 12 test/benchmark sources, 159 current CTest cases |
| Sub2API research snapshot | `origin/main@fbfdcef`, `v0.1.176-5-gfbfdcef81`; local worktree `43ec48d` is 1 ahead / 283 behind; pinned ref has 668 non-test Gin registrations, 42 Ent schema-directory files (39 entities, 2 mixins, 1 test), 297 Vue, 426 TS and 259 SQL migrations; none automatically defines product scope |
| Inventory | 65 unique domains: 1 `verified`, 52 `partial`, 7 `scaffold`, 5 `blocked`, 0 `missing` |
| Prior conclusion | The 2026-08-13 `65 implemented` claim is withdrawn because the current migration contract benchmark and evidence gates fail |

The counts above are scope signals, never compatibility percentages.

## Current blockers

1. **DEP-01**: empty PostgreSQL 17 migration fails before migration 055 because
   later SQL refers to `users` / `api_keys`, not the product tables
   `user_accounts` / `user_api_keys`.
2. **DEP-06**: Platform and Gateway `dispatch.capnp` differ at audio endpoint
   enum values. Gateway's local digest passes; Platform's stale canonical digest and
   the authoritative cross-repository comparison fail.
3. **Truthful tests**: the no-database Platform run reports 502/502 while 123 test
   methods directly return without `GREENFIELD_SCHEMA_CONNECTION`.
4. **Benchmark gate**: all four Scheduler benchmark cases fail before measurement
   because the benchmark Silo cannot resolve `ISlotLeaseStore`.
5. **Operational scaffolds**: active monitoring, quota refresh, scheduled backup
   and offsite upload contain explicit simulated/no-I/O code paths.
6. **DEP-07**: the stress harness queries tables outside the implemented ownership
   model and does not make every dead child or settlement timeout fatal.
7. **Release ordering**: Platform publishes four image families including `latest`
   before its later clean rebuild and never checks out a paired Gateway.
8. **Bypass and fabricated evidence**: greenfield CI omits the Gateway argument;
   `docker.yml` publishes on tags without gates; `deploy/release.sh` checks only the
   Gateway-local digest and hard-codes pass claims for commands it never runs.
9. **Realtime policy bypass**: the WebSocket dispatch omits the initial request body
   and then relays both directions raw, so Platform evaluates empty request content
   and Gateway never invokes response policy for later frames.
10. **Request-size contract mismatch**: Gateway accepts 32 MiB and serializes the
    whole body into Cap'n Proto, while Platform closes frames above 1 MiB. Large
    multipart/media requests can fail before a durable dispatch decision.
11. **Evidence durability**: `output_started` is sent only after the first client
    write and a failed RPC is log-only; unacknowledged non-retryable usage events are
    deleted rather than retained or converted into reconciliation incidents.
12. **Gateway runtime/release boundary**: startup can remain alive after dependency
    or listener failure, readiness checks only dispatch UDS, and Gateway's independent
    tag workflow publishes without a paired Platform or full-stack gate.

## Gateway and protocol

| ID | Pri | Status | Current implementation | Required closure |
| --- | --- | --- | --- | --- |
| GW-01 | P0 | partial | Anthropic Messages JSON/SSE, native headers, count tokens, usage/terminal validation and cancellation tests | Current Platform/database settlement and controlled Provider/runtime evidence |
| GW-02 | P0 | partial | OpenAI Chat JSON/SSE, strict stream media/terminal handling, bounded timers, errors, replay and late usage | Current full-stack financial evidence after DEP-01 |
| GW-03 | P0 | partial | Responses root/compact/read/input-items/cancel/delete routing, mock contracts and validation | Current empty-stack mutation/fault matrix after DEP-01; remaining lifecycle semantics |
| GW-04 | P0 | partial | Gemini generate/catalogue/count paths, native key isolation, JSON/SSE usage and terminal checks | Current Platform/database settlement and controlled Provider/runtime evidence |
| GW-05 | P0 | partial | Pairwise text/error conversions, finish reasons and tool-call response conversion | Multimodal/candidates/identifiers/tool results plus an explicit safe upstream-error exposure/rewrite/monitoring policy |
| GW-06 | P1 | partial | OpenAI/Gemini catalogue shape and Anthropic token-count validation | Authoritative provider catalogue refresh and tokenizer versions; an unauthenticated cache miss must not imply an empty authoritative catalogue |
| GW-07 | P0 | partial | Bounded embeddings inputs/responses, float/base64, Jina/Gemini profiles and mock pricing | Live/provider-specific adapters and tokenizer fixtures |
| GW-08 | P0 | partial | Sync/async/batch images, durable media/item stores, S3 signing, repair and retention code | Align HTTP/RPC media-size contract; current empty-stack/partition/long contention evidence after schema repair |
| GW-09 | P1 | partial | Video create/edit/extend/control routes, polling and object metadata | Complete cancellation/delete/restore/provider matrix and specialized settlement E2E |
| GW-10 | P0 | partial | A ScalaAPI Realtime/Live route subset and model parsing are unit-tested; dispatch omits body, raw-relays frames and bypasses ordinary query/trusted-proxy handling | Name each supported WebSocket/sideband protocol, then apply bounded policy/identity/attestation rules and runtime evidence |
| GW-11 | P0 | partial | Lease/hold/idempotency, failover and SQLite usage outbox exist; `forwarded` is fail-closed | Durably queue `output_started`; retain/incident every unacknowledged usage event; run current exactly-once crash/recovery matrix |
| GW-12 | P0 | partial | Bounded Provider auth-header validation and authenticated proxy URL exist | Reject unknown method/path/general headers, apply TLS fingerprint, and prove provider-specific proxy/TLS/rotation behavior |
| GW-13 | P0 | partial | xAI/Grok identity and OpenAI-compatible text scaffolding exist, but `AuthenticateXai` is defined only in Provider.Mock and never wired to a route | Native auth account OAuth quota media/search/voice behavior and Provider-owned fault/billing matrix; generic Bearer is not full xAI support |
| GW-14 | P1 | blocked | Search capability, mock/history/status and price-unit source exist; declared streaming is not selected by the handler | Fix DEP-01, implement real Web/X adapters, make streaming reachable or unadvertised, then prove authorization and settlement E2E |
| GW-15 | P1 | blocked | TTS/STT routes, request validation, mock, voice/audio stores and price units exist | Fix DEP-01 plus Cap'n Proto drift; prove multipart/audio bytes, ownership/storage and character/time settlement E2E |

## Identity

| ID | Pri | Status | Current implementation | Required closure |
| --- | --- | --- | --- | --- |
| AUTH-01 | P0 | partial | Registration/login, hashing, counters and lockout code/tests | Database-enabled concurrent/anti-enumeration and browser evidence |
| AUTH-02 | P0 | partial | Rotating hashed refresh sessions, logout/revocation and session listing | Current multi-process/database and browser replay/concurrency evidence |
| AUTH-03 | P1 | partial | PostgreSQL TOTP replay/lockout/backup-code service and UI | Database-enabled backup-code sign-in and browser recovery workflow |
| AUTH-04 | P1 | partial | Fido2/WebAuthn challenges, credentials, counters, audit and UI conversion | Real browser authenticator plus distributed abuse evidence |
| AUTH-05 | P1 | partial | Password/email token state and encrypted leased notification outbox | Live SMTP TLS/auth, receipt/expiry, metrics and browser links |
| AUTH-06 | P1 | partial | OAuth PKCE/state/exchange against source mock | Production redirect/account-link collision policy and browser callback evidence |
| AUTH-07 | P1 | partial | Profile/password/session revocation and soft deletion | Key/session concurrency, retention/erasure and browser mutation evidence |
| AUTH-08 | P1 | partial | Captcha interface/mock and email-domain quota store/tests | Real provider profiles and all public-entry-point enforcement, audit/metrics and browser failure UX |

## Core control plane

| ID | Pri | Status | Current implementation | Required closure |
| --- | --- | --- | --- | --- |
| CORE-01 | P0 | partial | User/admin state and NUMERIC adjustment path | Database-enabled role/idempotency/browser matrix |
| CORE-02 | P0 | partial | Scoped/expiring API keys, rotation/revoke/audit and pre-dispatch rejection | Multi-instance database contention and complete Admin/User workflows |
| CORE-03 | P0 | partial | Exact/prefix/wildcard routing, RPM, peak multipliers and fallback/cycle logic | Authenticated CRUD/projection rebuild and multi-Silo contention |
| CORE-04 | P0 | partial | Encrypted semantic credentials, refresh CAS, terminal revoke/replacement and audits | Live Provider profiles, key rotation and multi-Silo refresh contention |
| CORE-05 | P0 | partial | Scheduler, persistent slot leases, account health, sticky/rate/fallback code | Current database and two-Silo load/failure evidence |
| CORE-06 | P1 | partial | Versioned config store, propagation/rollback APIs and node status | Prove all dynamic consumers converge and reject rollback under real multi-process faults |
| CORE-07 | P1 | scaffold | Quota schema/store/CAS and scheduler inputs exist | Refresh real account inventory through bounded Provider adapters; current worker only bumps seeded rows |

## Usage and billing

| ID | Pri | Status | Current implementation | Required closure |
| --- | --- | --- | --- | --- |
| BILL-01 | P0 | partial | PostgreSQL lease/hold/usage/ledger/outbox and reconciliation code | Greenfield DB plus current crash/retry/late-settlement matrix |
| BILL-02 | P0 | partial | Decimal immutable quotes, cache/reasoning tokens and provider-source metadata | Provider-specific catalogue/tokenizer authority and integrated price selection |
| BILL-03 | P0 | partial | API-key/subscription quota reservation/event/lifecycle stores | Database-enabled concurrent reserve/commit/release and payment renewal E2E |
| BILL-04 | P0 | partial | NUMERIC authority, monotonic ledger and idempotent effects | Long multi-Silo financial invariant gate on current schema |
| BILL-05 | P1 | partial | Usage query/dashboard/export/cleanup source | Database aggregation/retention correctness and authorized browser download |
| BILL-06 | P0 | partial | Observed-model/search/audio/character/long-context price fields and tests | Propagate terminal observed model for media (source still has TODO) and prove all unit types E2E |

## Commercial

| ID | Pri | Status | Current implementation | Required closure |
| --- | --- | --- | --- | --- |
| COM-01 | P0 | partial | Mock/Stripe-shaped checkout, signatures, refunds and ledger state machines | Current DB/full HTTP crash matrix, secret rotation and real checkout completion |
| COM-02 | P1 | partial | Plan/purchase/cancel/renew/expiry stores and worker | Payment-authoritative renewal and browser/database failure recovery |
| COM-03 | P1 | partial | Transactional redeem and promotion source | Concurrent database/HTTP limits, audit and browser evidence |
| COM-04 | P2 | partial | Referral code/attribution/reward stores and UI | Signup attribution, anti-abuse, transfer/rebate lifecycle and browser evidence |
| COM-05 | P2 | partial | Announcement lifecycle, targeting/read stores and views | Current database targeting/scheduling/authorization/browser proof |

## Administration and operations

| ID | Pri | Status | Current implementation | Required closure |
| --- | --- | --- | --- | --- |
| OPS-01 | P0 | partial | Broad Admin API and 16-page application source | Source-built authenticated CRUD/authorization/error E2E; build alone is not workflow proof |
| OPS-02 | P0 | partial | Metric collector/store, summaries, alerts and Operations page | Automatic cross-service correlation, delivery/recovery and multi-process budgets |
| OPS-03 | P1 | scaffold | Monitor templates/claims/retries/incidents/UI source | Distributed leader fencing and real provider probe; current worker elects every process and simulates checks |
| OPS-04 | P1 | scaffold | Local backup/restore, checksum/crypto/key/policy APIs and UI | Scheduler must create backups; offsite must transfer/verify bytes; measured restore and rollback drills |
| OPS-05 | P2 | partial | Export jobs/tokens and cleanup/retention stores | Scheduled worker, object lifecycle, database/browser authorization and immutable audit retention |
| OPS-06 | P1 | scaffold | Passive V2 rollup/watermark/privacy stores and APIs | Replace process-local leader placeholder with PostgreSQL fencing; run dedup/backfill/multi-process E2E |

## Security

| ID | Pri | Status | Current implementation | Required closure |
| --- | --- | --- | --- | --- |
| SEC-01 | P0 | partial | HTTP policy hooks cover only chat-classified text capabilities; realtime and Search/Antigravity/audio/media/embeddings paths lack equivalent response evaluation | Define an explicit per-capability request/response/binary matrix, then prove outage/order/long-stream/WebSocket behavior |
| SEC-02 | P1 | partial | Actor audit and recursive redaction/export source | Immutable retention, complete authorization matrix and security scan gate |
| SEC-03 | P0 | partial | Encrypted proxy credentials and TLS profile CRUD | Apply TLS fingerprint in outbound transport and prove rotation/expiry/proxy isolation |
| SEC-04 | P0 | partial | AES-GCM secret storage, JWT checks, redaction and master-key operations | Production key custody/rotation, step-up enforcement and secret scanning |
| SEC-05 | P1 | partial | Login/registration counters, captcha/domain policy components | All public endpoints, distributed limits and anti-enumeration runtime evidence |

## Frontend

| ID | Pri | Status | Current implementation | Required closure |
| --- | --- | --- | --- | --- |
| UI-01 | P0 | partial | Admin Web typechecks/builds with core pages and mutations | Blocking authenticated backend-backed browser matrix |
| UI-02 | P0 | partial | User Web typechecks/builds with dashboard/usage/keys/profile/billing | Cross-user authorization, refresh/replay and real mutation browser matrix |
| UI-03 | P1 | partial | Auth/recovery/OAuth/TOTP/Passkey page source | Real mail/authenticator/callback and expiry/failure flows |
| UI-04 | P1 | partial | Billing/subscription/redeem/referral page source | Provider checkout and full commercial browser lifecycle |
| UI-05 | P1 | partial | Reconciliation/policy/operations/monitor/backup pages | Live authorized mutations against persistent backend state |
| UI-06 | P2 | partial | Public models/status/terms/privacy source plus current typecheck/build | Run anonymous backend-proxy authorization and accessibility browser checks |

## Deployment and reliability

| ID | Pri | Status | Current implementation | Required closure |
| --- | --- | --- | --- | --- |
| DEP-01 | P0 | blocked | Forward-only migrator and 65 product SQL files exist | Empty PostgreSQL 17 double-run currently fails at missing `users`; repair names/order and prove all 66 records including Orleans |
| DEP-02 | P0 | verified | Static scan finds no Sub2API/Redis/CDC/Debezium runtime or data dependency | Preserve this narrow invariant; it does not imply that the currently broken empty-stack bootstrap works |
| DEP-03 | P0 | scaffold | Compose/probe/metrics source exists, but fresh schema is broken and Gateway readiness is incomplete | Repair schema/startup/readiness; define product-native dependency/first-admin setup; retain empty-volume source-built results |
| DEP-04 | P0 | scaffold | Two-Silo/two-Gateway Compose and rolling/fault scripts exist | Current outage/rejoin/drain/partition execution and exact financial/object assertions |
| DEP-05 | P0 | scaffold | TLS, local backup/restore, crypto and policy code exists | Real offsite bytes, scheduled backups, restore/RPO/RTO and ingress/rollback drills |
| DEP-06 | P1 | blocked | Platform/Gateway have independent publishers; Admin update falsely reports a metadata check as a download | Gate paired publication, remove/replace the self-update scaffold, fix contract/benchmark/no-DB gates, and derive reports from executed evidence |
| DEP-07 | P0 | blocked | Large smoke/stress/load/fault script surface exists | Empty stack blocked; stress SQL also references nonexistent tables (`gateway_usage_outbox`, `reconciliation_incidents`) and must be repaired before a real 3600-second run |

## Research capability decisions

The pinned Sub2API tree was used to check whether the 65 selected domains hide a
material family. These signals are mappings or explicit decisions, not compatibility
requirements and not extra verified rows:

| Research signal | ScalaAPI treatment at this audit |
| --- | --- |
| First-run dependency tests, setup status/install and setup UI | Mapped to DEP-03. Select a product-native first-admin/bootstrap workflow before DEP-03 can be verified |
| Configurable upstream error pass-through/rewrite and monitor suppression | Mapped to GW-05 and SEC-01. Define a safe native contract or explicitly reject the feature; current generic error normalization is not equivalent |
| Responses WebSocket, OpenAI Live/sideband attestation and xAI Realtime | GW-10 covers only explicitly named ScalaAPI routes. No broad “Realtime parity” is claimed |
| Binary download, atomic replacement and rollback | In-process self-update is excluded by the paired immutable release design. Remove the current false-success Admin endpoint or connect it to a real external controller |
| Custom user attributes, client-version/fingerprint policy, training opt-out and admin compliance views | Candidate details inside CORE-01/SEC-03/SEC-05/OPS-01; they require individual product decisions and evidence before advertising |

No major billing, commercial, Provider-account, monitoring or UI family was otherwise
absent. Static breadth in the research tree does not prove its runtime correctness.

## Deliberate exclusions

The following remain out of scope even when a similar Sub2API feature exists:

1. Sub2API private/Admin API compatibility, error-body fidelity and UI layout.
2. Sub2API PostgreSQL/Ent schema, migration numbers, data import or CDC history.
3. Sub2API Redis keys/cache behavior, credentials, hashes, IDs or state values.
4. Upgrade, rollback or dual-run paths from a Sub2API deployment.
5. Compatibility shims, deprecated aliases and dual-read/write branches for this
   unreleased greenfield internal contract.
6. Service-managed binary replacement; rollout and rollback belong to the paired
   immutable deployment controller.

## Promotion rule

A domain is promoted only from current evidence at immutable refs. For a database
domain that includes the empty-schema migration and database-enabled tests; for a
cross-repository domain it includes canonical/vendor equality; for a browser or
operations domain it includes source-built runtime behavior. Historical smoke names,
route/table presence, mock-only success and tests that silently return are supporting
context, not promotion evidence.
