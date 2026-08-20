# ScalaAPI Feature Implementation Gap Report

Audit date: 2026-08-14. Code baseline: Platform `30d82d0`, Gateway `98c62fd`,
ScalaAPI superproject `032721b`, and read-only Sub2API research snapshot
`origin/main@fbfdcef`.

ScalaAPI is a greenfield Orleans + C++ product. This report does not measure API
parity and does not require compatibility with Sub2API routes, errors, schemas,
migrations, identifiers, state, data, keys, Redis layout or deployment. Sub2API is
non-normative research used only to discover capability families; a reference
commit or feature never becomes ScalaAPI scope without a product decision.

## Status model

- `verified`: the defined slice has current source/state plus the focused evidence
  required at every boundary it crosses.
- `partial`: meaningful production source and tests exist, but Provider, browser,
  multi-process, fault or operational acceptance evidence remains incomplete.
- `scaffold`: topology, routes, DTOs, stores, mocks or scripts exist, but the
  production state machine or execution proof is still too thin to call partial.
- `missing`: no material implementation exists.
- `blocked`: implementation may exist, but a shared blocker prevents even focused
  acceptance evidence from running.

A green build or unit suite alone never promotes a database, cross-repository,
browser, Provider or production-operation domain.

## Baseline and result

| Item | Current evidence |
| --- | --- |
| Platform | `origin/master@30d82d0`; 181 tracked production C# files, 90 C# test files, 466 xUnit declarations, 65 product migrations and 189 direct Admin API mappings |
| Platform verification | Release restore/build pass with 0 warnings/errors; isolated PostgreSQL 17 applies all 66 records (Orleans 000 + 65 product migrations), skips all 66 on rerun, and the DB-enabled solution passes 502/502 tests |
| Negative test control | Running Host tests without `GREENFIELD_SCHEMA_CONNECTION` fails visibly: 113 failed, 145 passed, total 258; database absence is no longer a silent green |
| Platform benchmarks and contracts | Six Dry benchmarks pass (two Dispatch and four Scheduler); all three contract checksums, Platform/Gateway byte equality, generated C# comparison and retired-dependency scan pass |
| Web applications | Admin Web and User Web both pass `npm ci`, typecheck and production build; both dependency trees still report one high-severity `nanoid <3.3.18` advisory |
| Gateway | Standalone head `98c62fd`; 52 production C++ files and 12 test/benchmark files. The shared worktree has user WIP in `gateway_handler.cpp` and `test_protocol.cpp` and currently fails to compile because `LeaseAbortDisposition::Safe` is not declared; that WIP is not release evidence |
| Supported pair | ScalaAPI `032721b` pins Platform `e73a5d8` and Gateway `777278e`; `validate-pair.sh` and pair-manifest generation pass. The newer standalone heads are not a supported pair until both gitlinks and full evidence are advanced together |
| Sub2API research snapshot | `origin/main@fbfdcef`; local `43ec48d` is 1 ahead / 283 behind. The upstream tree is far broader, but none of its routes, entities, migrations or UI files automatically defines product scope |
| Inventory | 65 unique domains: 2 `verified`, 62 `partial`, 1 `scaffold`, 0 `blocked`, 0 `missing` |

The withdrawn 2026-08-13 claim that all 65 domains were complete remains withdrawn.
The repaired foundational gates are real progress, not proof that every product
workflow is production-complete.

## Current release blockers

1. The latest standalone Platform and Gateway heads are not yet selected and proven
   as one immutable ScalaAPI pair.
2. The shared Gateway worktree is dirty and its WIP does not compile because it uses
   the nonexistent `LeaseAbortDisposition::Safe` enum value.
3. Provider protocols, quota/catalogue refresh, financial crash recovery, monitoring
   and object operations still need current source-built multi-process/runtime proof.
4. Gateway accepts 32 MiB HTTP bodies while Platform framed RPC rejects above 1 MiB;
   media/multipart traffic lacks one pre-lease size contract.
