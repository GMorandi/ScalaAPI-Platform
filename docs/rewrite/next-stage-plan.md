# ScalaAPI Rewrite Next-Stage Plan

Baseline: Platform `bc083d1`, Gateway `b6e4e02`, Sub2API research snapshot
`origin/main@fbfdcef`; audit date 2026-08-14.

This plan completes a greenfield implementation. It does not migrate, emulate or
remain compatible with Sub2API. The research repository can suggest candidate
capabilities but cannot add scope without a product decision. Every task below
produces a ScalaAPI-native contract and may delete or replace current incomplete
design without a compatibility period.

## Execution rules

1. Work in dependency order. A later feature cannot be accepted while its schema,
   contract or evidence gate is red.
2. Start from an empty product database. Never add Sub2API tables, migrations,
   aliases, data transforms, CDC, Redis or dual-read/write to make tests pass.
3. Platform owns the canonical Cap'n Proto schema; Gateway vendors the exact same
   bytes. Change both repositories and generated output in one reviewed release.
4. A test that lacks its prerequisite must report a true skip or fail the integration
   job. A direct `return` must never be counted as executed integration coverage.
5. A mock can freeze a protocol contract, but a production Provider/operation slice
   additionally needs its adapter and failure/recovery evidence.
6. Money remains PostgreSQL-authoritative. Unknown Provider outcomes retain holds
   and reconciliation evidence; no new feature creates a second ledger.

## Phase 0: Restore truthful foundations

### G0-01 Repair the greenfield migration chain

Status: `TODO`, release blocking.

- Replace erroneous `users` / `api_keys` references in migrations 055 and 056 with
  product-owned `user_accounts` / `user_api_keys`, and audit migrations 057-066 for
  every referenced table, column and FK.
- Decide whether the absent 054 is intentional. Gaps are allowed in a greenfield
  sequence, but the manifest/report must not claim a contiguous 001-066 chain.
- Run the actual migrator against empty PostgreSQL 17 twice. Expected count is 66
  records: one Orleans schema plus 65 product files.
- Run `MigrationSchemaTests` and the complete database-enabled solution immediately
  after that same migration, without runtime ORM table creation.

Exit: both migration passes exit 0, the second reports 66 skips, and no product
schema object comes from Sub2API or startup-time CodeFirst.

### G0-02 Repair the canonical/vendor contract release

Status: `TODO`, release blocking.

- Keep `audioTts @12` / `audioStt @13` in the canonical greenfield contract if those
  capabilities remain; update Gateway's vendored schema and digest.
- Regenerate Platform C# from the pinned compiler and remove the hand-maintained
  numeric C++ enum as an independent authority (derive it from generated schema or
  add a compile-time equality gate).
- Checkout the paired Gateway with explicit credentials and pass its path to
  `verify-contracts.sh <gateway>`; the current greenfield job omits the argument and
  therefore exits after Platform-local digest verification.
- Record the paired Platform/Gateway SHAs in one immutable manifest before image
  publication. No compatibility negotiation is needed for old internal revisions.

Exit: byte comparison, both repository digests, generated C# comparison, both builds
and audio dispatch tests pass at the paired refs.

### G0-03 Make test and CI results truthful

Status: `TODO`, release blocking.

- Replace 123 database-test early returns with explicit integration traits/fixtures.
  The greenfield job must fail if PostgreSQL/Garnet prerequisites are absent; local
  unit jobs may report real skips with counts and reasons.
- Remove the duplicate/nonblocking Admin typecheck path (`continue-on-error: true`).
- Register `ISlotLeaseStore` and the production-equivalent scheduler dependencies in
  the benchmark Silo. Require all Scheduler cases to emit valid reports before
  applying threshold checks.
- Make Platform ordinary and release CI invoke the greenfield database job or an
  equivalent required reusable workflow.
- Make cross-repo contract, Web typecheck/build, Playwright, migration double-run,
  smoke and security checks required rather than merely present in separate YAML.
- Do not publish `latest` until all paired release gates pass.
- Delete or gate the independent tag-triggered `docker.yml` publish path. Move every
  image push after clean rebuild and paired verification.
