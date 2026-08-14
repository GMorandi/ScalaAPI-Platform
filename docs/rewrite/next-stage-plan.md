# ScalaAPI Rewrite Next-Stage Plan

Baseline: Platform `30d82d0`, Gateway `98c62fd`, ScalaAPI pair `032721b`, and
Sub2API research snapshot `origin/main@fbfdcef`; audit date 2026-08-14.

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

Status: `DONE` on Platform `30d82d0`; keep as a release gate.

- The erroneous `users` / `api_keys` references in migrations 055 and 056 were
  replaced with product-owned `user_accounts` / `user_api_keys`; migrations 057-066
  were audited for referenced tables, columns and FKs.
- Decide whether the absent 054 is intentional. Gaps are allowed in a greenfield
  sequence, but the manifest/report must not claim a contiguous 001-066 chain.
- Run the actual migrator against empty PostgreSQL 17 twice. Expected count is 66
  records: one Orleans schema plus 65 product files.
- Run `MigrationSchemaTests` and the complete database-enabled solution immediately
  after that same migration, without runtime ORM table creation.

Evidence: an isolated PostgreSQL 17 run applied 66 records and a second run skipped
66 records; the database-enabled Platform suite then passed 502/502. No product
schema object comes from Sub2API or startup-time CodeFirst.

### G0-02 Repair the canonical/vendor contract release

Status: `DONE` on Platform `30d82d0` / Gateway `98c62fd`; keep as a paired gate.

- Canonical Platform and vendored Gateway schemas now contain the same bytes and
  digests, including `audioTts @12` / `audioStt @13`.
- Checked-in C# output matches Cap'n Proto 1.0.2, and the centralized workflow
  compares both repositories before publishing. No compatibility negotiation is
  needed for old internal revisions.

Evidence: `verify-contracts.sh ../gateway`, generated C# comparison and both local
digest checks pass at the latest component heads. A clean Gateway build remains a
separate acceptance item because the current worktree has uncommitted changes.

### G0-03 Make test and CI results truthful

Status: `DONE` on Platform `30d82d0`; keep as a required CI gate.

- Database-required tests now fail visibly when `GREENFIELD_SCHEMA_CONNECTION` is
  missing; the current negative control reports 113 Host failures instead of a
  false green result.
- Admin typecheck is blocking, the Scheduler Silo registers `ISlotLeaseStore`, and
  all six Platform benchmark cases now emit successful reports.
- Platform component publishing was removed from the old bypass paths. ScalaAPI's
  central workflow owns pair validation, database gates, Web builds and release
  evidence; it publishes only exact superproject tags.

Evidence: the current local commands and centralized workflow fail on missing
database, contract, benchmark or typecheck prerequisites. Full browser and runtime
smoke gates remain separate release work.

### G0-04 Repair the runtime verification harness

Status: `DONE` for corrected SQL and failure propagation on Platform `30d82d0`;
runtime duration gates remain open.

- Stress queries now use `usage_outbox` and
  `accounting_reconciliation_incidents` with the current column names.
- Background child exits and settlement timeouts now set a fatal status, and the
  verifier returns non-zero after a failed verification phase.

Evidence: shell syntax and static ownership checks pass. A full short fault run and
the 3600-second runtime gate are still required before REL-02 can close.

### G0-05 Make fresh-stack setup and Gateway readiness truthful

Status: `DONE` for startup/readiness and first-admin source guards on Platform
`30d82d0` / Gateway `98c62fd`; runtime deployment evidence remains open.

- Gateway startup and `/ready` now include dispatch, Garnet, SQLite and listener
  dependency checks. Platform first-admin bootstrap has one-time locking and
  default-secret rejection guards.
- The setup contract remains ScalaAPI-native; it does not copy Sub2API defaults or
  state.

Exit: negative dependency/listener probes fail readiness and the one-time bootstrap
creates exactly one authorized initial administrator without reference-system data.

