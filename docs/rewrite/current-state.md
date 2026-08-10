# ScalaAPI Rewrite Current State

This document is the active baseline for the new ScalaAPI product as of
2026-08-10. ScalaAPI reimplements the useful product capabilities catalogued in
Sub2API, but it does not preserve Sub2API APIs, internal contracts, schemas, IDs,
keys, state mappings, deployment processes, or data. The Sub2API repository is a
read-only requirements reference and is excluded from builds and runtime.

## Source snapshot

| Repository | Commit | Worktree | Active role |
| --- | --- | --- | --- |
| `gateway` | `418da3a` | clean | C++ Gateway edge, protocol routing/conversion, Provider transport, and Garnet client |
| `platform` | `1cc4538` | clean | C# Orleans control plane, PostgreSQL authority, Provider mock, Admin Web, and User Web |
| `sub2api` | `43ec48d` | read-only clean | Requirements reference only; no runtime or compatibility dependency |

## Historical role descriptions

| Repository | Commit | Worktree | Role |
| --- | --- | --- | --- |
| `gateway` | `d1e4a85` | clean | C++ HTTP/WebSocket edge, protocol parsing/conversion, versioned OpenAI Chat/Responses, Anthropic Messages, and Gemini request/response/SSE golden contracts, full pairwise provider request/response/error matrix assertions, fail-closed model catalog and token-count validation, non-billable control-request hold release, explicitly bounded Responses read/input_items/cancel/delete subresource routing and release, bounded Embeddings and Responses validation, streaming, strict Provider media contracts, bounded transport timers, normalized Provider availability errors, shared retryable Platform transport policy, durable usage delivery that does not let one retryable outbox row starve later leases, authenticated Garnet projections, bounded request/response content-policy RPC evaluation, event-boundary streaming response moderation, and fail-closed response delivery including retryable classifier outages |
| `platform` | `eecaff6` backend + Admin Web + User Web | clean | C# Orleans control plane, PostgreSQL accounting/product authority and reconciliation, Provider mock contracts including source-owned malformed, cancellation, and input-item OpenAI Responses fixtures, OpenAI Responses JSON/SSE plus idempotent read/input_items/cancel/delete subresource runtime smoke coverage, four non-billable control leases with no usage/debit evidence, malformed-success 502/unknown-charge retention smoke coverage, seeded Anthropic/Gemini provider-group smoke coverage, Claude price aliasing, control-operation concurrency accounting, terminal lease-slot release, bounded provider pricing catalog refresh with immutable source/checksum history and admin-source precedence, media object HEAD reconciliation with retryable missing/mismatch state, native mock and Stripe Checkout Session payment adapters with HTTPS/auth/amount bounds and idempotent pending-order retry, Stripe raw-body webhook verification and event normalization with provider payment-id association, native partial-refund Provider commands with pending/retryable state, cumulative order refund state, independent Provider/ledger refund effects, SKIP LOCKED refund recovery with expiring claims, and one NUMERIC ledger effect per refund, User Web provider selection and checkout links, public model catalog/status/legal routes with source-built Compose browser evidence, authenticated portal browser evidence for login/dashboard/usage/API keys/profile, rotating identity/session/TOTP/OAuth state, native Passkey/WebAuthn ceremonies, encrypted email notification outbox and retry worker, API-key policy and audit, versioned runtime configuration, persistent scheduling and lease/hold/ledger state, atomic subscription quota reservation and settlement, idempotent subscription expiry/renewal worker, audited operator reconciliation, Admin Web incident filtering/run/evidence-backed settle-release workflow with replay-key preservation, content-policy rule CRUD/change/alert operations with an API-intercepted Playwright smoke, authenticated Operations metric/alert dashboard, bounded Channel Monitor history and health-check submission with browser evidence, source-built OpenAI Moderation empty-stack smoke coverage, atomic audited referral rewards, authenticated and audited operational metrics, bounded redacted audit queries/exports, encrypted proxy and validated TLS profile administration, audited bounded channel monitor checks, auditable user data export, bounded authentication/ceremony cleanup, user announcement read tracking, media/object lifecycle, staged request/response content-policy evaluation with versioned Unicode normalization, explicitly selectable source-owned/OpenAI Moderation classifiers, fixed-label OpenAI moderation counters, persisted cross-process snapshots, configuration-backed p95/unavailable-ratio budget gauges, durable budget alerts with rolling-window recovery, deterministic hosted-worker and multi-process/restart evidence, cross-Gateway shared-idempotency exactly-once settlement evidence, controlled secondary Silo/Gateway outage and rejoin settlement evidence, idempotent PostgreSQL backup artifacts with SHA-256 and isolated restore target, bilingual Admin backup/restore controls, isolated Provider fault fixtures with deterministic empty-stream EOF, durable no-TTL policy revision propagation and PostgreSQL-backed Garnet rebuild, Garnet TLS CA trust-anchor and server-name validation, source-built Garnet TLS server override, rotation/expiry rejection and recovery, four-session realtime WebSocket soak with bounded connection hold and exactly-once billing assertions, operational alert evidence, redacted audits, and crash-reclaimable content-policy outbox propagation |
| `sub2api` | `43ec48d` | read-only clean | Requirements catalogue only; never a runtime or compatibility dependency |

The active source snapshot for this document is Platform/Admin Web/User Web
`1cc4538` and Gateway `418da3a`; both worktrees are clean. The table's longer
capability descriptions are retained as inventory context, while this override
and the evidence below define the current commits.

The current tracked inventory is:

- Gateway: 52 production C++ source/header files, 11 test source files, and 126
  CTest cases.
- Platform: 121 hand-written production C# files, 5 generated Cap'n Proto/global C# files, 67 test/benchmark C# files, and 259 passing tests: 69 Grain, 83 Host, 46 Admin, and 61 Provider mock tests.
  The Admin Web source now has 29 TypeScript/TSX files and 16 page views.
- Product surface: 129 direct Admin API route declarations, 50 product tables,
  22 SQLSugar entity types, 29 Admin Web TypeScript/TSX files and 16 page views,
  plus 21 User Web TypeScript/TSX files and 14 user views.
- Reference scope: approximately 612 Sub2API route registrations, 39 concrete
  Ent schemas, 82 Vue view/component files, and 240 migrations. These are scope
  signals, not parity percentages or migration targets.

The 58-domain inventory is 2 `implemented`, 55 `partial`, 1 `skeleton`,
and 0 `missing`. A route, table, mock response, or manual probe does not promote a
domain; promotion requires a defined contract/state machine, automated tests, and
current-source runtime evidence.

## Architecture now implemented

- Gateway and Platform are independent repositories joined by one revision-3
  Cap'n Proto contract. Platform owns the canonical schema; Gateway vendors an
  identical copy. Digest and deterministic C# generation gates reject drift.
- PostgreSQL is authoritative for product and accounting state. Orleans
  coordinates aggregate concurrency. `entity_registry`, rather than Orleans
  storage internals, is used for business discovery and administrative listing.
- Each user has one `accounting_accounts` row containing an authoritative NUMERIC
  posted balance and monotonically increasing ledger version. Every current
  money-mutating path uses the same per-user transaction lock and append rule;
  Orleans holds only a versioned projection and cannot authorize spending.
- Garnet is the only distributed cache/projection service. Both products use
  authenticated external TCP clients with optional TLS and no embedded RESP
  server, Microsoft.Garnet package, Redis process, image, or fallback. The
  Platform client can load a configured PEM CA trust anchor, keeps the configured
  TLS server name as the identity check, and fails closed on name mismatch or
  unavailable certificates; the source Compose stack passes the setting through
  as `Garnet__CaCertificatePath` for a deployment-specific read-only CA mount.
  `deploy/stack/docker-compose.tls.yml` now supplies server-side Garnet TLS
  flags, a password-protected PFX mount, and the same CA mount to Platform and
  Gateway. `deploy/stack/garnet_tls_smoke.sh` generates a short-lived CA/server
  chain and runs the complete source smoke through Platform's authenticated TLS
  readiness path. The default development stack remains plaintext unless this
  override is selected. The wrapper also rotates the mounted PFX through Garnet's
  refresh period, rejects wrong-name and expired certificates through Platform
  readiness, restores a valid bundle, and proves a new billable request. Partitioned
  multi-process convergence remains a separate release gate.
- API keys carry a normalized capability scope set and an optional millisecond
  expiry in the Orleans authority. Platform rejects an unauthorized capability
  before scheduling or creating a balance hold. Admin and user create/update/
  rotate/revoke paths persist the same policy projection and append actor-scoped
  audit events; denied capability requests are recorded with a request ID and no
  plaintext key material.
