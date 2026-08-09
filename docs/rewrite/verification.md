# ScalaAPI Rewrite Verification

## Current evidence

The latest source snapshot is Gateway `20d0b85` and Platform `bab9d44`.
The SEC-01 request-policy slice extends the single revision-3 Cap'n Proto contract
with bounded request content and a dedicated rejection code. Platform evaluates
scope-aware `log`/`block` rules after authentication but before scheduling,
persists match audits, and rejects blocked content before any lease or hold.
Gateway and generated Platform contracts match byte-for-byte; the checked-in
smoke now asserts HTTP 400 `content_policy_violation`, one audit, and zero leases.
The source contract slice now also validates OpenAI model-list entries, Gemini
model names/methods/token limits, and Anthropic positive bounded `input_tokens`;
the Provider mock exposes deterministic malformed, duplicate, and zero-count
profiles for these contracts.
Platform `c029b3c` adds versioned runtime configuration snapshots, bounded key/value
validation, secret/connection-string rejection, boolean-only feature flags, stale
version conflict handling, and Admin actor/IP audit persistence; the Grain suite
covers snapshot isolation and validation.
The latest source-built project `scalaapi-embeddings-20260809b` passed the
full empty-volume gate: 27 migration files applied and skipped on the second
run, Garnet authenticated the Gateway -> Platform path, two three-dimensional
float Embeddings and one two-dimensional base64 Embedding settled with the
active NUMERIC price version, and a shape-invalid Provider response returned
`502/provider_protocol_error` while retaining one unknown-charge lease. The
same run passed OAuth, API-key lifecycle, realtime, restarts, the Provider fault
matrix, reconciliation, and MinIO persistence; the cleanup trap removed its
temporary stack and tags.
The current source-built project `scalaapi-api-key-audit-verified` passed the
full 27-migration empty-volume gate and authenticated the API-key audit query:
the filtered denied event returned actor/action metadata and no plaintext-key
field. The complete Garnet, API-key replay/concurrency/expiry, auth/OAuth,
realtime, restart/recovery, Provider fault, reconciliation, and MinIO matrix
remained green; all temporary stack resources and images were removed.
The follow-up `scalaapi-api-key-lifecycle-verified` smoke passed the API-key
lifecycle assertions: Admin ownership guard/update/revoke, updated-key Chat,
revoked-key 401, and user self-service rotation with old/new state and audit
invariants. The lifecycle slice is evidence-backed. A later clean run also
passed the full Provider fault matrix, including `disconnect_before_output`.
The current source-built project `scalaapi-key-http-verified` passed the full
27-migration empty-volume gate with authenticated HTTP API-key replay and expiry
checks. Two simultaneous Chat requests sharing one idempotency key produced one
completed lease/idempotency row and no duplicate billing; a short-lived key was
rejected after expiry with HTTP 401 `authentication_error` before any lease was
created. The same run passed Garnet, auth/OAuth, realtime, restart/recovery,
Provider faults, reconciliation, and MinIO object assertions; temporary stack
resources and tags were removed.
The current source-built project `scalaapi-auth-abuse-verified3` passed the
27-migration empty-volume gate. Malformed registration returned HTTP 400; five
failed logins for one unknown email returned HTTP 401 and the sixth returned
HTTP 429 with `Retry-After`, backed by the new hash-only PostgreSQL
`auth_abuse_counters` table. The same run passed authenticated Garnet,
rotating-session and OAuth flows, realtime settlement, Platform/Gateway restart
recovery, the complete Provider fault matrix, audited reconciliation, and MinIO
signed object persistence; its cleanup trap removed the temporary stack and
stack-specific image tags.
The current source-built project `scalaapi-key-policy-verified` passed the
26-migration empty-volume gate. A key scoped only to `models` received HTTP 403
`permission_error` for Chat, produced one denied API-key audit row, and created
no request lease; the full accounting, restart, realtime, reconciliation,
Provider failure, and object-storage assertions also passed.
The current source-built project `scalaapi-oauth-refresh-20260809` passed the
full empty-volume gate after correcting the smoke DTO path to `.oAuth`: a seeded
expired `mock-access-v1` credential rotated over real HTTP to version 2 before
the first Chat dispatch, the request settled once, and Admin details remained
secret-free. The cleanup trap removed all project resources and temporary image
tags; the named `apitf_*` development resources were retained.
The current source project `scalaapi-scheduling-verified` also passed the full
26-migration empty-volume gate with authenticated Garnet, rotating auth sessions,
OAuth refresh, realtime settlement, Platform/Gateway restart recovery, the complete
Provider fault matrix, audited reconciliation, and MinIO signed object persistence.
Its cleanup removed all containers, volumes, networks, and stack-specific image tags;
only the named `apitf_*` development resources remain.
The Platform `3572abd` auth/scheduling/policy slice passes the full 143-test suite and Release
build with zero warnings: API-key scope normalization and projection tests pass,
unknown scopes are rejected, capability denials are classified before scheduling,
and migration/schema checks require the new scope, expiry, append-only audit, and
auth-abuse counter state. Authenticated HTTP replay/concurrency and expired-key
cases pass in `scalaapi-key-http-verified`; the authenticated API-key audit query
and lifecycle update/revoke/rotation pass in `scalaapi-key-lifecycle-verified`;
multi-instance contention and browser cases remain open.
Gateway CTest is 104/104. The complete empty-volume gate passed in
`scalaapi-realtime-smoke-20260809` with the 22-migration double-run,
Garnet-authenticated request path, realtime WebSocket settlement, nine
unknown-charge fault scenarios, and the `disconnect_after_usage` case: valid
usage observed before truncated SSE EOF settled exactly once through the durable
outbox. The gate ended with nine unknown-charge incidents, one audited operator
resolution, and eight remaining open incidents. Temporary smoke containers,
volumes, networks, and tags were removed by the cleanup trap; baseline `apitf_*`
development resources remain.
The canonical/vendor Cap'n Proto digest check passes for all three schemas. The
host `verify-generated-contracts.sh` command remains an environment gate on this
machine because the pinned `capnp` compiler is not installed; CI or the build
container must run the byte-identical C# generation check before release.
The recovery hook matrix also passed in `scalaapi-recovery-after-commit-0901`
with `platform.after_settlement_commit` and in
`scalaapi-recovery-debug-0903` with `platform.before_outbox_ack`: both observed
the Platform process exit, restarted the same container, and ended with one
terminal debit and eight remaining open incidents. Both temporary stacks,
volumes, networks, and image tags were removed after evidence capture.
The current source-built project `scalaapi-gateway-recovery-0907` also enabled
`gateway.after_provider_completion`: the Gateway request failed with an empty
transport reply, the same container was explicitly started, its marker survived,
and the forwarded lease reached `reconciliation_needed` with an active hold and
no usage/debit. The full gate ended with ten unknown-charge incidents, one
audited operator settlement, and nine remaining open incidents. Its containers,
volumes, network, and image tags were removed; baseline `apitf_*` resources were
retained.
The follow-up source-built project `scalaapi-gateway-dispatch-recovery-0911`
enabled `gateway.before_provider_dispatch`: Gateway terminated before Provider
contact, the same container was explicitly started, and the held lease safely
became `expired` with its hold/idempotency released and no usage/debit or new
incident. The full gate passed with nine unknown-charge incidents, one audited
settlement, and eight remaining open incidents; its temporary resources and image
tags were removed.
The current source-built project `scalaapi-platform-dispatch-recovery-0912`
enabled `platform.before_provider_dispatch`: Platform terminated after creating
the SQL lease/hold but before returning the upstream target. The same container
was explicitly started, the marker survived, and TTL recovery changed the lease
from `held` to `expired`, released the hold and idempotency row, and produced no
usage, debit, or incident. The complete gate passed with nine unknown-charge
incidents, one audited operator settlement, and eight remaining open incidents;
the cleanup trap removed the temporary project and image tags.
The current source-built project `scalaapi-platform-worker-recovery-0913`
enabled `platform.after_outbox_claim`: Platform completed the SQL settlement,
claimed the durable `complete` outbox item, and terminated before any Grain side
effect. The same container was explicitly started; the expired claim was
reclaimed and the outbox was applied once with no duplicate usage, debit, or hold
transition. The complete gate passed with nine unknown-charge incidents, one
audited operator settlement, and eight remaining open incidents; all temporary
resources and image tags were removed.
The current source-built project `scalaapi-platform-dispatch-retry-0914`
enabled `platform.before_provider_dispatch`: Platform terminated after the
lease/hold commit, Gateway retried the same request under the same idempotency
identity, and the replacement Platform rebuilt the active lease target. The
request settled exactly one lease, usage event, usage log, and NUMERIC debit;
the full matrix passed with nine unknown-charge incidents, one audited operator
settlement, and eight remaining open incidents. This smoke used a clean runtime
image assembled from Gateway `9c7171f`; full FetchContent history is required
because the pinned Photon commit is not advertised on an upstream discoverable
ref. All temporary resources and tags were removed.
The Platform `dd23bb4` Provider.Mock local WebSocket probe also passed: a real
HTTP/1.1 upgrade to `/v1/responses`, masked `session.update` input, deterministic
`session.created` and `response.done` usage frames (7 input/5 output), and a
clean close all completed successfully. The subsequent
`scalaapi-realtime-smoke-20260809` gate also validated Gateway-to-Provider
forwarding and settlement; long-connection soak remains.
The checked-in `deploy/stack/realtime_smoke.py` client performs the same protocol
probe without third-party dependencies, and the full-stack invocation now
asserts one realtime lease, usage event, usage log, committed hold, and ledger
debit. Gateway `9c7171f` builds cleanly from the immutable Photon pin by using a
full FetchContent checkout; the upstream ref does not advertise the pinned
commit, so shallow checkout is intentionally disabled.
The older detailed rows below retain prior checkpoint evidence; this snapshot
supersedes them where commit, image, or late-usage results differ.