5. Realtime now includes the first body and applies initial capability policy, but
   later WebSocket frames still lack an accepted bounded policy/settlement matrix.
6. Backup scheduling creates a claimed job and offsite upload performs HTTP PUT, but
   scheduled job-to-artifact execution, remote readback and restore drills are not
   proven end to end.
7. Both web applications build, but authenticated backend-backed browser workflows
   are not a required gate and both lockfiles carry the high-severity `nanoid` advisory.
8. The corrected stress verifier still needs a current short fault run and the real
   3600-second mixed load/fault run with durable invariants and cleanup evidence.
9. `/admin/system/update` still reports a download that it does not perform; immutable
   paired deployment should remove it or delegate to a real external controller.
10. The release evidence generator does not parse uploaded test artifacts; its fixed
    `status: passed` and `skipped: []` fields cannot substantiate test totals.

## Gateway and protocol

| ID | Pri | Status | Current implementation | Required closure |
| --- | --- | --- | --- | --- |
| GW-01 | P0 | partial | Anthropic Messages JSON/SSE, native headers, count tokens, usage/terminal validation and cancellation tests | Current Platform settlement plus controlled Provider/runtime fault evidence |
| GW-02 | P0 | partial | OpenAI Chat JSON/SSE, strict stream media/terminal handling, bounded timers, errors, replay and late usage | Full-stack financial crash/retry/disconnect matrix on the selected pair |
| GW-03 | P0 | partial | Responses root/compact/read/input-items/cancel/delete routing, mock contracts and validation | Complete lifecycle semantics and current database-backed mutation/fault matrix |
| GW-04 | P0 | partial | Gemini generate/catalogue/count paths, native key isolation, JSON/SSE usage and terminal checks | Current Platform settlement plus controlled Provider/runtime fault evidence |
| GW-05 | P0 | partial | Pairwise conversions now reject unsupported multimodal/multi-candidate cases, preserve response IDs, detect tool-result images, fix Anthropic SSE events and produce unique Gemini tool IDs | Finish supported tool-result/media matrix and define bounded upstream-error exposure/rewrite/monitoring policy |
| GW-06 | P1 | partial | Catalogue/token-count shapes exist; Platform aggregates active-account models and anonymous models fail 503 rather than empty 200 when Garnet is unavailable | Provider-authoritative refresh, stale snapshot rules and versioned tokenizer evidence |
| GW-07 | P0 | partial | Bounded embeddings inputs/responses, float/base64, Jina/Gemini profiles and mock pricing | Production Provider adapters and tokenizer/settlement fixtures |
| GW-08 | P0 | partial | Sync/async/batch images, durable media/item stores, S3 signing, repair and retention source | Align HTTP/RPC size limits and run partition/partial-write/two-Silo lifecycle evidence |
| GW-09 | P1 | partial | Video create/edit/extend/control routes, polling and object metadata | Complete cancellation/delete/restore/provider fault and specialized settlement E2E |
| GW-10 | P0 | partial | Clean head includes the initial WebSocket body, trusted-proxy/query validation and explicit initial policy selection; later frames remain raw and the shared WIP does not compile | Bound and evaluate later text frames, decide binary/audio policy, and run reconnect/multi-instance settlement E2E |
| GW-11 | P0 | partial | Lease/hold/idempotency/failover and SQLite usage outbox exist; durable `output_started` evidence and retention of unacknowledged non-retryable events are implemented | Source-built exactly-once crash/recovery matrix for HTTP and realtime |
| GW-12 | P0 | partial | Bounded auth headers and proxy URL exist; unknown methods return 405 and TLS profiles are explicitly rejected instead of silently ignored | Bound paths/general headers, implement a real TLS profile or keep it unsupported, and prove proxy/rotation isolation |
| GW-13 | P0 | partial | xAI identity/text fixtures, credential state and a real quota client exist | Explicit native auth/OAuth/catalogue/search/media/voice matrix and Provider-owned fault/billing evidence |
| GW-14 | P1 | partial | Fresh-schema search state, routes, mock/history/status and price units exist; declared streaming is still unreachable | Real Web/X adapters, reachable bounded stream or removed advertisement, authorization/privacy/settlement E2E |
| GW-15 | P1 | partial | Fresh-schema audio/voice state, synchronized contract, routes, validation, mock and price units exist | Multipart/audio bytes, ownership/storage, cancellation and character/time settlement E2E |