- Gateway transmits bounded request and response bodies in the revision-3 dispatch
  contract. Platform evaluates active, scope-aware `log`/`block` rules after
  authentication and capability authorization but before scheduling, lease creation,
  or Provider contact. Response rules run after Provider validation and before
  successful output is delivered. Non-stream blocks return HTTP 400 and preserve
  one normal usage settlement for exact replay. Streaming Gateway delivery buffers
  one bounded SSE event at a time, evaluates it before client write, and emits a
  protocol-shaped terminal policy error on block/fail-closed outcomes; a blocked
  stream retains its unknown-charge hold/idempotency evidence. Matches are durably
  audited with rule identity, evaluator version, classifier, policy revision, and
  optional redacted snippets. The native `unicode-confusable-v1` evaluator applies
  NFKC, case folding, format-character removal, and a bounded confusable map. Local
  matching is deterministic; the configured external classifier uses the bounded
  source-owned HTTP adapter contract and fails closed with retryable 503 semantics
  on transport, timeout, or 429/5xx outcomes. The optional `openai` classifier
  uses the official HTTPS Moderations endpoint with a bounded Bearer credential,
  configured moderation model, 129 KiB input and 16 KiB response limits, and a
  single `results[].flagged` decision; malformed, unauthorized, oversized, or
  unknown responses fail closed as protocol errors. The OpenAI adapter does not
  send policy patterns upstream: an `openai` rule is a global flagged-content
  decision, while local/source-owned rules retain pattern matching.
  Policy mutations are recorded in a PostgreSQL outbox, propagated to Garnet with
  expiring claims and retry evidence, and policy blocks/classifier outages create
  deterministic alert rows queryable by Admin. A Host test now runs two concurrent
  propagation workers against one PostgreSQL outbox and proves each revision is
  claimed and published once. Revision writes no longer expire during idle periods;
  the authenticated cache rebuild restores the PostgreSQL revision and increments
  invalidation after Garnet key loss. A dedicated RemoteGarnet test deletes both
  keys and reads back the rebuilt values. `OpenAiModerationMetrics` now exports
  fixed-label request/outcome counters and a bounded latency histogram without
  content, rule, endpoint, or credential labels. Migration 044 persists
  instance/sequence-idempotent snapshots, a hosted worker flushes retryable
  deltas, and `/metrics` merges PostgreSQL totals with the live process to expose
  unavailable ratio, bucketed p95, and configuration-backed breach gauges.
  Migration 045 updates durable unavailable-ratio/p95 alert state in the same
  transaction as snapshot append. Windowed recovery re-evaluates even without new
  requests and closes expired anomalies. Runtime multi-process flush/restart,
  credential rotation, and browser workflows remain open.
- S3-compatible storage owns media bytes. PostgreSQL owns media metadata,
  authorization, object keys, ETags, sizes, and lifecycle state.
- All business money is `decimal`; PostgreSQL uses `NUMERIC`; the RPC boundary
  uses signed integers at a documented 1e8 fixed scale. No billing path relies on
  binary floating point.

## Product capabilities now present

### Gateway and Provider transport

- Capability-driven routes cover OpenAI Chat/Responses/Embeddings/Images/video,
  Anthropic Messages and token counting, Gemini generation, model discovery,
  realtime WebSocket entry points, and approved media subresources.
- OpenAI, Anthropic, and Gemini JSON/SSE normalization and cross-protocol
  conversion exist, including OpenAI, Gemini, and nested Anthropic streaming
  usage extraction.
- Non-stream Provider requests have a 30-second boundary and bounded retries.
  Streaming Provider calls now bound the response-header wait by the first-token
  deadline and extend the incoming client socket for the configured stream window;
  separate inter-chunk and total-stream timers are independently enforced and
  unit-tested. Failover retains the
  public idempotency key while allocating a unique internal request/lease ID per
  attempt.
- Gateway durably changes a lease from `held` to `forwarded` before opening HTTP or
  realtime Provider transport. The first response bytes successfully written to a
  streaming client record `output_started`. If evidence persistence fails before
  transport, Gateway fails closed without contacting the Provider.
- Only an actual Provider response with a 4xx/5xx status proves an explicit
  no-charge rejection and permits release/failover. Transport loss is exposed as
  `503/provider_unavailable`; bounded timeouts, malformed payloads, conversion
  failure, and media persistence failure use `502/provider_protocol_error` or a
  more specific 502 contract. All remain unknown-charge outcomes and do not fail
  over.
- Successful payload-bearing 2xx responses are checked before usage extraction.
  A Provider connection reset before headers, during a non-stream body, or during
  an incomplete SSE is `503/provider_unavailable`; bounded timeout and malformed
  protocol cases remain `502/provider_protocol_error`. An upstream disconnect can
  no longer escape as a zero-token 200 response. Streaming requests reject a
  non-SSE success before client output.
  SSE completion now requires the source protocol's terminal event (`[DONE]`,
  `message_stop`, `response.completed`, or a finish reason); EOF/timeout before
  that event is an incomplete Provider stream and retains the charge hold. A
  client write returning zero or an error is treated as cancellation, never
  retried, and records a bounded disconnect/cancellation reason for usage
  evidence.
- Gateway's local usage outbox survives restart, replays retryable reports, and
  retires non-retryable terminal reports instead of blocking forever.
- Gateway and Platform expose opt-in, one-shot deterministic fault hooks at
  dispatch, Provider completion, settlement commit, outbox claim, and outbox
  acknowledgement. Hooks persist a claim marker so a restarted process does not
  crash repeatedly; the smoke harness proves Platform termination after creating
  an unforwarded lease (safe held-lease expiry), Platform termination after an
  outbox claim (claim reclaim with exactly-once Grain effects), Platform
  pre-commit/post-commit/pre-ack recovery, and Gateway termination before Provider
  dispatch (safe expiry) or after Provider completion (ambiguous lease retained
  for reconciliation).
- Platform dispatch responses distinguish transient transport/protocol loss with
  the dedicated `platformUnavailable` reject code. Gateway retries that code with
  bounded backoff under the existing dispatch deadline, preserving the original
  request and public idempotency identity. Platform looks up the durable request
  lease and rebuilds its upstream target after restart, so a lost response resumes
  the existing active lease instead of creating a second lease or charge.
- The same retry policy is used before opening a realtime WebSocket: transient
  Platform loss receives bounded backoff under the dispatch deadline, while the
  first client event, request ID, and public idempotency key remain unchanged.

### Identity and control plane

- Registration, password login, OAuth identity records, rotating hashed refresh
  sessions, logout, per-session revoke, password reset, email verification,
  profile/password update, and password-confirmed soft account deletion exist.
  Auth-session tests prove PostgreSQL row locking permits only one concurrent
  refresh winner, the old token cannot replay, replaced and logged-out access
  tokens fail session validation, and expired sessions reject both access and
  refresh paths; the source Compose gate covers the same HTTP lifecycle.
- Registration and login normalize and bound public input, return deterministic
  400/401/409 responses for invalid or concurrent duplicate requests, and use
  hash-only PostgreSQL `auth_abuse_counters` rows for cross-instance login
  identity/IP and registration-IP limits. Five failed logins produce a
  15-minute identity lock (with a higher shared-IP ceiling), ten invalid
  registrations produce an hourly IP lock, successful auth clears counters, and
  429 responses include `Retry-After`. Real PostgreSQL Admin tests and the
  current empty-stack smoke prove the policy; browser, notification, and broader
  public-endpoint limits remain open.
- Password-reset and email-verification requests now enqueue only AES-GCM protected
  token material in `email_delivery_outbox` migration 034. A bounded hosted worker
  claims messages, delivers through configurable SMTP or an explicit filesystem
  capture provider, retries transient failures with backoff, marks terminal results,
  and cancels pending messages superseded by a newer token. User Web action links
  hydrate the confirmation forms. Real PostgreSQL tests prove ciphertext redaction,
  one-shot delivery, retry recovery, and stale-token suppression; live SMTP/provider,
  browser mail flow, delivery metrics, and broader abuse limits remain open.
- API keys support create, list, rotate, revoke, hash-only persistence, registry
  projection, absolute quota, and independent 5-hour/day/week spend windows.
- Subscription plans and user subscriptions now carry NUMERIC granted, used, and
  reserved quota. Migration 035 binds a request lease to the active subscription
  selected at dispatch; `RequestLeaseStore` locks that row before reserving the
  maximum hold, rejects concurrent over-allocation with `quotaExhausted`, and
  releases or consumes the reservation atomically with normal settlement, no-charge
  abort, safe expiry, and operator release. User subscription responses and Billing/
  Dashboard expose reserved and remaining quota. A zero quota grant is finite (it
  rejects any positive reservation), while no active subscription leaves account
  balance billing unchanged; provider payment coupling and browser evidence remain
  open. Platform `e05ed40` adds a hosted lifecycle worker: internal
  auto-renewals reset grants only after reservations drain, no-auto-renew rows expire,
  stale `expired` auto-renew rows recover, and held rows wait in `past_due`; each
  transition is paired with a deterministic subscription event and concurrent workers
  process a row once.
- TOTP setup, verification, login backup-code use, and disable flows now share a
  PostgreSQL-backed state machine. Five failures in a 15-minute window lock the
  account, accepted TOTP time steps cannot be replayed, backup codes are consumed
  atomically, and disable never accepts a backup code. Real PostgreSQL Admin tests
  cover cross-instance lockout, recovery, replay, one-time backup use, and atomic
  disable behavior.
- Passkey/WebAuthn registration and authentication use Fido2NetLib with a native
  PostgreSQL ceremony table. Challenges are bounded, five-minute, flow-scoped, and
  atomically one-shot; credential public keys, user handles, and signature counters
  are stored without private key material. Registration and revocation write actor/IP
  audit rows in the same transaction, and authentication advances counters
  monotonically before issuing the normal rotating session. A real empty-schema test
  covers challenge replay, credential lifecycle, counter monotonicity, and audit
  cleanup. User Web now converts Fido2 options and responses for registration,
  revocation, and login; browser ceremony, anti-enumeration, and abuse limiting
  remain open.
- OAuth start and callback flows issue S256 PKCE material and persist only hashed
  state/verifier values. Callback consumption is bound to the normalized provider
  and exact redirect URI, serialized by PostgreSQL, expires after ten minutes, and
  cannot be replayed. Real Admin tests cover provider/redirect/verifier mismatch,
  one-time consumption, persisted consumed state, and expiry. The source-owned
  Provider mock now issues one-time authorization codes, validates client,
  redirect, and S256 verifier bindings, and serves GitHub-shaped identity data;
  the empty-stack gate proves start -> authorize -> callback account binding and
  replay rejection. Account-link collision policy, production redirect allowlists,
  and browser tests remain open.