| Gate | Result | Interpretation |
| --- | --- | --- |
| Gateway provider-completion crash recovery | `scalaapi-gateway-recovery-0907` source-built smoke passed; Gateway terminated after Provider completion, was explicitly restarted, and the same request retained one active hold in `reconciliation_needed` with no usage/debit | Durable lease evidence survives Gateway process loss; the one-shot marker prevents a restart loop, and the normal reconciliation/operator path remains authoritative |
| Gateway before-provider-dispatch crash recovery | `scalaapi-gateway-dispatch-recovery-0911` source-built smoke passed; Gateway terminated before Provider contact, was explicitly restarted, and the same held lease became `expired` with released hold/idempotency and no usage/debit | Never-forwarded work remains safe to expire after Gateway loss and does not create an unknown-charge incident |
| Platform before-provider-dispatch crash recovery | `scalaapi-platform-dispatch-recovery-0912` source-built smoke passed; Platform terminated after persisting an unforwarded lease/hold, the same container was restarted, and TTL changed `held` to `expired` with released hold/idempotency and no usage/debit/incident | Platform loss before the dispatch RPC response is safe to reclaim and cannot create a billable or unknown-charge outcome |
| Platform outbox-claim worker recovery | `scalaapi-platform-worker-recovery-0913` source-built smoke passed; Platform terminated immediately after claiming the completed settlement outbox, the same container was restarted, and the expired claim was reclaimed and applied exactly once | A worker crash before external Grain effects does not lose the durable event or duplicate the financial settlement |
| Platform dispatch retry and active-lease recovery | `scalaapi-platform-dispatch-retry-0914` source-built smoke passed; Platform died after the lease/hold commit, Gateway retried with the same request/idempotency identity, and replacement Platform recovered and settled the existing lease with exactly one lease, usage event, usage log, and debit | A lost dispatch response is retryable without allocating a second lease or billing twice |
| Embeddings contract smoke | `scalaapi-embeddings-20260809b`, exit 0 | Gateway `40cb02f` and Platform `ef1e474` returned two three-dimensional float vectors and one two-dimensional base64 vector, settled both against the active NUMERIC Embeddings price, and mapped a shape-invalid Provider response to `502/provider_protocol_error` with one retained `reconciliation_needed` hold; the same run applied and re-ran all 27 migrations and passed the full Garnet, OAuth, restart, Provider, reconciliation, and MinIO matrix |
| Gateway build and CTest | Clean local build; 111/111, exit 0 | Adds fail-closed OpenAI Responses completed-envelope/output/usage checks, OpenAI model-list duplicate/metadata checks, Gemini model name/method/token-limit checks, and Anthropic bounded positive token-count checks to the Embeddings request/response, nested Anthropic usage, SSE, timeout, cancellation, Garnet, retry, and routing coverage |
| Platform tests | 161/161, exit 0; Host 37/37 rerun against temporary PostgreSQL 17 | 69 Grain tests, 37 Host tests, 18 Admin tests, and 37 Provider mock tests; real-database Host coverage includes content-policy block auditing, scope isolation, fail-closed request-size limits, schema assertions, accounting, media, and reconciliation |
| Pre-dispatch content policy | Gateway `20d0b85`; Platform `bab9d44` | Canonical/vendor/generated Cap'n Proto contracts match; a dedicated content-policy rejection maps to HTTP 400, active scope-aware rules run before lease creation, matches are audited, and `deploy/stack/smoke.sh` asserts zero leases for a blocked request. Response moderation, classifier adapters, alerts, and browser evidence remain open |
| Runtime configuration contract | Platform `c029b3c` Grain tests and Admin Release build | `feature.*` values are boolean-only, sensitive keys and connection strings are rejected, updates use an expected version, returned snapshots are independent copies, and successful Admin writes persist `config.update` actor/IP audit rows; dynamic consumer reload and browser controls remain |
| Authentication abuse smoke | `scalaapi-auth-abuse-verified3`, exit 0 | Fresh PostgreSQL applies 000 plus 001-026, second migrator run skips all 27; malformed registration is 400, five bad logins are 401, the sixth is 429 with `Retry-After`, and the full Garnet/protocol/restart/Provider/reconciliation/MinIO matrix remains green |
| API-key HTTP replay and expiry smoke | `scalaapi-key-http-verified`, exit 0 | Two concurrent Chat requests share one idempotency key and leave one completed lease/idempotency row; a short-lived key returns 401 `authentication_error` after expiry with no lease; the complete stack matrix remains green |
| API-key authenticated audit smoke | `scalaapi-api-key-audit-verified`, exit 0 | Admin token reads a filtered denied event by key hash, receives actor/action metadata without plaintext-key fields, and the complete empty-volume Garnet/Provider/restart/reconciliation/MinIO matrix remains green |
| Realtime smoke client | `python3 deploy/stack/realtime_smoke.py` passed against Release `Provider.Mock`; `bash -n deploy/stack/smoke.sh` and `git diff --check` passed | Validates HTTP/1.1 upgrade, masked session input, deterministic session/usage frames, close handling, and full-stack exactly-once lease/usage/hold/ledger settlement using the clean Gateway image |
| Platform Release build | Passed, 0 warnings and 0 errors | Includes Platform Host, Admin API, migrator, Provider mock, and benchmark assembly |
| Admin Web | Typecheck and production build passed | Provider account form supports static headers and OAuth refresh metadata/replacement while list/details expose health but not stored secrets; browser tests are not configured |
| User Web | `npm run typecheck` and `npm run build` passed in `user-web` | Independent Solid client covers auth, OAuth callback, password recovery, email verification, dashboard, scoped usage, API keys, active-plan subscription purchase/cancel/renew, redeem codes, referral summary, profile, and TOTP setup/verify/disable; browser tests and backup-code sign-in remain |
| Provider OAuth credential refresh | Grain, Host, and Provider Mock tests pass for a single-account refresh lease, concurrent exclusion, CAS completion, rotated token/header hydration, safe metadata updates, persisted bounded failure/backoff, HTTPS-only token endpoint, bounded form response, secret-free errors, and real HTTP rotation/revocation/malformed/oversized response contracts. The `scalaapi-oauth-refresh-20260809` empty-stack gate proves an expired seeded credential rotates to version 2 before a billable Chat dispatch and never appears in Admin details | Add provider-specific parameters, refresh audit history, multi-Silo contention, and real provider adapter fixtures |
| Scheduler benchmark dry run | 4/4, exit 0; no-match negative probe exits 1 | Dependency injection and child-result propagation pass; not performance evidence |
| Contract generation and digest | Canonical and Gateway vendor schemas match at the content-policy extension; fixed-scale pricing round-trip passed; official Cap'n Proto 1.0.2 commit `1a0e12c0` plus local `capnpc-csharp` 1.3.118 regenerated all three C# files byte-identically; an intentional drift probe exited 1 with a unified diff | Platform's single-repository generated-output gate is blocking; atomic cross-private-repository schema release coordination remains |
| PostgreSQL migrator | A temporary empty PostgreSQL 17 database applied product migrations 001-027 and skipped all 27 on replay; migration 027 constrains content rule actions/status/pattern length and indexes active scopes | Compose also installs Orleans support and now requires 28 total migration records; the complete current-source empty-stack run remains pending. No source database, CDC, compatibility table, snapshot, or old key was used |
| Empty-volume Compose gate | `deploy/stack/smoke.sh` passed from Platform `3572abd` in unique project `scalaapi-oauth-20260809b`; the stack applied all 27 migration files, skipped all 27 on the second run, drove external OAuth mock authorization-code exchange with PKCE/account binding/replay rejection, authenticated Garnet, settled/replayed Chat and realtime WebSocket, exercised restart/recovery and the complete Provider matrix, resolved one incident, and persisted/downloaded the MinIO object | Source-owned gate proves ordered accounts, evidence-backed holds, exactly-once recovery including dispatch retry and outbox claim reclaim, OAuth login exchange and credential refresh over mock HTTP contracts, safe never-forwarded expiry, late usage settlement, actual client cancellation retention, and audited unknown-charge resolution. Hosted CI, runtime WebSocket soak, multi-instance recovery, and cross-protocol automation remain |
| Garnet smoke | Auth, PING, SET/GET, PX, INCR, DEL passed | Official digest; no Redis or embedded server |
| Empty-volume Embeddings gate | `deploy/stack/smoke.sh` passed from Platform `ef1e474` in unique project `scalaapi-embeddings-20260809b` | The current source applied all 27 migrations and skipped all 27 on the second run, authenticated Garnet, settled two float and one base64 Embeddings request against the NUMERIC price version, retained one unknown-charge hold for a shape-invalid response, and passed the full restart, Provider, reconciliation, OAuth, realtime, and MinIO matrix |
| Garnet outage/recovery | Platform readiness 503 then 200 | Automatic TCP reconnect verified |
| Garnet projection rebuild | `discovered=15`, `written=15`, `deleted=0`, `errors=0`; immediate `scalaapi:v1:auth:*` read succeeded; Gateway CTest covers version change and deleted-version flush/recovery | TLS and multi-client assertions remain |
| Provider mock | Health, OpenAI Chat/Responses/models/embeddings, Anthropic Messages/count-tokens/SSE, Gemini models/generation/SSE, synchronous media, asynchronous image/video tasks, and deterministic OAuth form token rotation/revocation/malformed/oversized/timeout contracts are source-owned; normalized Chat input selects deterministic faults and nine independent seed groups isolate scheduler state. Non-stream and streaming OpenAI 429/500 explicitly reject and release without debit; malformed success, timeout, and invalid streaming content type retain unknown-charge holds without retry. Direct non-stream and zero-output streaming resets return 503/provider_unavailable; partial SSE resets may end as 000/200/502/503 after output has begun, while all retain the hold. The `disconnect_after_usage` profile emits valid usage and closes before `[DONE]`, and the smoke proves one late settlement. Final Anthropic SSE settlement stored 32 input/5 output tokens and `0.00017100` cost | Gateway terminal-event, exact media-type, cancellation classification, normalized connection errors, incomplete chunked-body handling, bounded pre-header timeout response, independent inter-chunk/total timer tests, truncated-stream usage parsing, idempotent usage reconnect backoff, and shared bounded retry for HTTP/realtime Platform dispatch loss are in `9c7171f`; Platform `da62f74` Provider Mock HTTP contract tests classify rotation/revocation/malformed/oversized/timeout responses, while the `9320320` empty-stack gate proves expired credential rotation before dispatch, its secret-free audit row, and secret-free Admin reads. Provider-specific fixtures, runtime WebSocket soak, real adapters, golden fixtures, and remaining Gateway crash boundaries remain |
| External OAuth Provider mock smoke | Provider mock authorization codes are single-use and bind the configured client, exact redirect URI, and S256 verifier; `scalaapi-oauth-20260809b` passed start -> authorize -> callback, persisted `github` / `mock-oauth-user` account binding, returned the user session, and rejected a second callback with `oauth_state_replayed` (400) | Account-link collision policy, production redirect allowlists, and browser callback automation remain |
| Embeddings Provider contract | Source-owned mock and HTTP tests at Platform `ef1e474` | The mock returns one deterministic vector per input, honors requested dimensions and `float`/`base64` encoding, reports input usage, and exposes deterministic 429/500/malformed/shape-invalid profiles; Gateway `40cb02f` validates the response before settlement and retains an unknown-charge lease on malformed success. Provider-specific tokenizers and golden fixtures remain |
| Model catalog and token-count contracts | Gateway `6243b2d` CTest and Platform `d126ea5` Provider mock tests | OpenAI model-list entries require unique IDs, positive creation timestamps, and owner metadata; Gemini list/detail entries require `models/` names, methods, and positive input/output token limits; Anthropic count-token responses require positive bounded `input_tokens`. Malformed, duplicate, zero, and invalid profiles fail closed before settlement; provider-specific catalog authority and tokenizer/golden fixtures remain |
| Responses envelope contract | Gateway `b27965f` CTest | Direct non-stream Responses success requires a completed response object, model/id metadata, non-empty typed output, and consistent positive usage; malformed payloads are mapped to `502/provider_protocol_error` with the charge retained for reconciliation |
| Gateway dispatch smoke | Seeded OpenAI Chat, Responses, models, embeddings, synchronous image, and asynchronous image/video requests returned success; independent Anthropic and Gemini groups returned 200 and settled against their own price versions; protocol-native JSON-on-stream injection returned bounded 503, four aborted leases/released holds, zero usage/debits, and no Photon overflow | Full cross-protocol conversion/failure matrix and empty-stack automation for non-Chat protocols remain |
| Media lifecycle smoke | Image and video create calls returned durable `med_*` IDs; Platform polling copied provider bytes to MinIO, persisted `object_status=stored`, object key/ETag/size, and returned one-hour SigV4 URLs that downloaded `image/png` or `video/mp4`; batch `delete_outputs` returned 200, removed the object (old URL 404), cleared metadata, and terminal operation delete returned 204; a signature failure remained retryable without settlement | Object-vs-database reconcile/restore, cancel/failure/restart, and batch create coverage remain |
| Billable settlement smoke | JSON, SSE, and realtime WebSocket completed; SQL-authoritative holds committed; usage outbox processed; one versioned NUMERIC ledger debit per successful lease; account balance equalled ledger sum, max version equalled account version, versions were contiguous/distinct, and projection backlog reached 0. Platform dispatch retry after process loss, before-provider-dispatch, after-outbox-claim, pre-settlement, post-settlement, and before-outbox-ack crashes plus Gateway before-provider-dispatch and after-provider-completion crashes were recovered by explicitly starting the same container; never-forwarded work expired safely at both dispatch boundaries, while forwarded ambiguity retained one reconciliation hold. Explicit non-stream and streaming 429/500 attempts released; malformed/disconnect/timeout-before-headers plus streaming disconnect, disconnect-before-output, malformed-usage, invalid-content-type, and downstream client-cancel attempts each retained one unknown hold. The truncated-stream profile settled valid usage exactly once before EOF; Admin settle replay returned `duplicate`; realtime settled exactly one lease/event/log/hold/debit; the latest gate ended with eight open incidents after nine were created | Runtime WebSocket soak, multi-instance recovery, and clean hosted CI image reproducibility remain |
| Ordered accounting and reconciliation | All current money effects use one per-user serialized store. Real-database tests prove 20 concurrent versions, replay/conflict, hold oversubscription, protected debit, final account/ledger equality, safe terminal-hold and projection repair, mismatch/unknown-charge incidents, retained unknown-charge hold/idempotency, late settlement, audited settle/release, same-key conflict, concurrent decision serialization, and later incident resolution. A PostgreSQL advisory lock serializes scheduled and Admin runs; the pre-settlement fault smoke proves reconnect/outbox replay remains exactly-once | Add multi-Silo lock evidence, alerts, and all remaining deterministic crash hooks; subscription/affiliate effects must adopt the same authority contract |
| Administrative balance adjustment | New users started at zero; the first authenticated adjustment returned balance 1000 and ledger version 1, exact replay returned `duplicate=true`, changed replay returned 409, and an excessive debit returned 409. PostgreSQL held one `admin_adjustment` NUMERIC row and one `balance.adjust` actor audit. The real-database store test also covered an active hold that prevents a debit | Browser assertions and authorization matrix remain |
| Request idempotency smoke | Concurrent same-key calls produced one 200 and one active-lease 409; after settlement a matching retry returned the original body; different fingerprint produced 409. Platform dispatch retry, Platform and Gateway before-provider-dispatch loss safely reused or expired their original lease/key; Platform after-outbox-claim recovery reclaimed the completed event; forwarded/unknown outcomes stay blocked in `reconciliation_needed`; late completion and truncated-stream usage finalize the original key once. Platform pre/post/pre-ack and Gateway after-provider-completion crash replays each produced one terminal debit or one retained reconciliation hold; Gateway source tests also pin the shared HTTP/realtime retry identity policy | Streaming cancellation/partial output is non-retryable in Gateway source; runtime WebSocket replay/settlement and multi-instance replay remain |
| Price snapshot smoke | Lease persisted `runtime-v1` and NUMERIC input/output rates; changing the in-memory price to `runtime-v2` before completion left the original cost unchanged; Admin published/closed a version, rebuilt Platform loaded `stage2-live-1786199990`, and mock embedding/image/video leases stored their active database versions and NUMERIC rates | Media-unit pricing, historical backfill, and provider price adapters remain |
| Quota projection coherence | A low-quota key completed one current-image request; after settlement and projection rebuild, the next request returned `401 authentication_error` with `Quota exhausted` instead of using a stale Gateway auth cache | Subscription entitlements, grant lifecycle, and distributed concurrent reservation remain |
| Payment webhook state machine | Current Admin image created order `id=2`, accepted signed success once, replayed it as `duplicate=true`, accepted signed refund, and persisted `paid -> refunded` plus one `payment_credit` and one `payment_refund` ledger effect; a seeded pending event was claimed on attempt 1 and recovered to `applied` with zero pending events | Provider-specific adapters, reconciliation UI, and crash injection at the exact SQL/cluster boundary remain |
| Admin settlement queries | Ledger, lease, and hold endpoints returned current PostgreSQL rows with user filters | Pagination/export and browser assertions remain |
| Reconciliation incident lifecycle | A real PostgreSQL test deliberately corrupted one account, one terminal hold, and one Grain projection while creating an additional unknown-charge lease. The Admin API settled one incident with actor/reason/evidence, replayed the same command, and the next run preserved its resolved state. The latest full-stack fault gate persisted nine unknown-charge incidents; Platform and Gateway before-provider-dispatch loss safely expired outside the incident set, Platform dispatch retry recovered its active lease without an incident, Platform after-outbox-claim recovery reclaimed its completed event without an incident, Gateway after-provider-completion loss retained one incident, and one audited decision left eight open incidents | Add alert delivery, multi-Silo concurrency, runtime WebSocket soak, and remaining crash hooks |
| Auth lifecycle smoke | `scalaapi-auth-session-verified` passed login -> refresh rotation, rejected old refresh-token replay with 401, rejected the replaced access token, accepted the replacement, and rejected it after `/user/logout`; the empty-volume gate also completed the full billing/fault matrix | Multi-device session management, browser UX, session audit/retention, and hosted-CI evidence remain |
| TOTP lifecycle | Real PostgreSQL Admin tests enable TOTP, reject same-time-step replay, consume one backup code exactly once, reject backup codes for disable, lock after five failures across two service instances, and recover after the 15-minute lockout; User Web builds the setup/verify/disable and backup-code display workflow | Browser setup and backup-code sign-in tests remain |
| OAuth PKCE and external exchange | Real PostgreSQL Admin tests issue GitHub/Google state, reject provider/redirect/verifier mismatches, consume accepted state once, persist `consumed_at`, and reject expiry after ten minutes. The source-owned Provider mock authorization endpoint persists one-time codes bound to client, redirect URI, and S256 verifier; `scalaapi-oauth-20260809b` drives start -> authorize -> callback, verifies the bound `oauth-user@example.test` identity, and rejects state replay with HTTP 400 | Account-link collision policy, production redirect allowlists, and browser callback tests remain |
| User portal contract | User Web uses refresh-aware `/auth`, password recovery and email verification, scoped `/user/usage` and `/user/usage/balance`, API key, payment, active-plan subscription, redeem, referral, profile, password, and TOTP contracts; repository test proves usage rows cannot cross user boundaries | Browser automation, backup-code sign-in, real payment provider checkout, referral reward settlement, and commercial audit evidence remain |
| Redeem-code settlement smoke | First redemption returned 200; repeat returned 409; after a Silo contract restart a committed redemption remained 409 and replayed its balance effect; one redemption and one NUMERIC ledger row were observed | Concurrent HTTP contention and audit-event assertions remain |
| Provider failover idempotency | Matching external idempotency keys reopen only after explicit Provider rejection or never-forwarded expiry. Active/completed keys retain replay/conflict semantics, while forwarded transport/protocol ambiguity stays blocked in `reconciliation_needed`; Host coverage and the stack fault matrix passed | Cross-protocol failover response replay remains |
| Password recovery | Explicit local debug mode issued a one-time token; first confirmation returned 204, token replay returned 400, and new-password login succeeded; User Web exposes request/confirmation forms and a login entry point | Real mail provider delivery and browser recovery flow remain |
| Email verification | Explicit local debug mode issued a one-time token; first confirmation succeeded, replay returned 400, and PostgreSQL persisted `email_verified=true` with a timestamp; User Web builds request/confirmation forms and links them from unverified profiles | Real mail provider delivery and browser verification flow remain |
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
4. Extend the now-normalized `503/provider_unavailable` Provider reset contract to
   replay after process failure across adapters. The distinct inter-chunk/total timer
   contract, actual downstream client socket cancellation, and usage-before-EOF
   settlement are covered by the latest empty-stack gate.
   The hook implementation and Platform pre-, post-, and pre-ack settlement
   recovery are source-smoke proven; Gateway before-provider-dispatch safe expiry
   and after-provider-completion recovery are also covered by the latest smoke.
   Platform dispatch retry and active-lease recovery are proven for regular Chat,
   and the shared retry policy covers realtime at source level. Exercise runtime
   WebSocket soak, other Gateway crash boundaries, and outbox-acknowledgement
   boundaries. Require each restart outcome to settle, safely
   release, or remain a durable reconciliation incident without redispatch. The
   current audited operator settle/release path must be exercised at those boundaries
   rather than bypassed.
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