- Delete or gate Gateway's independent `v*` release publisher as well; a passing
  Gateway-local digest/build/test/benchmark is not a paired release.
- Replace `deploy/release.sh`'s hard-coded pass report with captured command results;
  it must run tests, benchmarks, migrations, clean builds and cross-repo byte comparison
  before creating or pushing either repository tag.

Exit: CI summary reports exact tests executed/skipped, database identity, migration
count, benchmark reports, paired commits and image digests; intentional DB/contract/
benchmark/typecheck failures make the top job non-zero.

### G0-04 Repair the runtime verification harness

Status: `TODO`, release blocking.

- Fix stress queries that refer to nonexistent PostgreSQL tables
  (`gateway_usage_outbox`, `reconciliation_incidents`). Gateway's durable usage
  outbox is SQLite and Platform incidents use the product table names.
- Fail when a load/fault child exits early; the current loop logs a dead child but
  can continue without accumulating a failure.
- Remove readiness/admin URL fallbacks that hide failures, and make settlement
  timeout a hard failure.
- Add shell/static contract tests for every SQL query used by smoke/stress scripts.

Exit: a short deterministic fault run first passes, then an injected bad query and
an early-dead load child each fail the top-level command.

### G0-05 Make fresh-stack setup and Gateway readiness truthful

Status: `TODO`, release blocking. Dependencies: G0-01.

- Fail Gateway startup if any per-core listener, dispatch client, Garnet client or
  usage SQLite authority is unusable; `/ready` must cover the mandatory set.
- Choose one ScalaAPI-native first-administrator bootstrap contract: an authenticated
  deployment command or a bounded one-time setup API/UI. Do not copy Sub2API setup
  envelopes, defaults or state.
- Prove dependency failure, concurrent/replayed initialization, default-secret
  rejection, completion lockout and clean empty-volume startup.

Exit: negative dependency/listener probes fail readiness and the one-time bootstrap
creates exactly one authorized initial administrator without reference-system data.

## Phase 1: Close the monetary and Provider core

### P0-01 Revalidate leases, scheduler and settlement on the repaired schema

Dependencies: G0-01, G0-03.

- Run two-Silo slot acquisition/reclaim, account health/cooldown, sticky/rate/fallback
  and provider-quota admission under real contention.
- Exercise pre-forward, forwarded, output-started, completion, outbox-ack and client
  cancellation crashes through HTTP and realtime.
- Persist `output_started` locally before or with first output rather than relying on
  a post-write one-shot RPC. An unacknowledged non-retryable usage report must remain
  durable or create a reconciliation incident, never disappear.
- Prove one terminal debit/release or one durable reconciliation incident per lease,
  with one idempotency identity across retries.
- Finish terminal observed-model propagation for media; remove the source TODO.

Exit: all monetary invariants are queried from PostgreSQL after process replacement;
no test asserts only in-memory Grain state.

### P0-02 Finish the protocol conversion matrix

Dependencies: G0-02, P0-01.

- Preserve tool calls and tool results, multimodal blocks, multiple candidates,
  finish reasons and stable identifiers for supported OpenAI Chat/Responses,
  Anthropic and Gemini conversions.
- Freeze explicit loss/rejection policy for unsupported native-only fields.
- Cover malformed headers/content types, streaming terminal/error events and usage
  extraction for every supported pair.
- Decide whether configurable upstream error exposure/rewrite and monitoring
  suppression is a ScalaAPI feature. If accepted, persist bounded redacted rules and
  apply them consistently; otherwise reject it explicitly rather than inheriting
  reference behavior.
- Replace permissive Provider target handling with a generated bounded contract:
  reject unknown methods, unsafe paths/general headers and unused TLS profile values
  before any outbound I/O.
- Align Gateway HTTP and Platform RPC request-size contracts. Large media should use
  a bounded metadata/object-reference design or a shared explicit limit; never let a
  32 MiB HTTP acceptance become an unexplained 1 MiB RPC disconnect.