- The User Web is a separate Solid client with registration/login, OAuth callback,
  refresh-aware sessions, password-reset request/confirmation, balance and recent
  usage overview, scoped usage history, API key create/rotate/revoke, payment
  orders, subscriptions, profile editing, password change, and an authenticated
  TOTP setup/verify/disable page with one-time backup-code display. Email
  verification has a request/confirmation page linked from unverified profiles.
  Billing now reads the active plan catalogue, purchases, cancels or renews a
  subscription, redeems promotion codes, generates/displays a referral code, and
  provides Passkey registration, revocation, and sign-in controls. It is served
  independently from Admin Web and uses only the new `/auth` and `/user` contracts;
  backup-code sign-in UX, real payment checkout, referral reward settlement, and
  browser automation remain open.
- Users, groups, Provider accounts, encrypted credentials, scheduling, sticky
  routing, rate/concurrency policy, and versioned pricing are represented as
  Orleans aggregates with PostgreSQL operational records where implemented.
  Group scheduling now persists RPM window counters, chooses exact model routes
  before longest-prefix and wildcard routes, applies overnight peak multipliers,
  and follows multi-level fallback chains with cycle protection. Sticky bindings,
  account load ordering, capability filtering, and user/account concurrency remain
  Orleans-owned; multi-silo contention and HTTP group policy validation remain open.
  OAuth Provider accounts persist encrypted access/refresh/client secrets with an
  expiry, version, refresh lease, compare-and-set completion, bounded failure code,
  and scheduler backoff. Dispatch recovery and media polling resolve credentials
  through one refresh service before contacting the Provider. Its generic token
  adapter requires HTTPS by default, bounds responses, validates header material,
  and never includes Provider response bodies in errors. Admin reads expose only
  non-secret refresh health; the account form supports explicit static-header and
  OAuth modes while retaining encrypted secrets on metadata-only edits. Every
  acquired refresh attempt appends a non-secret PostgreSQL audit row with source,
  version transition, bounded outcome code, endpoint host, and duration; Admin can
  page and filter that history by account, source, and outcome.
- Admin can publish/close effective price versions. New leases snapshot version
  identity and every NUMERIC unit rate, so mutable configuration cannot reprice an
  existing request.
- Platform `d71fe8b` adds migration 036 price-source metadata and a bounded provider
  pricing catalog adapter. HTTPS is required by default, provider credentials are
  sent only as authorization headers, malformed/duplicate/oversized/non-decimal
  quotes are rejected, and a canonical checksum gives each model an immutable
  source version. Changed snapshots close only the previous open version for the
  same provider/model; identical snapshots replay without new rows. The hosted
  refresh worker is configured for the source-owned Provider Mock in the development
  stack. Provider-specific pricing rules, tokenizer authority, and multi-provider
  runtime evidence remain open.
- Runtime configuration is persisted in the `system` ConfigGrain with bounded
  keys/values, explicit rejection of secrets and connection strings, boolean-only
  `feature.*` flags, independent snapshots, and optimistic version checks. Admin
  updates return the new version and append actor/IP `config.update` audit rows;
  dynamic consumers and browser controls remain open.

### Billing and idempotency

- Lease creation checks the SQL-authoritative posted balance minus active holds,
  then transactionally creates a `held` lease, durable balance hold, idempotency
  record, and immutable lease event. Its strict state machine is `held -> forwarded
  -> output_started -> completed`, with terminal `aborted`, `expired`, or
  `reconciliation_needed` branches. Completion transactionally records usage, a
  unique versioned `usage_debit`, terminal lease/hold state, and outbox records.
  Explicit no-charge abort releases idempotently; unknown abort preserves the hold,
  blocks redispatch, and emits reconciliation evidence. TTL releases only a
  never-forwarded `held` lease. Expired `forwarded` or `output_started` work enters
  `reconciliation_needed` and still accepts one late usage completion.
- Completed non-stream requests persist a bounded response for exact replay.
  Matching settled requests return the stored response without a second lease or
  debit; active duplicates and fingerprint conflicts are deterministic.
- Settlement outbox claims expire and can be reclaimed after process failure.
  Financial effects use stable IDs and bounded retry rather than silent loss.
- User create/configuration contracts cannot set balance. Administrative credits,
  payment credits/refunds, redeem bonuses, and usage debits append through one
  `AccountingStore`; stable effect identity provides exact replay/conflict
  semantics and every accepted effect advances one per-user ledger version.
  Administrative debits additionally reject active-hold overdraft and persist an
  actor/reason audit. A latest-snapshot SQL outbox retries Orleans projection;
  stale versions cannot overwrite a newer Grain balance.
- A globally serialized scheduled reconciler checks account balance/version and
  ledger contiguity, usage/debit equality, lease/hold terminal state, and Grain
  projection state. It repairs only provably safe terminal holds and stale
  projections; unsafe mismatches and unknown charges persist as incidents. Admin
  exposes run/incident queries and Platform exposes open-count, unknown-charge,
  oldest-age, and last-success metrics.

### Provider mock, media, and commercial foundations

- The source-owned Provider mock implements deterministic OpenAI, Anthropic, and
  Gemini JSON/SSE paths, models, embeddings, token count, sync media, pollable
  image/video tasks, and faults for 429, 500, timeout, disconnect, malformed usage,
  invalid stream content, downstream client cancellation, and a truncated stream
  that emits usage before EOF. Its Responses endpoint also accepts a WebSocket
  realtime session, emits deterministic `session.created` and `response.done`
  usage frames, and waits for the Gateway to close the session. The mock also
  exposes a deterministic form-based OAuth refresh endpoint with versioned token
  rotation, revoked/timeout/malformed/oversized profiles, and stale access-token
  rejection so credential refresh is tested over real HTTP.
- Its OpenAI Embeddings endpoint returns one deterministic vector per input,
  honors bounded `dimensions` and `encoding_format` (`float` or `base64`), reports
  input usage, and can emit 429/500/malformed/shape-invalid responses for billing
  and reconciliation tests.
- Its model endpoints return deterministic OpenAI list metadata and Gemini model
  limits/methods; Anthropic token counting returns a deterministic positive
  `input_tokens` value. Catalog duplicate/malformed and token-count malformed or
  zero profiles are available for fail-closed contract tests.
- Normalized OpenAI Chat input can select a fault without private headers. A
  protected seed endpoint creates nine independent fault accounts/groups so one
  scheduler cooldown cannot mask another scenario; account credentials also pin
  the mock scenario header so the fault matrix is independent of request-body
  conversion.
- Media polling copies Provider bytes to S3-compatible storage and persists object
  ownership metadata. Signed downloads, output deletion, and terminal operation
  deletion work. Platform `44d2096` adds migration 037 and a metadata-only HEAD
  reconciler: missing, size-mismatched, ETag-mismatched, and transient failures
  become retryable `object_status=failed` rows without changing the settled media
  operation or lease, and a later valid HEAD returns the row to `stored`. Platform
  `7d0abc6` now calls the Provider image task/batch cancellation endpoint before
  marking a durable operation cancelled; repeated cancellation is terminally
  idempotent and retains the unknown-charge hold for reconciliation. Object
  listing/orphan cleanup, restore, retention, and full cancellation/restart
  coverage remain open.
- Signed payment webhooks, order paid/refunded transitions, stable ledger effects,
  pending-event recovery, subscription purchase/cancel/renew/expiry, and
  transactional redeem-code effects exist as partial commercial foundations.
- Platform `d8788dd` extends the native payment checkout/refund boundary: migrations 038-042
  and 039
  persists a bounded checkout URL; `/user/payments/create` routes `mock` and
  `stripe`, normalizes amount/currency, enforces idempotency payload conflicts,
  persists a pending order before the provider call, retries pending orders, and
  returns a provider order ID plus checkout URL. The mock adapter uses bounded
  Bearer JSON; the Stripe adapter uses bounded HTTPS Basic-auth form requests,
  minor-unit amounts, order metadata, strict Checkout Session response parsing,
  and fail-closed redirect/secret handling. User Web exposes provider selection and
  checkout links. Stripe webhook signatures now use the raw body with a bounded
  timestamp tolerance; checkout success, payment-intent success, and full
  charge-refund events normalize to the same idempotent ledger path, with 039
  persisting the provider payment ID. Migration 040 adds an administrator full-
  refund command: `payment_refunds` is created before Provider contact, ambiguous
  timeout/unavailable outcomes remain retryable under the same command key, Stripe
  refunds use the payment-intent minor-unit contract, the deterministic Provider
  mock exposes the same endpoint, and successful settlement appends one audited
  NUMERIC `payment_refund` effect. Migration 041 adds actor-bound attempts, expiring
  claims, and a hosted `SKIP LOCKED` recovery worker that retries pending or
  ambiguous rows with the original Provider idempotency key; expired claims are
  safely reclaimable. Migration 042 adds the NUMERIC `refunded_amount` order
  accumulator and keeps only one pending/reconciliation command active per order;
  each successful refund has an independent Provider refund and ledger effect ID,
  and orders move through `partially_refunded` before `refunded`. Stripe
  `refund.created` webhooks apply an incremental amount while `charge.refunded`
  treats `amount_refunded` as cumulative, both using the same accumulation rule.
  Additional production adapters, browser payment completion, and exact-boundary
  crash evidence remain open.
- Platform `6344f88` replaces the unaudited referral record mutation with an
  authenticated Admin reward command. It takes a deterministic lock on both
  users, requires an owned referral code, enforces one referrer/referred pair,
  credits the referrer through the NUMERIC `AccountingStore`, updates referral
  counters, and writes actor/IP audit evidence in one transaction. Exact command
  replay and changed-payload conflict are covered by a real PostgreSQL test;
  signup attribution, anti-abuse policy, and browser evidence remain open.