## Identity

| ID | Pri | Status | Current implementation | Required closure |
| --- | --- | --- | --- | --- |
| AUTH-01 | P0 | partial | Registration/login, hashing, counters and lockout code/tests | Concurrent database anti-enumeration and browser evidence |
| AUTH-02 | P0 | partial | Rotating hashed refresh sessions, logout/revocation and session listing | Multi-process replay/concurrency and browser evidence |
| AUTH-03 | P1 | partial | PostgreSQL TOTP replay/lockout/backup-code service and UI | Backup-code sign-in plus real browser recovery workflow |
| AUTH-04 | P1 | partial | Fido2/WebAuthn challenges, credentials, counters, audit and UI conversion | Real authenticator ceremony and distributed abuse evidence |
| AUTH-05 | P1 | partial | Password/email token state and encrypted leased notification outbox | Live SMTP TLS/auth, receipt/expiry, metrics and browser-link evidence |
| AUTH-06 | P1 | partial | OAuth PKCE/state/exchange against source mock | Production redirect/account-link collision policy and browser callbacks |
| AUTH-07 | P1 | partial | Profile/password/session revocation and soft deletion | Key/session concurrency, retention/erasure and browser mutations |
| AUTH-08 | P1 | partial | Captcha interface/mock and email-domain quota store/tests | Real provider profiles, all-public-entry enforcement, audit/metrics and failure UX |

## Core control plane

| ID | Pri | Status | Current implementation | Required closure |
| --- | --- | --- | --- | --- |
| CORE-01 | P0 | partial | User/admin state and NUMERIC adjustment path | Database role/idempotency and browser matrix |
| CORE-02 | P0 | partial | Scoped/expiring API keys, rotation/revoke/audit and pre-dispatch rejection | Multi-instance contention and complete Admin/User workflows |
| CORE-03 | P0 | partial | Exact/prefix/wildcard routing, RPM, peak multipliers and fallback/cycle logic | Authenticated CRUD/projection rebuild and multi-Silo contention |
| CORE-04 | P0 | partial | Encrypted semantic credentials, refresh CAS, terminal revoke/replacement and audits | Live Provider profiles, key rotation and multi-Silo refresh contention |
| CORE-05 | P0 | partial | Scheduler, persistent slot leases, health, sticky/rate/fallback logic; Scheduler Dry benchmarks execute | Database-backed two-Silo load/failure and regression thresholds |
| CORE-06 | P1 | partial | Versioned config store, propagation/rollback APIs and node status | Prove all dynamic consumers converge and reject rollback under process faults |
| CORE-07 | P1 | partial | Active-account refresh now queries accounts table, stale tracking with consecutive_failures/state, contract tests for all 4 quota clients | Run fenced Provider fault evidence and multi-process refresh contention |

## Usage and billing

| ID | Pri | Status | Current implementation | Required closure |
| --- | --- | --- | --- | --- |
| BILL-01 | P0 | partial | PostgreSQL lease/hold/usage/ledger/outbox/reconciliation state passes fresh-schema tests | Current crash/retry/late-settlement and two-Silo invariant matrix |
| BILL-02 | P0 | partial | Decimal immutable quotes, cache/reasoning tokens and Provider-source metadata | Provider-authoritative catalogue/tokenizer inputs and integrated selection |
| BILL-03 | P0 | partial | API-key/subscription quota reservation/event/lifecycle stores | Concurrent reserve/commit/release and payment renewal E2E |
| BILL-04 | P0 | partial | NUMERIC authority, monotonic ledger and idempotent effects | Long multi-Silo financial invariant gate |
| BILL-05 | P1 | partial | Usage query/dashboard/export/cleanup source | Aggregation/retention correctness and authorized browser download |
| BILL-06 | P0 | partial | Observed model now flows from media Provider responses; search/audio/character/long-context fields and tests exist | Prove all specialized units and model mismatch outcomes end to end |