Exit: versioned request/response/SSE/error goldens plus source-built cross-provider
groups pass without silently taking only the first text candidate.

### P0-03 Finish provider catalogue, tokenization, pricing and quota authority

Dependencies: P0-01.

- Define versioned provider adapters for model catalogues, token counting, price
  quotes and quota/tier snapshots. Keep Admin prices authoritative when configured.
- Replace `ProviderQuotaRefreshService`'s seeded-row generation bump with real active
  account discovery and bounded adapter calls, fenced across Silos.
- Apply explicit stale/unknown/quota policy before dispatch and audit transitions.
- Stop anonymous model discovery from turning Garnet failure into HTTP 200 with an
  authoritative-looking empty list.
- Cover response-model, long-context, search, audio, realtime, image and video unit
  snapshots without binary floating point.

Exit: source mocks cover success/401/429/5xx/timeout/malformed/stale cases and at
least one controlled live profile per production adapter records headers, versions,
checksums and settlement without secrets.

### P0-04 Complete xAI/Grok as a provider, not a label

Dependencies: P0-02, P0-03.

- Freeze an explicit xAI capability matrix: catalogue, text JSON/SSE, Responses,
  OAuth/API-key lifecycle, account health, quota/tier, image/video, Search/X Search,
  realtime/voice and pricing feature gates.
- Implement only advertised capabilities; return stable product-native unsupported
  errors for the rest. Do not inherit support merely because the wire looks OpenAI.
- Add native Provider mock fixtures and controlled live contract evidence for auth,
  401/revoked, 429/cooldown, malformed, disconnect and terminal usage.

Exit: Admin and scheduler expose truthful capabilities; no generic Bearer route is
described as full native Grok support.

### P0-05 Close the realtime content-policy boundary

Dependencies: P0-01, P0-02.

- Include the bounded initial session request in pre-dispatch evaluation.
- Evaluate later client text frames before Provider delivery and Provider text frames
  before client delivery; define explicit binary/audio policy rather than silently
  bypassing it.
- Share ordinary HTTP query validation and trusted-proxy client identity rules with
  the WebSocket upgrade path. Extend the response-policy matrix beyond the current
  chat-only predicate or explicitly unadvertise uncovered capabilities.
- Preserve one lease and conservative unknown-charge settlement when a response frame
  is blocked or the classifier is unavailable.

Exit: request/response block and fail-closed scenarios have durable redacted audits
and one explainable financial outcome across disconnect and process replacement.

## Phase 2: Specialized APIs and durable media

### P1-01 Web Search and X Search

Dependencies: G0-01, P0-03, P0-04 for xAI routes.

- Repair `search_history` FKs and freeze bounded query/domain/recency/result/source
  contracts plus privacy/redaction policy.
- Implement distinct Web/X adapters, per-query settlement, retry/account-penalty
  semantics and owner-scoped history.
- Make declared Search streaming use the bounded stream/policy/usage path, or remove
  that advertised mode.
- Prove empty results, partial results, 401/429/5xx/timeout/malformed and replay.

### P1-02 TTS, STT and custom voices

Dependencies: G0-01, G0-02, P0-03.

- Repair voice/audio FKs and the contract vendor drift.
- Implement bounded multipart/audio input and output, real object metadata, signed
  access, owner authorization, cancellation, retention and missing-object repair.
- Snapshot character/audio-duration/storage pricing before Provider contact and
  reconcile partial/unknown output conservatively.

### P1-03 Images and video lifecycle

Dependencies: P0-01, P0-03.

- Re-run current sync/async/batch/item/cancel/delete/ZIP/retention paths from an
  empty source-built stack.
- Test object-store partition, partial PUT, committed-response loss, deterministic
  recopy and two-Silo claim fencing while preserving settled billing.
- Add complete video cancellation/delete/restore/provider fault behavior.
- Prove multipart and binary limits at the HTTP/RPC/object-storage boundaries with a
  stable product-native oversize error before lease creation.

Exit for Phase 2: every stored object is owner scoped and traceable to one durable
operation/lease; restart and retention cannot duplicate objects or financial effects.

## Phase 3: Identity, commercial and operations closure