- Platform `9848427` replaces the raw operational-metric insert with an
  authenticated `OpsMetricsStore` command. Metric names and labels are bounded,
  the metric and actor/IP audit row commit together, and Admin exposes bounded
  summaries plus filtered content-policy alert evidence. An empty-schema
  PostgreSQL test covers invalid input, latest/average/sample aggregation, alert
  filtering, and audit cardinality; collector rules, dashboards, traces, and
  alert delivery remain open.
- Platform `becf189` routes Admin audit reads through a bounded `AuditLogStore`,
  adds a 1,000-row export cap, recursively redacts token/secret/password/
  authorization/key fields in JSON details, and removes the generic client
  audit-insert endpoint. A real PostgreSQL test covers redaction and bounds;
  retention, immutable storage controls, browser authorization, and security
  scanning remain open.
- Platform `db770e2` replaces raw proxy/TLS mutations with an encrypted and
  audited `NetworkProfileStore`. Proxy passwords use the configured AES-GCM
  master key and never appear in list responses; proxy type/host/port/status and
  TLS JA3/JA4/cipher inputs are bounded, probe failures are generic, and an
  empty-schema PostgreSQL test covers ciphertext, password retention/clear,
  TLS validation, and audit cardinality. Provider-specific outbound adapters,
  TLS handshake enforcement, and browser/security evidence remain open.
- Platform `326fc43` replaces raw channel-monitor inserts with an authenticated
  `ChannelMonitorStore`: checks require an active account, bound status/latency/
  error fields, and one transactionally paired actor/IP audit row. Real
  PostgreSQL coverage proves valid, invalid, missing-account, bounded listing,
  and audit behavior; scheduled runners, templates, history, and feedback
  notifications remain open.
- Platform `80ab783` adds `MaintenanceStore`: `/user/export` returns a bounded
  repeatable-read snapshot without password, refresh-token, or API-key hashes, and
  `/admin/maintenance/cleanup` removes only expired authentication/ceremony records
  under a retention/row limit. Cleanup supports dry-run, actor-scoped idempotency
  replay/conflict, and transactionally paired audit evidence. Empty-schema PostgreSQL
  coverage proves export redaction and deletion; immutable retention policy,
  scheduled execution, object/media cleanup, and browser export remain open.
- Platform `acb1c66` adds migration `033-announcement-reads.sql` and a user-scoped
  `AnnouncementStore`. Published, unexpired announcements are listed with read
  state; the first read persists one row and one audit event, while duplicate reads
  replay the same timestamp without another audit. User Web renders unread items on
  the Dashboard and marks them through the authenticated endpoint. Real empty-schema
  PostgreSQL coverage proves migration idempotency, read-state persistence, duplicate
  replay, and audit cardinality; targeting/scheduling and browser authorization remain
  open.

### Bootstrap and deployment

- The direct source migrator applies product migrations 001-046 plus the Orleans
  baseline to an empty PostgreSQL 17 database; the Compose gate therefore expects
  47 records (46 product migrations plus the image-owned Orleans baseline).
  Migration 043 makes `openai` an explicit allowed classifier for policy rules and
  migration 044 adds cross-process classifier metric snapshots; migration 045 adds
  budget alert state; migration 046 adds idempotent PostgreSQL backup jobs and
  isolated restore runs. The `scalaapi-backup-0810b` source smoke applied all 46
  product records, replay-skipped them, created a non-empty SHA-256-verified
  artifact, restored it to `platform_restore`, and replayed both commands without
  duplicate rows.
  audit rows. The source smoke derives this count from the checked-in migration
  directory plus the image-owned Orleans schema, so a new forward migration cannot
  leave the gate stale. No source database, snapshot, old key, CDC table, or
  compatibility mapping is required.
- `deploy/stack` independently starts PostgreSQL, authenticated Garnet, MinIO,
  Provider mock, Platform, Gateway, Admin API, Admin Web, and User Web. Image digests pin the
  infrastructure services.
- The stack uses `restart: on-failure`; the Podman Compose 1.6 smoke harness also
  explicitly starts the exited Platform container when a fault hook is enabled.
  `Orleans:SingleSiloRecovery` is an explicit development-only mode which retires
  stale membership rows before a replacement silo joins; multi-silo defaults remain
  unchanged.
- CDC consumers, Debezium configuration, migration fences/write gates, cutover
  endpoints, and CDC-only product tables are absent from active runtime code.
  `docs/migration/README.md` is only a pointer to historical material under
  `docs/archive/migration`.

## Current verification evidence

## Embeddings profile implementation evidence

The read-only Sub2API reference implements the OpenAI-compatible Embeddings
surface in `backend/internal/service/openai_embeddings.go`: it resolves the
requested model against an account mapping, forwards the normalized
`/v1/embeddings` request, preserves the upstream response, and extracts input,
output, cache, and image-token usage for billing. Its tests also exercise a
Jina-style mapped model and provider-specific upstream URL construction.

ScalaAPI now has the source-owned provider profile contract: Gateway validates
input count, requested dimensions, float/base64 shape, finite values, ordered
indexes, positive usage, and the Jina/Gemini profile ceilings before lease
creation. Platform forwards `/v1/embeddings`; the Provider mock publishes
OpenAI-compatible `text-embedding-3-small`, Jina-compatible
`jina-embeddings-v5-text-small`, and Gemini-compatible `gemini-embedding-001`
profiles with deterministic default/max dimensions and per-profile token
accounting. The catalog, seeded account, default NUMERIC prices, HTTP tests,
and versioned Gateway goldens cover all three. This is a new product contract;
no Sub2API data, key, URL, or legacy mapping is imported at runtime.

The latest completed vertical slice is the source-built Embeddings
provider-profile gate `scalaapi-embeddings-profiles-0810a` (Platform `5f04bfd`,
Gateway `22f65d4`). It returned deterministic Jina/Gemini dimensions and token
counts, then settled four pricing-versioned Embeddings requests exactly once.
The empty-volume run also passed the 47-record migration double-run,
Garnet-authenticated stack, fault/restart, reconciliation/operator, realtime
soak, media/S3, and Web checks; the smoke exited zero and removed its Compose
project, volumes, and containers.

## Images/media lifecycle implementation evidence

The read-only Sub2API reference exposes a broader batch-image lifecycle than the
current ScalaAPI edge: authenticated batch create/list/models/items/download,
cancel, record deletion, output deletion, and per-item content download. Its
public service keeps owner identity on every read or mutation, uses bounded cursor
queries, and treats cancellation/output cleanup as durable state transitions.

ScalaAPI now persists asynchronous image/video operations in PostgreSQL and copies
successful Provider bytes into S3-compatible storage. Gateway `418da3a` routes
`GET /v1/images/batches` to the internal media-operation RPC and projects batch
`/items` reads to the public `data` array. Platform `f75fbfb` adds a bounded,
newest-first `ListBatchesAsync` query isolated by API key and emits a stable
`object: list` envelope without Provider contact. The real Host test proves that
another API key cannot see the rows, and the source-built gate
`scalaapi-media-batch-0810a` proves batch creation, durable list visibility,
normalized items, MinIO object persistence, and the existing full restart/fault/
reconciliation/Web matrix. Platform `7d0abc6` adds Provider-backed image task/
batch cancellation, and `scalaapi-media-cancel-0810c` proves the cancel request,
durable cancelled read, and conservative reconciliation accounting. Object
listing/orphan cleanup, restart restore, retention, and full batch
download/reconciliation remain follow-on work.

The preceding completed vertical slice is the source-built protocol gate
`scalaapi-responses-compact-0810b` (Platform `18daa64`, Gateway `992f3fc`).
It adds exact, reserved `POST /v1/responses/compact` routing and forwards the
request through the existing Responses capability and settlement transaction to
the source-owned Provider mock. Non-stream JSON and `response.completed`-
terminated SSE both return a deterministic `compaction` output item with
consistent positive usage. Provider HTTP tests cover invalid input, non-boolean
`stream`, 429/500, malformed-success fixtures, and the reserved path's exact
method boundary.

The empty-volume run passed compact JSON/SSE through Gateway -> Cap'n Proto ->
Platform -> Provider and asserted exactly four completed leases, usage events,
usage logs, committed holds, and NUMERIC usage debits for the Responses root pair
plus compact pair. It also passed the 47-record migration double-run,
Garnet-authenticated stack, fault/restart, reconciliation/operator, realtime
soak, media/S3, and web checks. The smoke exited zero and removed its Compose
project, volumes, and containers.

The preceding completed vertical slice was the source-built protocol gate
`scalaapi-responses-input-items-0810b` (Platform `eecaff6`, Gateway `d1e4a85`). It includes
the provider-group gate below plus OpenAI Responses JSON/SSE.
Anthropic `count_tokens`, JSON Messages, and SSE Messages, plus Gemini model
catalog, JSON `generateContent`, and SSE `streamGenerateContent`, all traversed
Gateway -> Cap'n Proto -> Platform -> Provider mock. Four billable requests
settled exactly once: four leases, usage events, usage logs, committed holds,
and NUMERIC usage debits. The two provider-group control requests were explicitly no-charge:
both leases aborted, both holds released, and neither produced usage or ledger
rows. The empty-stack gate also passed the existing restart, reconciliation,
media, S3, content-policy, Provider-fault, and four-session realtime checks.
During this slice a retryable `pricing_snapshot_missing` event was found to
starve later durable usage reports; Gateway `d1e4a85` retains that event for
retry while continuing independent reports. The Garnet RESP health probe was
also corrected to emit actual CRLF bytes. The smoke trap removed the isolated
project and `podman ps -a` was empty afterward.