## Commercial

| ID | Pri | Status | Current implementation | Required closure |
| --- | --- | --- | --- | --- |
| COM-01 | P0 | partial | Mock/Stripe-shaped checkout, signatures, refunds and ledger state machines | Full HTTP/database crash/replay, secret rotation and real checkout completion |
| COM-02 | P1 | partial | Plan/purchase/cancel/renew/expiry stores and worker | Payment-authoritative renewal and browser/database recovery |
| COM-03 | P1 | partial | Transactional redeem and promotion source | Concurrent limits, audit, HTTP and browser evidence |
| COM-04 | P2 | partial | Referral code/attribution/reward stores and UI | Signup attribution, anti-abuse, transfer/rebate lifecycle and browser proof |
| COM-05 | P2 | partial | Announcement lifecycle, targeting/read stores and views | Database scheduling/authorization and browser proof |

## Administration and operations

| ID | Pri | Status | Current implementation | Required closure |
| --- | --- | --- | --- | --- |
| OPS-01 | P0 | partial | Broad Admin API and 16-page source application; typecheck/build pass | Authenticated backend-backed CRUD/authorization/error browser E2E |
| OPS-02 | P0 | partial | Metric collector/store, summaries, alerts and Operations page | Automatic cross-service correlation, delivery/recovery and multi-process budgets |
| OPS-03 | P1 | partial | Monitor templates/claims/retries/incidents/UI plus PostgreSQL advisory-lock leadership and bounded HTTP probes | Two-process fencing, Provider-specific checks, outage/incident/recovery runtime evidence |
| OPS-04 | P1 | partial | Backup/restore/checksum/crypto/policy APIs, scheduled jobs now execute inline pg_dump, offsite upload wired with remote readback SHA-256 verification, zombie row reclaim | End-to-end restore drill, measured RPO/RTO and isolated restore evidence |
| OPS-05 | P2 | partial | Export jobs/tokens and cleanup/retention stores | Scheduled lifecycle, object cleanup, authorization and immutable audit proof |
| OPS-06 | P1 | partial | Passive rollup/watermark/privacy stores, percentile fix and PostgreSQL advisory-lock leadership | Duplicate/out-of-order/backfill/restart/privacy evidence across processes |

## Security

| ID | Pri | Status | Current implementation | Required closure |
| --- | --- | --- | --- | --- |
| SEC-01 | P0 | partial | Initial HTTP/realtime capability policy selection is explicit; later realtime frames and several binary/media capabilities lack an accepted response matrix | Per-capability request/response/binary policy plus outage/order/long-stream/WebSocket evidence |
| SEC-02 | P1 | partial | Actor audit and recursive redaction/export source | Immutable retention, complete authorization and security scan gate |
| SEC-03 | P0 | partial | Encrypted proxy credentials and TLS profile CRUD; unsupported profiles are rejected | Implement and prove TLS transport profiles or retain explicit unsupported behavior; prove rotation/expiry/isolation |
| SEC-04 | P0 | partial | AES-GCM secret storage, JWT checks, redaction and master-key operations | Production custody/rotation, step-up enforcement and secret scanning |
| SEC-05 | P1 | partial | Login/registration counters, captcha and domain-policy components | All-public-endpoint distributed limits and anti-enumeration runtime evidence |

## Frontend