## Phase 1: Close the monetary and Provider core

### P0-01 Revalidate leases, scheduler and settlement on the repaired schema

Status: `PARTIAL`; focused database evidence passes, multi-process runtime evidence remains.

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

Status: `PARTIAL`; current Gateway HEAD includes substantial conversion and policy
fixes and its clean source build passes, but cross-component runtime acceptance is
still missing.

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

Status: `PARTIAL`; real quota clients and model-catalogue refresh are present, but
controlled Provider and stale/unknown runtime evidence is missing.

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

Status: `PARTIAL`; xAI quota support is now wired, while the full native capability
matrix and billing/runtime evidence remain open.

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

Status: `PARTIAL`; the clean Gateway HEAD dispatches the initial body and applies
non-chat policy selection, while the current uncommitted frame-policy change does
not compile and later-frame runtime evidence is absent.

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

Status: `PARTIAL`; greenfield schema/history and mock contracts pass, but real Web/X
adapters, streaming and settlement evidence are not complete.

Dependencies: G0-01, P0-03, P0-04 for xAI routes.

- Keep the repaired `search_history` FKs in the empty-schema gate and freeze bounded
  query/domain/recency/result/source contracts plus privacy/redaction policy.
- Implement distinct Web/X adapters, per-query settlement, retry/account-penalty
  semantics and owner-scoped history.
- Make declared Search streaming use the bounded stream/policy/usage path, or remove
  that advertised mode.
- Prove empty results, partial results, 401/429/5xx/timeout/malformed and replay.

### P1-02 TTS, STT and custom voices

Status: `PARTIAL`; migrations, contract bytes, stores and mock routes pass, while
provider audio/object/ownership E2E remains open.

Dependencies: G0-01, G0-02, P0-03.

- Keep the repaired voice/audio FKs and canonical/vendor contract in the paired
  empty-schema and byte-equality gates.
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

Status: `PARTIAL`; PostgreSQL leadership, real channel probes and passive watermark
logic are implemented, but multi-process outage/recovery evidence is pending.

- Exercise the implemented PostgreSQL advisory-lock leadership under concurrent
  Platform processes and prove one active owner, handoff and fencing after failure.
- Exercise the bounded HTTP channel probes against success, timeout, malformed and
  recovery targets, including incident open/close behavior.
- Prove Passive V2 watermark/dedup/backfill across process replacement and enforce
  privacy defaults under the implemented distributed leadership path.
- Correlate Gateway/Platform/Provider metrics by bounded request/lease IDs and add
  alert delivery/recovery without sensitive labels.
- Fail Gateway startup when any per-core listener or mandatory durable dependency is
  unusable; make readiness cover dispatch, Garnet, usage SQLite and all listeners.

### P2-04 Backup, restore and disaster recovery

Status: `PARTIAL`; scheduled creation and offsite upload code now exists, but
corruption, restore and measured RPO/RTO drills are pending.

- Exercise scheduled encrypted/signed backup creation under concurrent workers and
  prove the singleton claim cannot create duplicate artifacts.
- Extend the implemented S3-compatible PUT with HEAD/readback checksum, retry and
  remote retention deletion, then prove it against an isolated object store.
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
Generate test totals/skips by parsing the uploaded Platform TRX and explicit
Gateway/Web result artifacts; do not synthesize `status: passed` or `skipped: []`
from job dependency names alone.
Remove or disable the current Admin self-update endpoint that reports a metadata
lookup as a download; rollout and rollback are external paired-deployment transactions.

## Completion condition

The project may be called complete only when all 65 inventory domains are `verified`
or explicitly accepted out of scope, every required current gate above is green,
and [current-state.md](current-state.md), [feature-gap-report.md](feature-gap-report.md),
[risk-register.md](risk-register.md), [verification.md](verification.md) and the CSV
inventory agree at the same immutable repository pair. A commit message, route,
table, mock, historical log or all-`DONE` checklist is never sufficient by itself.