The `scalaapi-responses-input-items-0810b` run exercised the OpenAI Responses root
endpoint and read/input_items/cancel/delete subresources through the source-owned Provider mock.
Non-stream JSON validated the completed response envelope, output text, and
positive usage; SSE validated `response.created`, `response.output_text.delta`,
and terminal `response.completed` events with nested usage. Both generation
requests produced exactly one lease, usage event, usage log, committed hold,
and NUMERIC debit. `GET /v1/responses/{id}` returned the stored response and
created one aborted routing lease with a released hold and no usage/debit.
`GET /v1/responses/{id}/input_items` returned a stable list containing the
source request's input text and created a second aborted routing lease with a
released hold and no usage/debit. Deleting the response also removes this stored
input resource, and Provider HTTP tests cover its subsequent not-found result.
`POST /v1/responses/{id}/cancel` returned an idempotent `cancelled` response;
repeating the cancel returned the same state and a subsequent GET retained
`cancelled`. It created one additional aborted routing lease with a released
hold and no usage/debit.
`DELETE /v1/responses/{id}` returned a deletion envelope and created one further
aborted routing lease with a released hold and no usage/debit. The four controls
therefore released four holds without a usage event, usage log, or debit.
The malformed non-stream fixture returned HTTP 502 with the public
`provider_error` envelope, retained exactly one `reconciliation_needed` lease,
and produced no usage event, usage log, or NUMERIC usage debit. The full smoke
then reconciled and operator-resolved the expanded incident set. Remaining
mutation semantics beyond read/input_items/cancel/delete, provider-group fault coverage, and live adapters remain
open.

The following detailed bullets are retained as the preceding-slice record; the
current totals for Platform `1cc4538` and Gateway `418da3a` are 259/259 and
126/126 respectively:

- Gateway built locally and passed 126/126 CTest cases, including deterministic
  fault-hook claim/repeat behavior, terminal SSE detection, provider EOF
  classification, incomplete chunked-body disconnect classification, zero-length
  client-write cancellation, and bounded Provider pre-header stream timeout
  handling plus independent inter-chunk and total-stream timeout scenarios. The
  source-owned protocol golden suite covers versioned OpenAI Chat/Responses,
  Anthropic Messages, and Gemini request/response/SSE/error fixtures, all
  sixteen request pairs, all sixteen response pairs, cross-protocol response
  envelope validation, and cross-protocol error normalization with standard
  status precedence.
- Platform Release test/build passed with 0 warnings and 0 errors in the
  preceding slice: 247/247 tests, including 81 Host tests, 69 Grain tests, 46
  Admin tests, and 51 Provider mock
  tests. Admin coverage
  includes PostgreSQL-backed TOTP replay, backup-code consumption, lockout,
  recovery, OAuth provider/redirect/verifier binding, one-time state consumption,
  and expiry. Host coverage
  includes deterministic fault-hook configuration plus atomic operator
  settle/release, replay/conflict behavior, concurrent resolution serialization,
  OAuth credential refresh leases, atomic token rotation, failure backoff, strict
  token endpoint form/HTTPS handling, and sensitive-error redaction. Provider mock
  coverage additionally includes real ASP.NET HTTP contract tests through the
  Platform token client for rotation, revoked grants, malformed JSON, and bounded
  oversized responses. Passkey coverage adds the bounded challenge/credential
  lifecycle and monotonic counter assertions. Maintenance coverage adds bounded
  export redaction, cleanup deletion, actor-scoped replay, and changed-payload
  conflict evidence.
- Platform `75c4908` makes content-policy revision publication replay-safe and
  monotonic across propagation workers and cache rebuilds. Both paths take the
  same PostgreSQL advisory lock before the pinned Garnet deployment's native
  GET/SET/INCR sequence; an older revision cannot overwrite a newer projection,
  and repeated publication returns the existing invalidation version. Host tests
  cover duplicate, stale, failed, concurrent, and rebuild/replay behavior. The
  source smoke reached the OpenAI/external policy propagation and response-policy
  gates on real Garnet; its later Provider `disconnect_before_output` probe timed
  out at the host boundary and is retained as a failed environmental gate.
- Platform `32e9576` removes the batch-wide propagation lock: independent workers
  claim distinct change events with PostgreSQL `SKIP LOCKED`, then take the shared
  advisory lock only for one Garnet publication and its durable mark. The concurrent
  Host test now requires both workers to claim and propagate one event each; failed
  rows remain retryable.
- Platform `30cc8dc` adds `OpenAiModerationMetrics` to the classifier boundary and
  `/metrics`: fixed `classifier="openai"` counters cover requests, matches,
  no-matches, unavailable/protocol errors, and cancellations; a bounded histogram
  records completed classifier latency. Platform `330b9a8` adds migration 044,
  instance/sequence-idempotent PostgreSQL snapshots, retryable hosted flushing,
  cross-instance aggregation, unavailable-ratio and bucketed p95 output. Platform
  `f565d9f` adds migration 045, validated budget configuration, atomic durable
  breach alerts, and `/metrics` breach gauges. Platform `49f68d5` adds bounded
  rolling-window totals and automatic recovery of expired budget breaches. The
  targeted real-PostgreSQL classifier/schema/worker run passed 24/24 and the
  Release build passed with zero
  warnings/errors; output remains free of content, policy patterns, endpoints, and
  credentials. Hosted-worker instance/restart replay is covered. The source-built
  `scalaapi-metrics-process-0810f` gate now runs two Platform silos and two Gateway
  processes, sends an OpenAI Moderation match through the secondary pair, restarts
  both processes, and proves two instance IDs, sequence 1 snapshots, two requests,
  two usage events, and two NUMERIC debits. Credential rotation/redaction,
  deployed malformed/oversized/timeout/cancellation scenarios, and long-stream
  buffer/late-usage evidence remain release gates.
- Platform `926b98e` adds a deterministic `platform.after_policy_outbox_claim`
  process hook. The source-built `scalaapi-policy-reclaim-0810e` smoke terminated
  Platform after a PostgreSQL policy-event claim, restarted the same container,
  reclaimed the expired claim, and completed monotonic Garnet publication before
  passing the full 44-migration/Provider/realtime/reconciliation/MinIO matrix.
  The smoke also waits for API-key audit persistence after recovery, avoiding a
  false negative from asynchronous projection writes.
- Platform `e639b50` isolates the `disconnect_before_output` Provider fault in its
  own seeded account/group and completes the zero-length SSE response with normal
  EOF, avoiding a Kestrel hard-abort/Photon socket-timeout artifact. The source-
  built `scalaapi-fault-isolation-0810b` smoke exited 0 after applying and replaying
  all 44 migration records, passing Garnet, OpenAI/external policy, restart,
  realtime, the complete Provider matrix including `disconnect_before_output ->
  503` with one retained unknown-charge hold, audited reconciliation, and MinIO
  persistence. The trap removed all smoke containers and anonymous volumes;
  `podman ps -a` was empty afterward.
- Platform `4ed1d5b` adds the multi-process classifier metric runtime gate. The
  source-built `scalaapi-metrics-process-0810f` project started a second Platform
  silo and Gateway on an independent Cap'n Proto socket, waited for two active
  Orleans membership rows, sent and settled two OpenAI Moderation response-policy
  requests across a Platform and Gateway restart, and asserted two persisted
  snapshots from two instance IDs with sequence 1 on each, two requests, two
  usage events, and two `usage_debit` ledger effects. The command exited 0 and
  cleanup left `podman ps -a` empty.
- Platform `6ae059b` extends the same source-owned gate with concurrent requests
  through the primary and restarted secondary Gateways using one API-key
  idempotency key. It accepts only success or active replay and proves one
  completed idempotency row, one lease, one usage event, and one NUMERIC
  `usage_debit`; the isolated project is removed on exit.
- Platform `c7bd987` extends that gate with a controlled secondary Gateway/Silo
  outage: the primary pair settles while one active Silo remains, the original
  secondary containers rejoin, and a second request settles through the rejoined
  pair. The empty-volume run proves two completed leases, usage events, and
  NUMERIC debits and removes the isolated project on exit.
- Platform `1f0f9ef`, `840636e`, and `0acdf9c` add migration 046, idempotent
  PostgreSQL backup/restore job state, a dedicated backup volume, PostgreSQL 17
  `pg_dump`/`pg_restore` clients, and bilingual Admin controls. The source-built
  `scalaapi-backup-0810b` empty-volume gate applied/skipped all 46 product
  migrations, created a non-empty SHA-256-verified artifact, replayed create and
  restore commands, restored a fresh user into `platform_restore`, and left no
  containers after cleanup. Offsite/S3 backup, encryption/signing, measured
  RPO/RTO, and rollback remain open.
- Announcement coverage adds published/expiry filtering, read-state listing,
  duplicate-read replay, and exactly one `announcement.read` audit row through a
  real PostgreSQL test; User Web builds with the Dashboard read action.
- Email delivery coverage adds encrypted password-reset and verification outbox
  rows, one-shot SMTP-worker delivery, superseded-token cancellation, retry/backoff
  recovery, and action-link hydration through three real PostgreSQL Admin tests.
- Subscription quota coverage adds migration 035, a PostgreSQL `FOR UPDATE`
  reservation boundary, concurrent over-allocation rejection, normal settlement
  consumption, and no-charge release. The user subscription API and Billing/
  Dashboard expose `quotaReservedUsd` and `quotaRemainingUsd`; payment-provider
  coupling and browser evidence remain open. Admin `SubscriptionRenewalService`
  coverage additionally proves auto-renew grant reset, stale-expired recovery,
  no-renew expiry, reservation deferral, deterministic events, and concurrent
  worker once-only processing.