### P2-01 Public identity abuse boundary

- Apply captcha/domain/rate/anti-enumeration policy consistently to registration,
  recovery, verification, OAuth and Passkey entry points.
- Prove refresh replay, multi-device revocation, TOTP backup-code sign-in, real
  WebAuthn ceremony and SMTP delivery/expiry in browser tests.
- Keep token/key material hash-only or encrypted and absent from logs/metrics/audits.

### P2-02 Commercial lifecycle

- Drive checkout -> verified webhook/provider query -> ledger -> subscription through
  one idempotent state machine; cover refunds, replay and exact crash boundaries.
- Close redeem promotion limits, signup referral attribution, anti-abuse, rebate and
  transfer state, targeted announcements and user authorization.
- Production provider credentials and webhook secret rotation require explicit
  deployment profiles; mock success is not production acceptance.

### P2-03 Active/passive monitoring and observability

- Replace process-local monitor leadership with PostgreSQL advisory/fenced claims.
- Replace simulated checks with bounded actual channel/Provider probes and close
  incidents on recovery.
- Replace Passive V2's leader placeholder, prove watermark/dedup/backfill across
  process replacement and enforce privacy defaults.
- Correlate Gateway/Platform/Provider metrics by bounded request/lease IDs and add
  alert delivery/recovery without sensitive labels.
- Fail Gateway startup when any per-core listener or mandatory durable dependency is
  unusable; make readiness cover dispatch, Garnet, usage SQLite and all listeners.

### P2-04 Backup, restore and disaster recovery

- Make the scheduler create encrypted/signed backups under a fenced singleton claim.
- Replace the no-I/O offsite placeholder with real S3-compatible upload, HEAD/readback
  checksum, retry and retention deletion.
- Restore only to an isolated target, run post-restore schema/user/accounting checks,
  inject corruption/target/credential failures and record measured RPO/RTO.
- Exercise rolling forward and rollback using paired Platform/Gateway release refs.

### P2-05 Web applications

- Run Admin and User browser suites against the real source-built backend rather
  than intercepted responses for key mutations.
- Cover authorization, loading/error/retry, refresh expiry/replay, payment, policy,
  monitor, backup, export and public status/legal/accessibility workflows.
- Never expose provider credentials, API-key hashes, reset tokens or backup keys.
- Cover the selected product-native first-run setup flow, including one-time lockout,
  dependency errors and replay, if G0-05 chooses a browser surface.

## Phase 4: Release evidence

### REL-01 Short empty-stack gate

Run migrations twice, database tests, Gateway tests, both Web builds/E2E, contract
generation/comparison, the full deterministic mock matrix, Gateway readiness/target/
evidence negative probes, two Silo/two Gateway replacement and cleanup. Record
commands, exit codes, commits and image digests.

### REL-02 One-hour mixed fault/load gate

Run 3600 seconds of stream/realtime/media/backpressure load while injecting Provider,
Garnet, PostgreSQL, object-storage, TLS and process faults. Required outcomes:

- no duplicate debit, usage event or object;
- no terminal lease with an active hold;
- every unknown outcome has a durable explainable incident;
- bounded connection/outbox/claim backlog and recorded latency samples;
- zero project containers/networks/volumes after cleanup.

### REL-03 Paired immutable release

Publish only after the same manifest pins Platform SHA, Gateway SHA, contract digest,
migration manifest, image digests, test totals/skips and release evidence artifacts.
The release never includes Sub2API refs or data because it is not an upgrade path.
Remove or disable the current Admin self-update endpoint that reports a metadata
lookup as a download; rollout and rollback are external paired-deployment transactions.

## Completion condition

The project may be called complete only when all 65 inventory domains are `verified`
or explicitly accepted out of scope, every required current gate above is green,
and [current-state.md](current-state.md), [feature-gap-report.md](feature-gap-report.md),
[risk-register.md](risk-register.md), [verification.md](verification.md) and the CSV
inventory agree at the same immutable repository pair. A commit message, route,
table, mock, historical log or all-`DONE` checklist is never sufficient by itself.
