# ScalaAPI Rewrite Risk Register

Audit date: 2026-08-14. Baseline: Platform `bc083d1`, Gateway `b6e4e02`, Sub2API
non-normative research snapshot `origin/main@fbfdcef`.

Status values are `Open`, `Partial`, `Controlled` and `Accepted`. A control is not
closed by code presence alone. This project is a greenfield reimplementation: no
risk treatment may introduce Sub2API API/data/schema/key/state compatibility.

| Risk | Sev | Status | Current evidence | Required control |
| --- | --- | --- | --- | --- |
| Greenfield database cannot bootstrap | P0 | Open | Empty PostgreSQL 17 commits Orleans + 001-053 then migration 055 fails because `users` is absent; 055/056 also reference `api_keys` while product tables are `user_accounts`/`user_api_keys` | Correct product-native FKs/order; apply all 66 records twice; run full DB suite on that exact schema |
| Canonical/vendor contract drift | P0 | Open | Platform `dispatch.capnp` has audio enum values 12/13 and a stale local digest; Gateway vendor omits them; Platform digest and cross-repo comparison fail while Gateway local digest passes | Atomic paired schema/generated/digest change; remove hand-maintained numeric authority; block releases on byte equality |
| Database-test false green | P0 | Open | No-DB solution run says 502/502; 46 test files inspect `GREENFIELD_SCHEMA_CONNECTION`, with 123 direct early returns | Explicit integration fixtures and true skips/failures; DB job required for merges/releases with executed/skip totals |
| Scheduler performance gate cannot execute | P0 | Open | Dry run exits 1; all four cases lack reports because benchmark Orleans activation cannot resolve `ISlotLeaseStore`; ordinary and greenfield CI both invoke it | Register production-equivalent scheduler dependencies; require valid reports and regression thresholds; add a deliberate broken-DI probe |
| Lease/hold/ledger duplicate or loss | P0 | Partial | Transactional PostgreSQL stores and idempotency logic exist; Gateway 159 tests pass | Current empty-stack crash matrix and one-hour multi-instance proof after schema repair |
| Unknown Provider charge released as free | P0 | Partial | Forward/output evidence and reconciliation states exist; unit stream/cancellation tests pass | Current runtime timeout/disconnect/cancel/partial-output matrix for every adapter; only proven no-charge may release |
| Output/usage evidence lost before acceptance | P0 | Open | `output_started` is a post-write one-shot RPC with log-only failure; UsageReporter deletes unacknowledged non-retryable records | Durably queue output evidence; retain or convert every rejected record to a durable incident; crash/retry/restart invariants |
| Provider protocol/usage drift | P0 | Partial | Source mock and goldens cover OpenAI/Anthropic/Gemini/xAI-shaped paths | Provider-owned/live contract profiles, catalogue/tokenizer versions, header/content-type/error/terminal assertions |
| Upstream error policy is implicit | P1 | Open | ScalaAPI normalizes protocol errors, while the research input exposes configurable pass-through/rewrite/monitor-suppression behavior that is not an explicit product decision | Define bounded safe exposure/redaction/monitoring rules or reject the feature; never inherit reference behavior silently |
| Generic xAI/Grok support overclaim | P0 | Open | Identity, Bearer-compatible text goldens and account/quota storage exist but generic OpenAI shape does not prove native media/search/voice/quota/OAuth | Explicit capability matrix and native adapters/fixtures/live evidence; advertise only proven slices |
| Search/TTS/STT unusable on clean schema | P0 | Open | Source routes/stores/mocks exist; their migrations reference nonexistent tables; audio contract is also drifted | Repair G0-01/G0-02, then owner/bytes/settlement/provider E2E |
| Pricing or response-model mischarge | P0 | Partial | NUMERIC snapshots and specialized unit columns/tests exist; media hosted service still notes observed-model propagation TODO | Complete propagation and integrated mismatch/long-context/search/audio/media golden settlement |
| Quota/tier scheduler uses fabricated freshness | P0 | Open | CAS store exists, but refresh worker only enumerates seeded quota rows and rewrites current values | Real account inventory + bounded provider adapters + fenced refresh + explicit stale/unknown policy |
| Active monitor split brain or fake health | P1 | Open | Worker sets `IsLeader=true` in every process and simulates check execution | PostgreSQL fenced leadership, actual bounded probe, retry/incident/recovery and two-process evidence |
| Passive monitor duplicate/private rollups | P1 | Partial | Rollup/watermark/privacy stores exist; source comments retain a production leader placeholder | Durable leader fencing, duplicate/out-of-order/backfill/restart evidence and user-scope privacy tests |
| Backup reported offsite without bytes | P0 | Open | `UploadOffsiteAsync` records `completed` and a URL without performing I/O | Real S3 PUT + HEAD/readback checksum, retry, retention delete and failure evidence |
| Scheduled backup never creates artifact | P0 | Open | Scheduler acquires a DB claim and enforces retention, but only reads policy and marks claim completed | Schedule due logic must invoke actual backup/crypto/sign/offsite pipeline and audit outcome |
| Restore corrupts live authority | P0 | Partial | Live-target rejection/checksum/restore primitives exist | Isolated target runtime, corruption/wrong-key/partial restore faults, post-restore invariants and measured RPO/RTO |
| Stress gate produces false green | P0 | Open | Scripts query nonexistent `gateway_usage_outbox` and `reconciliation_incidents`; dead background child may only log; settlement timeout warns | Fix schema/source boundaries, make every failure fatal, add harness tests, then run actual 3600 seconds |
| Cross-repository release is not reproducible | P0 | Open | Repos have separate workflows and no tags; greenfield CI calls contract verification without a Gateway path; `deploy/release.sh` checks only Gateway's local digest before tagging both repos | Required paired refs/byte comparison/migration/image manifest and coordinating job with sibling credentials |
| Admin update reports work it never performs | P1 | Open | `/admin/system/update` fetches release metadata then says the binary was downloaded without downloading, verifying or installing bytes | Remove/disable under immutable paired deployment, or delegate to a real external controller and report only verified state |
| Publish gates are bypassable | P0 | Open | Platform `docker.yml` and Gateway `release.yml` independently publish tags/`latest`; both can act without one paired gate and publication precedes later clean rebuild checks | One protected non-bypassable paired workflow; all greenfield/contract/benchmark/E2E/security gates before tags/images |
| Release report fabricates success | P0 | Open | `deploy/release.sh` never runs tests benchmarks or clean builds but hard-codes all as passed and no skips | Capture commands exit codes executed/skipped totals and artifacts; refuse report/tag/push on absent evidence |
| Browser UI masks backend gaps | P1 | Partial | Both Web applications typecheck/build; Playwright exists but is not a general required workflow | Authenticated source-built browser matrix for mutation, authorization, expiry/replay and failures |
| Payment webhook/refund replay | P0 | Partial | Stripe-shaped signature/parser/state/ledger source and tests exist | Current DB/full HTTP crash/retry/secret-rotation and real provider checkout evidence |
| Identity token/session abuse | P0 | Partial | Hash/encryption/session/TOTP/Passkey/OAuth components exist | Multi-process concurrency, real mail/authenticator/browser flows, anti-enumeration and all-public-endpoint limits |
| Secret leakage or weak key custody | P0 | Partial | AES-GCM, redaction and target-header validation source exists | Production key custody/rotation, recursive scan, log/metric/Cap'n Proto dump probes and step-up authorization |
| Proxy/TLS profile is metadata-only | P1 | Partial | Proxy credentials are encrypted and passed; TLS fingerprint profiles have CRUD | Apply profile in outbound transport and prove wrong-name/expiry/rotation/proxy credential isolation |
| Provider target validation is incomplete | P1 | Open | Auth headers are bounded, but target path is concatenated, unknown method falls back to POST, general headers only drop hop-by-hop fields and TLS profiles are unused | Generated bounded target contract; reject unknown method/path/header/TLS values before Provider I/O |
| Content-policy bypass/order failure | P0 | Partial | Response evaluation is selected only for chat-classified HTTP capabilities; Search, Antigravity, audio/media, embeddings and models are excluded | Explicit per-capability request/response/binary matrix plus outage/order/rebuild/long-stream evidence |
| Realtime bypasses policy and client identity rules | P0 | Open | WebSocket dispatch omits request body, raw-relays frames, skips normal query validation and uses direct peer IP instead of trusted-proxy resolution | Bounded frame policy, shared query/IP rules, binary decision, audit/settlement ordering and WebSocket E2E |
| HTTP and RPC body limits conflict | P0 | Open | Gateway accepts 32 MiB and serializes full body while Platform drops framed requests above 1 MiB | Shared enforced limit or metadata/object-reference contract; explicit pre-lease error and multipart/media boundary tests |
| Gateway readiness reports an incomplete dependency set | P1 | Open | Construction can survive failed dependency or listener setup; `/ready` checks only dispatch UDS, not Garnet, SQLite durability or every per-core listener | Fail unusable startup; readiness and negative probes cover every mandatory dependency/listener |
| First-administrator bootstrap is unowned | P1 | Open | Empty schema is broken and the selected inventory previously called the dependency scan “independent bootstrap”; no explicit native setup contract is accepted | Choose deployment-command or bounded setup-UI ownership; test dependency failures, one-time admin creation, replay and default-secret rejection |
| Catalogue outage masquerades as no models | P1 | Open | Anonymous `/models` bypasses Platform and returns HTTP 200 empty when Garnet is unavailable | Product-native unavailable/stale response or proven cached snapshot; never present cache failure as authoritative empty state |
| Search streaming is advertised but unreachable | P1 | Open | Registry marks Search stream-capable, while handler enables streaming only for chat-classified capabilities | Route streaming through the supported bounded path with policy/usage evidence or remove the advertisement |
| Auth invalidation is coarse polling rather than subscription | P1 | Partial | Each core polls one Garnet version every two seconds and flushes its entire cache; vendored subscribe/resync RPC is unused | Declare polling as the product contract with lag/failure budgets or implement fenced subscription/resync and prove revocation latency |
| Object loss/duplicate after partial writes | P0 | Partial | Deterministic media keys, item reconciliation/retention and fault scripts exist | Current two-Silo object-store partition/partial PUT/committed-response-loss/retention runtime proof |
| Long connection/resource leak | P1 | Partial | Gateway stream timers and unit tests exist, but `GATEWAY_STREAM_TIMEOUT_MS` changes socket timeout without changing Forwarder's fixed total stream budget | One authoritative timeout contract plus one-hour stream/realtime/backpressure evidence with connection/buffer/outbox metrics |
| Observability cannot explain incidents | P1 | Partial | Metrics/alerts/audits/reconciliation stores and UI exist | Cross-service bounded correlation, alert delivery/recovery and durable operator evidence |
| Dependency/supply-chain drift | P1 | Partial | Retired dependency scan passes; Photon and tool versions are pinned in source | Hosted vulnerability/secret/SBOM/image provenance gates; verify pinned upstream availability and digests |
| Research input expands scope implicitly | P1 | Controlled | Sub2API was inspected at immutable `fbfdcef`; its divergent local CDC commit is excluded and upstream deltas are non-normative | Admit capabilities only through a ScalaAPI product decision; never turn reference commits or migration/data history into automatic gaps |
| Compatibility scope creep | P0 | Controlled | Architecture, contract README and static scan state greenfield/no compatibility | Reject legacy aliases, Sub2API data/keys/IDs/Redis/CDC and dual-read/write in review/CI |
| Reference behavior is unsafe/stale | P1 | Accepted | Sub2API breadth is used for discovery, not as release-quality acceptance oracle | Define ScalaAPI-native security/accounting/operations contracts and test them independently |

## Release blockers

The release is blocked until, at minimum:

1. the empty-schema migration and database-enabled suite pass;
2. canonical and vendored contracts are byte-identical at paired immutable refs;
3. Scheduler benchmarks emit valid reports and enforce their thresholds;
4. the verification harness itself is proven to fail on bad SQL/dead children;
5. output/usage evidence survives post-write RPC failure and non-retryable rejection;
6. Provider/financial crash paths pass in a current source-built stack;
7. scaffolded monitor/quota/backup paths are either completed or explicitly removed
   from advertised scope;
8. all alternate tag/image/release-report paths are removed or use the same required
   pre-publication gates;
9. Gateway startup/readiness and Provider target/policy boundaries pass negative probes;
10. first-admin setup is explicitly owned and the false-success update endpoint is removed;
11. the one-hour mixed fault/load gate and paired release manifest pass.

Historical smoke evidence remains useful for regression design, but it cannot close
any blocker that current source contradicts.