- BILL-02 coverage adds migration 036 source/provider/checksum columns. The
  `ProviderPricingCatalogClient` test proves bearer-header handling, deterministic
  checksum/version generation, decimal bounds, duplicate rejection, and bounded
  response parsing. Against an empty PostgreSQL 17 schema, two snapshots inserted
  four immutable model versions, replay inserted zero rows, and the changed snapshot
  closed the two previous open rows while leaving the new versions open. The source
  Provider Mock exposes three deterministic decimal quotes; provider-specific
  adapters, tokenizer/golden fixtures, and multi-provider runtime E2E remain open.
- Media reconciliation coverage adds migration 037, a PostgreSQL `SKIP LOCKED`
  object-check batch, signed S3-compatible HEAD verification, and a real database
  recovery test. A missing object leaves the business operation `succeeded` and
  marks only metadata `failed`; after the object is restored, the next check clears
  the error and returns metadata to `stored`. Object listing/orphan cleanup, restore,
  and full MinIO restart/cancellation evidence remain open.
- SEC-01 now has executable request, non-stream response, and SSE event-boundary
  evidence: the canonical Cap'n Proto contract carries bounded request/response
  policy content, Platform evaluates active scoped rules before lease creation or
  before successful output, PostgreSQL Host tests cover stage isolation and
  idempotent audit logging, and the source smoke asserts request no-lease blocking
  plus response output withholding with normal usage settlement and exact 400
  replay. Gateway buffers a bounded SSE event until policy approval, emits a
  protocol-shaped terminal policy error on block/fail-closed outcomes, and keeps
  unknown-charge settlement evidence when output is interrupted. The source-owned
  evaluator normalizes compatibility/decomposed/confusable Unicode forms, rules
  carry an evaluator/classifier version, policy mutations bump a monotonic revision,
  and redacted audits never persist sensitive snippets. An unavailable external
  classifier fails closed and is covered by Host tests, Gateway fail-closed tests,
  and the empty-stack response/settlement probe. Migration 030 adds an append-only
  policy-change outbox with expiring claims, authenticated Garnet revision/
  invalidation propagation, and deterministic `policy_block`/`classifier_unavailable`
  alert rows with protected Admin queries. Platform `7fca582` adds a source-owned
  HTTP adapter with explicit `content`, `pattern`, and `evaluator_version` JSON,
  bounded request/response bytes, a configurable 100-5000ms timeout, and stable
  fail-closed mappings for transport, status, timeout, and malformed outcomes.
  Platform `dcdca5e` adds the configurable HTTPS OpenAI Moderation adapter:
  Bearer authentication, `omni-moderation-latest` default model, 129 KiB input /
  16 KiB response bounds, single-result `flagged` validation, and the same stable
  fail-closed mappings. The Provider mock exposes an official-shaped
  `/v1/moderations` fixture with deterministic flag, no-match, unavailable,
  malformed, oversized, and timeout scenarios; Host and Provider HTTP tests cover
  authentication, parsing, bounds, and failure behavior. Platform `2992964` adds
  migration 043 and the tested Admin rule normalizer so `openai` can be selected,
  persisted, evaluated, and written to redacted audit evidence on the greenfield
  schema. Platform `94e0db8` removes the policy revision TTL and extends the
  authenticated cache rebuild with PostgreSQL revision plus Garnet invalidation
  evidence; the dedicated RemoteGarnet test proves recovery after both keys are
  deleted. Platform `49f68d5` adds migrations 044-045 with idempotent
  instance/sequence snapshots, retryable flush, aggregate unavailable-ratio and
  bucketed p95 output, configuration-backed breach gauges, and durable budget
  alerts; the real-PostgreSQL/schema/worker coverage is 24/24 without sensitive
  labels.
  A deterministic hosted-worker test now creates two independent instances and a
  restarted instance, proving one sequence-one snapshot per worker and complete
  aggregate totals. Actual separate Compose process restart, browser evidence,
  credential rotation, and long-stream soak remain open, so the domain is still
  `partial`.
- AUTH-01 coverage includes email/password boundary tests, PostgreSQL-backed login
  identity/IP lockout and success reset tests, independent-IP accounting,
  registration-IP lockout, migration schema assertions, and duplicate insert
  conflict handling. The source-built `scalaapi-auth-abuse-verified3` smoke
  returned 400 for malformed registration, five 401 responses followed by a
  429 login lock, and passed the complete 27-migration, Garnet, protocol,
  restart, Provider fault, reconciliation, and MinIO matrix.
- API-key policy tests pass in the 66-case Grain suite: scope normalization,
  unknown-scope rejection, explicit projection round-trip, capability allow/deny,
  and expiry rejection. The schema gate requires `user_api_keys.scopes`,
  `expires_at_ms`, and the append-only `api_key_audit_events` table. The runtime
  container now includes migrations `025-api-key-policy-audit.sql` and
  `026-auth-abuse-counters.sql`; authenticated
  HTTP audit-row and denied-capability empty-stack assertions are covered by the
  latest source-built smoke; authenticated HTTP replay/concurrency, expired-key,
  API-key audit-query, Admin update/revoke, and user-rotation cases now pass.
  Multi-instance contention and browser coverage remain release gates.
- The latest source-built empty-volume project `scalaapi-key-policy-verified`
  applied all 26 migration files and observed all 26 as `skip` on a second run.
  An API key scoped only to `models` received HTTP 403 `permission_error` for
  Chat, produced one `api_key_audit_events` denied row, and produced zero
  request leases. The same run passed the Garnet-authenticated Gateway ->
  Platform -> Provider path, NUMERIC settlement/replay, realtime WebSocket,
  restart/recovery, audited reconciliation, Provider failure matrix, and
  S3-compatible object assertions; its cleanup trap removed the temporary stack.
- Admin Web and User Web typecheck and production builds passed. Admin Web now
  manages static/OAuth Provider credentials without reading stored secrets, exposes
  open/resolved reconciliation incidents, manual runs, and evidence-backed settle/release
  commands through the existing authenticated API, and adds a Content Policy console for
  rule CRUD plus propagation-change and alert queries. The checked-in Playwright smoke
  intercepts the new API contract and renders rules, changes, alerts, navigation, filters,
  and a full-page screenshot. The User Web
  build includes password recovery, email verification, authenticator security,
  active-plan subscription controls, redeem codes, and referral summary routes.
- User Web now also serves unauthenticated `/models`, `/status`, `/terms`, and
  `/privacy` routes. `/models` proxies the Gateway public model directory and
  `/status` proxies Gateway readiness; the legal pages are versioned static
  notices. A checked-in Chromium smoke verifies public navigation, table
  caption/scoped headers, status feedback, legal navigation, and access without
  a session. The source-built `scalaapi-public-ui-0810b` Compose probe additionally
  verified the nginx-to-Gateway `/v1/models` and `/ready` paths and all four public
  routes in Chromium (`1/1`); its cleanup trap left no containers. The public slice
  remains partial until deployment-specific accessibility evidence and legal-text
  configuration are added.
- The source-built `scalaapi-user-portal-0810b` Compose probe also registered a
  fresh user and passed the live public plus authenticated User Web cases (`2/2`):
  login, Dashboard, Usage, API keys, and Profile all loaded through the
  nginx-to-Admin API proxy. The browser test is intentionally live-only; it does
  not claim backup-code, Passkey, payment, recovery-mail, or mutation workflows.
- `deploy/stack/realtime_smoke.py` passed against a real Release `Provider.Mock`
  process and through the full Gateway -> Platform -> Provider path. The clean
  Gateway runtime image was built from the immutable Photon commit
  `4dd457013c48d17c571fd6d2aa87199ae4c25d4f` after disabling shallow FetchContent
  checkout (the upstream does not advertise that commit on a discoverable ref).
  The realtime probe completed the HTTP/1.1 upgrade, sent a masked
  `session.update`, validated deterministic `session.created` and `response.done`
  usage frames, and settled exactly one lease, usage event, usage log, committed
  hold, and `usage_debit` ledger row. `GATEWAY_IMAGE` now lets Compose reuse this
  verified runtime image while the default path still builds from source.
- `deploy/stack/realtime_soak.py` now opens four concurrent `/v1/responses`
  WebSocket sessions at the seeded user's concurrency limit, validates each
  `session.created`/`response.done` usage frame, keeps each upgraded connection
  open for three seconds, and closes it cleanly. The source-built
  `scalaapi-realtime-soak-0810b` gate proved four completed leases, usage events,
  usage logs, committed holds, and NUMERIC `usage_debit` rows with no duplicates;
  all temporary containers were removed.
- The current source-built empty-volume project `scalaapi-oauth-refresh-20260809`
  passed the complete smoke gate with Platform `9320320` and Gateway `9c7171f`.
  The seeded OpenAI account began with encrypted expired `mock-access-v1` /
  `mock-refresh-v1`; the first billable Chat request rotated it to version 2 over
  the mock OAuth HTTP endpoint, succeeded, and settled one NUMERIC debit. The
  Admin account-details response reported version 2 and a future expiry without
  exposing access, refresh, or client-secret material. The same run passed the
  Garnet-authenticated stack, 25-migration double run, restart/recovery, Provider
  failure matrix, reconciliation, and MinIO assertions; its cleanup trap removed
  all project containers, volumes, network, and stack image tags.
- The current-source empty-stack project `scalaapi-gateway-recovery-0907` ran with
  `GATEWAY_FAULT_HOOK=gateway.after_provider_completion` and a 15-second lease TTL.
  Gateway returned an empty transport reply, persisted its one-shot marker,
  terminated, and was explicitly started as the same container. Readiness
  recovered, the original lease became `reconciliation_needed` with an active
  hold and no usage/debit, and the marker prevented a repeat crash. The complete
  gate then passed with ten unknown-charge incidents, one audited operator settle,
  and nine remaining open incidents. The cleanup trap removed the temporary
  project; only the named `apitf_*` development resources remain.
