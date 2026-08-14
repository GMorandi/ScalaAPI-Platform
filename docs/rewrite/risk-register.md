# ScalaAPI Rewrite Risk Register

Audit date: 2026-08-14. Code baseline: Platform `30d82d0`, Gateway `98c62fd`,
ScalaAPI pair `032721b`, and Sub2API non-normative research snapshot
`origin/main@fbfdcef`.

Status values are `Open`, `Partial`, `Controlled` and `Accepted`. Code presence alone
does not control a runtime risk. ScalaAPI is a greenfield reimplementation: no risk
treatment may introduce Sub2API API/data/schema/key/state compatibility.

| Risk | Sev | Status | Current evidence | Required control |
| --- | --- | --- | --- | --- |
| Greenfield database cannot bootstrap | P0 | Controlled | Platform `30d82d0` applies Orleans 000 plus 65 product migrations in isolated PostgreSQL 17, skips all 66 on rerun and passes 502/502 DB tests | Keep migration manifest, double-run and schema tests in every paired gate |
| Canonical/vendor contract drift | P0 | Controlled | Latest Platform/Gateway checksums and all three files match; generated C# comparison and current supported pair validation pass | Keep byte equality, generated output, compiler identity and paired digest in each release |
| Database-test false green | P0 | Controlled | Missing `GREENFIELD_SCHEMA_CONNECTION` produces 113 Host failures instead of silent returns; DB-enabled suite passes 502/502 | Preserve explicit prerequisite failure and report DB identity plus executed/skipped totals |
| Scheduler performance gate cannot execute | P0 | Controlled | Six Platform Dry cases, including four Scheduler cases, emit successful reports | Keep valid-report checks and add measured regression thresholds |
| Latest Gateway evidence is contaminated by a dirty worktree | P0 | Open | Shared worktree modifies `gateway_handler.cpp` and `test_protocol.cpp` and fails compilation because `LeaseAbortDisposition::Safe` does not exist | Resolve the WIP without discarding user changes, then build/test/benchmark a clean immutable Gateway ref |
| Cross-repository release is not reproducible at latest heads | P0 | Open | ScalaAPI `032721b` validates its older Platform `e73a5d8` + Gateway `777278e` pair; standalone heads `30d82d0` and `98c62fd` are newer | Deliberately advance both gitlinks and capture one full paired evidence run before publishing |
| Independent cross-repository workflow can fail before its gates | P1 | Controlled | Platform Greenfield Verification #242 failed at an obsolete `scalaapi/gateway` checkout; the workflow is removed and ScalaAPI Paired CI #6 passed the equivalent gates | Keep cross-repository validation only in ScalaAPI and reject new component-local paired publishers |
| Lease/hold/ledger duplicate or loss | P0 | Partial | Fresh-schema transactional state and focused tests pass; durable Gateway paths exist | Run current two-Silo/Gateway crash, retry, replacement and one-hour financial invariants |
| Unknown Provider charge released as free | P0 | Partial | Forward/output evidence, reconciliation states and conservative unknown disposition exist | Runtime timeout/disconnect/cancel/partial-output matrix for every adapter; only proven no-charge may release |
| Output/usage evidence lost before acceptance | P0 | Partial | Durable `output_started` evidence and retention of unacknowledged non-retryable events are implemented | Prove HTTP/realtime crash/retry recovery and exactly one durable outcome per lease |
| Provider protocol or usage drift | P0 | Partial | Source mock and goldens cover OpenAI/Anthropic/Gemini/xAI-shaped paths and recent conversion edge cases | Provider-owned/live profiles with versioned headers/content types/errors/terminal usage/catalogue/tokenizer evidence |
| Upstream error policy is implicit | P1 | Open | ScalaAPI normalizes errors but has no explicit product decision for safe pass-through/rewrite/monitor suppression | Define bounded redaction/exposure/monitoring rules or reject the capability explicitly |
| Generic xAI/Grok support is overclaimed | P0 | Partial | Identity, text fixtures, credential state and an xAI quota client exist; native search/media/voice/OAuth behavior is not proven | Publish an explicit capability matrix and advertise only Provider-tested slices |
| Search/TTS/STT are mistaken for complete because schema now passes | P0 | Partial | Fresh-schema state, synchronized contract, routes, stores and mock tests pass | Real Web/X/audio Provider, bytes/ownership/streaming and specialized settlement E2E |
| Pricing or response-model mischarge | P0 | Partial | NUMERIC snapshots and specialized unit fields/tests exist; media observed model now reaches settlement | Integrated mismatch, long-context, search, audio, character and media golden settlement |
| Quota/tier scheduler uses incomplete account authority | P0 | Partial | OpenAI/Anthropic/Gemini/xAI quota clients and CAS refresh exist, but enumeration still reads seeded quota rows | Active account inventory, bounded faults, stale/unknown policy and fenced multi-Silo refresh evidence |
| Active monitor split brain or false health | P1 | Partial | PostgreSQL advisory-lock code and bounded HTTP channel probes exist | Two-process fencing, Provider-specific probe, outage/incident/recovery and restart evidence |
| Passive monitor duplicate or private rollups | P1 | Partial | PostgreSQL advisory-lock code, watermark/dedup/privacy stores and percentile fix exist | Duplicate/out-of-order/backfill/restart/privacy proof across processes |
| Scheduled backup records a job without producing an artifact | P0 | Partial | Scheduler claims a due interval and inserts a `running` job; source still notes actual `pg_dump` needs a worker | Execute job-to-artifact idempotently, fail stale jobs and prove encrypted/signed artifact creation |
| Offsite backup is reported without remote verification | P0 | Partial | Offsite service performs HTTP PUT and records local checksum; no current remote HEAD/readback proof | Remote checksum/readback, retry, partial-upload cleanup and retention deletion evidence |
| Restore corrupts live authority | P0 | Partial | Live-target rejection, checksum, decrypt and restore primitives exist | Isolated-target corruption/wrong-key/partial failure drills, post-restore invariants and measured RPO/RTO |
| Stress gate produces false green | P0 | Partial | Ownership SQL and fatal child/settlement handling are corrected; no current 3600-second run is retained | Run bad-SQL/dead-child/timeout negatives, short fault gate and actual 3600-second test with cleanup evidence |
| HTTP and RPC body limits conflict | P0 | Open | Gateway accepts 32 MiB and serializes the full body while Platform closes framed requests above 1 MiB | One shared pre-lease limit or bounded metadata/object-reference contract with media boundary tests |
| Realtime bypasses later-frame policy | P0 | Partial | Clean Gateway head includes initial body, trusted-proxy/query validation and initial capability policy; later frames remain raw | Bounded frame evaluation, binary/audio decision, audit/settlement ordering and reconnect E2E |
| Gateway readiness reports an incomplete dependency set | P1 | Partial | Clean source includes dispatch/Garnet/SQLite/listener fail-fast readiness; current shared WIP is not runnable | Clean immutable runtime with each dependency and listener failed independently |
| First-administrator bootstrap is unowned | P1 | Partial | One-time lockout and default-secret rejection guards are implemented | Empty-volume concurrent/replayed initialization and browser/deployment proof |
| Catalogue outage masquerades as no models | P1 | Controlled | Anonymous models returns 503 when Garnet is unavailable and Platform aggregates active-account models | Define stale/cached snapshot policy and prove outage recovery |
| Search streaming is advertised but unreachable | P1 | Open | Registry marks Search stream-capable while handler streaming remains limited to another capability class | Route it through bounded policy/usage or remove the advertisement |
| Auth invalidation is coarse polling | P1 | Partial | Cores poll a Garnet version and flush cache; subscribe/resync is unused | Declare polling lag/failure budgets or implement fenced subscription/resync and prove revocation latency |
| Object loss or duplication after partial writes | P0 | Partial | Deterministic keys, media/item reconciliation/retention and fault scripts exist | Two-Silo MinIO partition/partial PUT/committed-response-loss/retention runtime proof |
| Long connection or resource leak | P1 | Partial | Gateway stream timers and tests exist, but timeout configuration is not one authoritative end-to-end budget | One-hour stream/realtime/backpressure evidence with connection/buffer/outbox metrics |
| Observability cannot explain incidents | P1 | Partial | Metrics, alerts, audits, reconciliation stores and UI exist | Bounded cross-service correlation, alert delivery/recovery and durable operator evidence |
| Browser UI masks backend gaps | P1 | Partial | Admin and User Web clean install, typecheck and production build pass | Required authenticated source-built browser matrix for mutation, authorization, expiry/replay and failures |
| Web dependency vulnerability ships | P1 | Open | Both `npm audit` runs report the high-severity `nanoid <3.3.18` advisory (GHSA-2v37-7h3g-55p8) | Upgrade/override the dependency, rerun tests/build/audit and gate high/critical advisories |
| Payment webhook/refund replay | P0 | Partial | Stripe-shaped signature/parser/state/ledger source and tests exist | Full HTTP/database crash/retry, secret rotation and real checkout evidence |
| Identity token/session abuse | P0 | Partial | Hash/encryption/session/TOTP/Passkey/OAuth components exist | Multi-process concurrency, real mail/authenticator/browser flows and all-public-endpoint limits |
| Secret leakage or weak key custody | P0 | Partial | AES-GCM, redaction and target-header validation source exist | Production custody/rotation, recursive scans, log/metric/RPC dump probes and step-up authorization |
| Proxy/TLS profile is metadata-only | P1 | Partial | Proxy credentials are encrypted; TLS profile CRUD exists and unsupported profiles are rejected | Implement applied transport behavior or retain explicit unsupported state; prove rotation/expiry/isolation |
| Provider target validation is incomplete | P1 | Partial | Unknown methods return 405 and TLS profiles are not silently ignored | Bound path/general headers before I/O and prove the generated target contract |
| Content-policy coverage/order is incomplete | P0 | Partial | Initial capability policy selection covers more HTTP/realtime paths; later frames and binary/media cases remain open | Explicit request/response/binary matrix plus outage/order/rebuild/long-stream evidence |
| Publish gates are bypassable | P0 | Controlled | Component publishers were centralized; ScalaAPI validates a pair before exact-tag image jobs and publishes no `latest` | Keep branch protection and ensure no alternate publisher is reintroduced |
| Release evidence omits executed test totals | P0 | Open | Pair manifest/evidence scripts validate refs and image metadata, but release JSON writes fixed gate names with `skipped: []` without parsing TRX/Gateway/Web results | Parse uploaded results, fail on missing/contradictory totals and retain executed/passed/failed/skipped evidence |
| Admin update reports work it never performs | P1 | Open | `/admin/system/update` reads metadata then claims a binary download without downloading/verifying/installing bytes | Remove/disable under immutable deployment or delegate to a real external controller |
| Dependency/supply-chain drift | P1 | Partial | Retired dependency scan passes and tool versions are pinned; web advisory remains | Hosted vulnerability/secret/SBOM/image provenance gates and pinned upstream digest checks |
| Research input expands scope implicitly | P1 | Controlled | Sub2API is inspected at immutable `fbfdcef`; its local CDC commit and upstream deltas are non-normative | Admit capabilities only through explicit ScalaAPI product decisions |
| Compatibility scope creep | P0 | Controlled | Architecture and static scan state greenfield/no compatibility | Reject legacy aliases, data/keys/IDs/Redis/CDC and dual read/write in review/CI |
| Reference behavior is unsafe or stale | P1 | Accepted | Sub2API breadth is discovery input, not a release-quality oracle | Define ScalaAPI-native security/accounting/operations contracts independently |

## Release blockers

Release remains blocked until, at minimum:

1. the Gateway WIP is resolved and a clean immutable Gateway build/test/benchmark passes;
2. both ScalaAPI gitlinks are deliberately advanced and the latest pair passes one central run;
3. the two `nanoid` advisories are remediated and browser E2E becomes a required gate;
4. Provider protocol/quota/catalogue, monitor and backup paths pass current runtime faults;
5. HTTP/RPC size policy and later-frame realtime content policy are explicit and tested;
6. the corrected short smoke plus negative controls pass from a clean source-built stack;
7. the actual 3600-second mixed fault/load gate passes with durable invariants and cleanup;
8. the final exact-tag images and release evidence are produced only from that paired run,
   with the false-success self-update endpoint removed or delegated to a real controller.

Historical evidence remains useful for regression design, but it cannot close a risk
when the current source, worktree or selected pair contradicts it.