| ID | Pri | Status | Current implementation | Required closure |
| --- | --- | --- | --- | --- |
| UI-01 | P0 | partial | Admin Web passes clean install, typecheck and production build | Blocking authenticated backend-backed workflow matrix and nanoid advisory resolved |
| UI-02 | P0 | partial | User Web passes clean install, typecheck and production build | Cross-user authorization, refresh/replay, real mutations and nanoid advisory resolved |
| UI-03 | P1 | partial | Auth/recovery/OAuth/TOTP/Passkey page source | Real mail/authenticator/callback/expiry/failure flows |
| UI-04 | P1 | partial | Billing/subscription/redeem/referral page source | Provider checkout and complete browser lifecycle |
| UI-05 | P1 | partial | Reconciliation/policy/operations/monitor/backup pages | Authorized mutations against persistent backend state |
| UI-06 | P2 | partial | Public models/status/terms/privacy source builds | Anonymous backend-proxy authorization and accessibility browser checks |

## Deployment and reliability

| ID | Pri | Status | Current implementation | Required closure |
| --- | --- | --- | --- | --- |
| DEP-01 | P0 | verified | Isolated PostgreSQL 17 applies all 66 migration records, skips all 66 on rerun and the DB-enabled solution passes 502/502 | Preserve exact migration manifest, double-run and schema tests in the paired gate |
| DEP-02 | P0 | verified | Retired-dependency scan finds no Sub2API/Redis/CDC/Debezium runtime or data dependency | Preserve this greenfield invariant in review and CI |
| DEP-03 | P0 | partial | Compose/probes, fail-fast readiness and one-time/default-secret first-admin guards exist | Clean empty-volume multi-service startup, concurrent/replayed setup and dependency/listener negative probes |
| DEP-04 | P0 | scaffold | Two-Silo/two-Gateway topology and rolling/fault scripts exist | Execute drain/outage/rejoin/partition scenarios with exact financial/object assertions |
| DEP-05 | P0 | partial | TLS/backup/restore crypto and policy source, scheduled jobs execute inline with offsite upload and remote readback verification | End-to-end restore drill, measured RPO/RTO, ingress and rollback drills |
| DEP-06 | P1 | partial | ScalaAPI central CI/release validates an immutable pair, exact tags and evidence; Gateway worktree clean, both gitlinks advanced to latest, fake self-update endpoint removed, nanoid advisory resolved, pair validation passes | Run all paired gates with full evidence archive |
| DEP-07 | P0 | partial | Smoke/stress/load/fault scripts exist and are syntactically validated; pair validation passes; dual-process leadership assertions added | Run current short negative controls and the real 3600-second gate with cleanup evidence |

## Research capability decisions

The pinned Sub2API tree was used only to check whether the 65 selected domains hide
a material capability family. These are product decisions, not compatibility gaps:

| Research signal | ScalaAPI treatment |
| --- | --- |
| First-run dependency tests and setup UI | Mapped to DEP-03 through a product-native one-time first-admin workflow |
| Configurable upstream error pass-through/rewrite and monitor suppression | Mapped to GW-05/SEC-01; define a bounded safe native contract or explicitly reject it |
| Responses WebSocket, Live/sideband attestation and xAI Realtime | GW-10 covers only protocols explicitly named and tested by ScalaAPI |
| Binary self-update and rollback | Excluded from the service process; paired immutable deployment belongs to an external controller |
| Custom attributes, fingerprint policy, training opt-out and compliance views | Candidate details inside CORE-01/SEC-03/SEC-05/OPS-01 and require individual decisions |

No reference-system route, migration, table, key or behavior is an acceptance oracle.

## Deliberate exclusions

1. Sub2API private/Admin API, error-body or UI compatibility.
2. Sub2API PostgreSQL migration history, data import, IDs, hashes, API keys or state.
3. Redis/Garnet key compatibility, CDC/Debezium replay or dual read/write.
4. Compatibility aliases, version negotiation or fallback to reference behavior.
5. In-process binary self-update; release and rollback are external paired operations.

The authoritative machine-readable mirror is `feature-inventory.csv`; its 65 IDs,
statuses and closure statements must remain synchronized with this report.