- The current-source project `scalaapi-gateway-dispatch-recovery-0911` ran with
  `GATEWAY_FAULT_HOOK=gateway.before_provider_dispatch` and the same 15-second
  development TTL. Gateway terminated before Provider contact, was explicitly
  started as the same container, and preserved its marker. The lease transitioned
  from `held` to `expired`, the hold and idempotency row were released/expired,
  and no usage, ledger debit, or reconciliation incident was created. The full
  matrix then passed with nine unknown-charge incidents, one audited settlement,
  and eight remaining open incidents; all temporary resources were removed.
- The current-source project `scalaapi-platform-dispatch-recovery-0912` ran with
  `PLATFORM_FAULT_HOOK=platform.before_provider_dispatch` and the same 15-second
  development TTL. Platform terminated after persisting the SQL lease and hold but
  before returning an upstream target; the same container was explicitly started,
  the durable marker survived, and the lease transitioned from `held` to `expired`.
  The hold and idempotency row were released/expired with no usage event, usage log,
  ledger debit, or reconciliation incident. The full matrix then passed with nine
  unknown-charge incidents, one audited operator settlement, and eight remaining
  open incidents. The cleanup trap removed the temporary project and image tags.
- The current-source project `scalaapi-platform-worker-recovery-0913` ran with
  `PLATFORM_FAULT_HOOK=platform.after_outbox_claim`. Platform completed the SQL
  settlement, claimed the durable `complete` outbox item, then terminated before
  invoking any Grain side effect. The same container was explicitly started; the
  expired claim was reclaimed and the outbox completed with no duplicate lease,
  usage event, ledger debit, or hold transition. The full matrix passed with nine
  unknown-charge incidents, one audited operator settlement, and eight remaining
  open incidents; temporary containers, volumes, networks, and image tags were
  removed after evidence capture.
- The current-source project `scalaapi-platform-dispatch-retry-0914` enabled
  `PLATFORM_FAULT_HOOK=platform.before_provider_dispatch`. Platform terminated
  after creating the durable lease/hold; Gateway retried the same request with
  the same idempotency identity, the replacement Platform recovered the active
  lease, and the Provider request settled exactly one lease, usage event, usage
  log, and NUMERIC debit. The complete matrix passed with nine unknown-charge
  incidents, one audited operator settlement, and eight remaining open incidents.
  This smoke used a clean runtime image built from the pinned Photon commit with
  `GIT_SHALLOW=FALSE`; all temporary containers, volumes, networks, and image
  tags were removed after evidence capture.
- The latest isolated project `scalaapi-realtime-smoke-20260809` reused the clean
  Gateway image `localhost/scalaapi-gateway-realtime-fix-1786253644`. The complete
  gate passed the 22-migration double run, Garnet-authenticated Chat/replay,
  realtime WebSocket settlement, Platform/Gateway restart requests, the Provider
  failure matrix, audited reconciliation, and MinIO signed media persistence.
  Its cleanup trap removed the project containers, volumes, network, and all
  stack-specific resources; only named `apitf_*` development resources remain.
- The current-source project `scalaapi-scheduling-verified` repeated the full
  26-migration empty-volume gate after the group scheduling change. Garnet,
  rotating sessions, OAuth refresh, realtime settlement, restart recovery, the
  Provider fault matrix, audited reconciliation, and MinIO signed persistence all
  passed; its containers, volumes, network, and temporary image tags were removed.
- The current-source project `scalaapi-auth-abuse-verified3` applied all 27
  migration files and observed all 27 as `skip` on the second run. Invalid
  registration returned 400; five failed logins for one unknown email returned
  401 and the sixth returned 429 with a durable PostgreSQL counter. The same
  run passed authenticated Garnet dispatch, auth-session rotation, OAuth
  refresh, realtime settlement, Platform/Gateway restart recovery, the complete
  Provider matrix, audited reconciliation, and MinIO signed persistence. The
  cleanup trap removed the temporary project resources and stack-specific tags.
- The current-source project `scalaapi-key-http-verified` added two concurrent
  Chat requests sharing one API-key idempotency key and observed one completed
  lease/idempotency row with no duplicate billing; the serialized responses were
  the documented active replay conflict or completed replay. A short-lived API
  key returned HTTP 401 `authentication_error` after expiry and created no lease.
  The complete 27-migration Garnet, Provider, restart, realtime, reconciliation,
  and MinIO matrix passed and all temporary stack resources were removed.
- The current-source project `scalaapi-api-key-audit-verified` additionally
  authenticated `GET /admin/apikeys/{hash}/audit?action=denied`, verified the
  filtered actor/action record, and rejected any plaintext-key field in the
  response. The full matrix passed again and all temporary containers, stack
  images, and dangling images were removed, leaving only the named `apitf_*`
  development resources and baseline images.
- The follow-up source smoke at Platform `4605f45`, project
  `scalaapi-api-key-lifecycle-verified`, authenticated the audit query, rejected
  an Admin ownership-changing update with 400, accepted a valid policy update,
  settled Chat through the updated key, revoked it and observed 401, then
  rotated a User Web key and verified distinct old-revoked/new-active rows plus
  `updated`, `revoked`, and `rotated` audit records. The lifecycle assertions
  passed repeatedly; the complete matrix still exposes the known
  `disconnect_before_output` transport-timeout/reconciliation fixture and is not
  claimed as a new green release gate. A later clean run below supersedes that
  temporary limitation.
- The current-source project `scalaapi-oauth-20260809b`, using Platform `3572abd`,
  passed the full empty-volume gate. It drove the configured Provider mock
  authorization endpoint, redeemed a one-time authorization code with the exact
  redirect URI and S256 verifier, created and bound `oauth-user@example.test` to
  `mock-oauth-user`, and rejected callback replay as `oauth_state_replayed` (400).
  The same run passed 27 migration skips on the second migrator invocation,
  Garnet, API-key lifecycle, realtime, restart/recovery, the complete Provider
  fault matrix, audited reconciliation, and MinIO persistence; the smoke trap
  removed all project containers, volumes, network, and temporary tags.
- The current-source project `scalaapi-embeddings-20260809b`, using Platform
  `ef1e474` and Gateway `40cb02f`, passed the full empty-volume gate after the
  smoke count was updated for the additional malformed-Embeddings incident. It
  applied 27 migrations and skipped all 27 on the second run, returned two
  three-dimensional float vectors and one two-dimensional base64 vector, settled
  both with the Embeddings price version, and mapped a shape-invalid Provider
  response to `502/provider_protocol_error` while retaining one
  `reconciliation_needed` hold. The same run passed Garnet, OAuth, API-key
  lifecycle, realtime, restarts, the complete Provider matrix, reconciliation,
  and MinIO persistence; cleanup removed the temporary stack and tags.
- Gateway `6243b2d` adds fail-closed validation for OpenAI model list entries,
  Gemini model metadata/token limits, and Anthropic positive bounded `input_tokens`;
  the source CTest catalog cases and Platform `d126ea5` Provider HTTP tests cover
  valid metadata plus malformed, duplicate, zero, and invalid token-count profiles.
- Gateway `b27965f` validates direct non-stream OpenAI Responses envelopes before
  settlement: completed status, non-empty output item types, model/id metadata,
  and positive consistent input/output/total usage are required; malformed
  envelopes retain an unknown-charge lease. Gateway `8f33790` freezes matching
  versioned request/response/SSE/error fixtures and runs all four protocol
  parsers, all sixteen request pairs, all sixteen response pairs,
  usage/terminal-event handling, and cross-protocol converters. Gateway `ab09bf8`
  now maps cross-protocol Provider errors into target OpenAI, Anthropic, or Gemini
  envelopes while preserving same-protocol bodies and provider error codes.
  Gateway `3da0d33` additionally translates Gateway-generated transport and
  protocol failures to the inbound envelope even when the upstream format is
  the same, while leaving explicit same-format Provider errors untouched.
- Scheduler benchmark integrity dry run executed all 4 selected child benchmarks
  and returned zero. It is a failure-propagation check, not performance evidence.
- `deploy/stack/smoke.sh` built current sibling sources in isolated Podman
  project `scalaapi-smoke-0830`, created new volumes, applied all 22 migrations,
  and observed all 22 skip on the second migrator run. Image IDs were Platform
  `09a9a0871f09318b414b1fbca2a4de0733b7a2189f7541da12dd8cfa5a6424cf`, Admin API
  `2734567850bfa0df74f099a6c2346465a6d4336d7cfbc0feb4d5dcfce4472eb9`, Gateway
  `6ffc8dd0a8bf7e7597de9cd98a0fe044896565a54a7f5a25f4478dd769b06826`, Provider
  mock `38f69d466c88ab33b1e14f9a9647e0c6c520fc8f48ad67aa27f91b45233ce2e5`,
  migrator `d8c9db33a784faefdf5b3c9dc1c08e50c236554b70e4f4f081eda5a7630090c4`,
  and Admin Web `dfb8a98fe1a8dc32c8c7721743eddcee86dd2f042c4218cb6a9c9c713db02ba2`.
  The smoke intentionally crashed Platform once before settlement commit; the
  harness observed the exit, explicitly started the same container, replayed the
  Gateway usage outbox after reconnect, and preserved one debit. It also replaced
  Platform and Gateway independently, separated explicit non-stream and streaming
  429/500 rejections from nine unknown-charge scenarios, including Provider
  disconnect, disconnect-before-output, malformed usage, timeout before response
  headers, partial SSE disconnect, invalid streaming content type, and a real
  downstream client timeout after the first SSE event. The pre-header timeout
  returned bounded HTTP 502 with `provider_protocol_error`, retained one hold, and
  did not fail over. Direct non-stream and zero-output streaming resets returned
  HTTP 503 with `provider_unavailable`; partial SSE resets are retained as
  unknown-charge even when the client observes a transport-level 000. One incident was settled through the Admin API, replayed as
  `duplicate`, and the next reconciliation retained eight remaining open
  unknown-charge incidents. A dedicated `disconnect_after_usage` stream returned
  HTTP 200 while the Provider ended before `[DONE]`; the Gateway outbox and
  Platform completion transaction recorded exactly one usage event, usage log,
  committed hold, NUMERIC debit, and completed idempotency row.
- The clean-stack Admin API funded a new zero-balance user once. Exact replay
  returned the same ledger identity, changed replay returned 409, overdraft returned
  409, and PostgreSQL contained exactly one NUMERIC adjustment and one actor audit.
- The empty-stack Chat request settled with one completed lease, one committed
  hold, one usage effect, one versioned NUMERIC debit, and drained Platform,
  accounting-projection, and Gateway outboxes. Exact response replay produced no
  second charge. SQL assertions proved posted balance equals ledger sum and every
  user ledger version is contiguous and unique.
- The real-database reconciliation test corrupted an account and terminal hold,
  repaired only safe hold/projection drift, preserved an unknown charge and active
  hold, accepted late settlement, and resolved both incidents on the next run. The
  stack gate intentionally ended with eight open unknown-charge incidents, so the
  comprehensive reconciliation result was `failed` rather than falsely reporting a
  clean account boundary.
- Platform and Gateway were independently replaced; a fresh billable request after
  each replacement settled once.
- Independent 500 and 429 scenarios produced explicit Provider rejections: four 500
  attempts and one 429 attempt ended `aborted` with released holds and no debit.
  Malformed-success, upstream-disconnect, and timeout each made one attempt, did not
  fail over, ended `reconciliation_needed`, retained the hold/idempotency key, and
  created one operator-visible incident without a usage debit. Streaming Provider
  disconnect, disconnect-before-output, malformed-usage, invalid-content-type, and
  downstream client cancellation scenarios did the same; each retained the hold
  with no usage or debit. The client-cancellation request used a short-lived curl, received the
  first SSE event, closed before the delayed second write, and returned transport
  status 000 while the Gateway recorded unknown charge evidence. Streaming 500
  exhausted four accounts and streaming 429 exhausted one account; both returned
  public 503/provider_unavailable responses and released every no-charge hold.
- Garnet authentication returned `PONG`; asynchronous media bootstrapped the empty
  MinIO bucket and a signed URL downloaded the expected 67-byte object.
The current source-built `scalaapi-openai-moderation-0810e` smoke command exited
zero. It applied and replay-skipped all 44 empty-volume migration records,
authenticated Garnet routing, exercised OpenAI Moderation response match and
upstream-unavailable cases as HTTP 400/503, and verified redacted audits,
warning/critical alerts, one normal lease/usage/debit settlement, and exact
idempotency replay. Chat, Embeddings, Realtime, Provider faults, restart/recovery,
reconciliation/operator resolution, and S3 persistence also passed. The cleanup
trap removed all project containers and volumes; `podman ps -a` was empty.

The following older checkpoint is retained for historical comparison only:
the `scalaapi-classifier-20260809d` smoke command exited zero. It
  applied 31 empty-volume migration records and skipped all 31 on replay, proved
  request and response content-policy paths, the source-owned external classifier
  and configurable OpenAI Moderation adapter contracts, the complete Provider
  fault matrix,
  Garnet-authenticated routing, media persistence, reconciliation, operator
  settlement/replay, and post-restart billing. The Unicode request scenario matched
  fullwidth/decomposed/confusable content, redacted its audit snippet, and created
  no lease. The external-classifier match scenario blocked with HTTP 400 and the
  outage scenario failed closed with HTTP 503; both redacted their audits and
  completed exactly one normal usage settlement and exact response replay. The
  response policy scenario hid
  Provider output while recording one audit, completed lease, committed hold,
  usage event/log, NUMERIC debit, and replayed client-facing HTTP 400 exactly.
  The streaming policy case adds one retained unknown-charge incident; the run
  therefore ended with eleven open incidents before the audited settlement and
  ten after it, with no duplicate debit for the blocked stream.
  The run also waited for policy-change outbox propagation, queried policy-block
  and classifier-outage alert evidence, and verified retryable Garnet propagation
  in Host tests. The cleanup removed every project container, temporary volume/
  network, and the `scalaapi-classifier-20260809d_*` image tags. Only the named
  `apitf_*` baseline development resources and `scalaapi-gateway:dev` remain.

Detailed gate results and residual coverage are maintained in `verification.md`.

## Known gaps

- PostgreSQL is the only monetary authority and periodic reconciliation now uses
  persisted held/forwarded/output-started evidence to classify expiry and aborts.
  Admin operators can resolve an open unknown-charge incident exactly once through
  an audited, idempotent `settle` or evidence-gated `release` command; subscription
  quota grants and signup referral attribution/anti-abuse still need explicit
  authority contracts beyond the audited Admin reward command.
- Operational metrics now require an authenticated actor and are transactionally
  audited; summaries and policy alert evidence are bounded for Admin queries.
  Collector alert rules, cross-service correlation, traces, dashboards, and
  delivery/recovery evidence remain required before OPS-02 is implemented.
- Admin audit reads are now bounded and redact sensitive JSON fields; generic
  client audit insertion is removed. Retention/immutability enforcement,
  export authorization, browser coverage, and security scanning remain before
  SEC-02 is implemented.
- User data export now returns a bounded repeatable-read snapshot without credential
  material, and maintenance cleanup is retention/limit bounded with idempotent audit
  evidence. Scheduled execution, immutable retention, object/media cleanup, and
  browser download authorization remain before OPS-05 is implemented.
- Proxy and TLS administration now encrypts proxy credentials, validates profile
  inputs, and records actor/IP audit evidence. Provider-specific outbound
  adapters, actual TLS fingerprint application, retention/rotation, and browser
  security evidence remain before SEC-03 is implemented.
- Gateway now classifies client cancellation and incomplete SSE as unknown-charge
  outcomes, records disconnect/cancellation reasons, and prevents failover after
  output or partial Provider output. The source-level behavior is covered by 125
  CTest cases; the empty-stack gate now proves Provider partial-SSE disconnect,
  disconnect-before-output, malformed-usage retention, and bounded pre-header
  timeout handling with no usage/debit.
  The empty-stack gate now proves actual downstream socket cancellation as well;
  Gateway commit `9c7171f` preserves valid Provider usage observed before
  truncated SSE EOF, settles it through the existing durable outbox path, and
  retries transient Platform dispatch loss under the same request identity.
  The same source line normalizes Provider
  connection resets and scheduler exhaustion to `503/provider_unavailable`; bounded
  timeout and malformed protocol cases remain `502/provider_protocol_error`; the
  timer distinctions are pinned by Gateway `18083f9`. Gateway `ab09bf8` also
  normalizes cross-protocol Provider errors into the inbound protocol envelope
  and gives standard HTTP status semantics precedence over conflicting provider
  labels; same-format explicit Provider errors remain byte-preserving, while
  Gateway-generated failures use the inbound protocol envelope.
- Source smoke now proves Platform before-provider-dispatch termination after lease
  creation with safe held expiry, Platform after-outbox-claim reclaim,
  pre-settlement-commit/post-commit/pre-outbox-acknowledgement crash boundaries,
  Gateway reconnect/backoff recovery, durable usage replay, and exactly-once
  settlement. The current source gate also proves Gateway termination before
  dispatch with safe held expiry and after Provider completion with retention of its
  forwarded lease/hold for reconciliation. Platform dispatch retry and active
  lease recovery now pass; realtime dispatch retry and full-stack realtime
  settlement are covered by source and empty-stack evidence. A four-session,
  three-second runtime WebSocket soak now passes; longer backpressure/load,
  cross-process Garnet failure/restart, and multi-instance hook assertions remain.
- Garnet authentication, outage/reconnect, rebuild, invalidation flush, and the
  content-policy revision outbox have evidence. Platform `e5c341d` adds a real
  TLS RESP listener test for custom-CA trust and server-name rejection. Actual
  Garnet TLS image flags and read-only certificate mounts are now source-smoke
  proven by Platform `8ca919b`; the source gate also rejects wrong-name and expired
  bundles, recovers a valid bundle, and settles a new request. Concurrent
  multi-Gateway/multi-Silo ordering and partition recovery are not release gates
  yet.
- Hosted CI cannot currently check out the private sibling repository with the
  default per-repository token. The local cross-repository smoke must become a
  blocking release workflow with a read-only checkout boundary.
- Provider adapters beyond the mock, provider-specific OAuth refresh profiles,
  provider-specific tokenizer/catalog fixtures, User Web browser
  tests for auth recovery/TOTP/Passkey UX, Passkey anti-enumeration and abuse,
  full commercial coupling, audit/observability,
  HA, load/soak, signed offsite backup, measured recovery, and rollback remain partial.
- Admin Web now has a blocking type/build gate and a checked-in Chromium smoke runner;
  User Web now has a checked-in local/public-Compose Chromium smoke runner plus a
  live authenticated portal smoke, while mutation, recovery, and security-ceremony
  browser workflows remain open;
  User Web Passkey controls are source-built but lack real authenticator evidence;
  email delivery, backup-code sign-in, payment checkout, referral signup
  attribution, and account-management browser scenarios remain.

## Historical boundary and acceptance rule

Old containers, historical databases, `/var/run/sub2api`, old image IDs, and manual
long-lived stack observations are not release evidence. New evidence must record the
current commits or worktree, source-built images, empty environment shape, and top
level exit code.

A capability is accepted only for the new ScalaAPI contract and state machine. No
test may require Sub2API data, IDs, keys, internal APIs, database layout, or behavior
compatibility.
