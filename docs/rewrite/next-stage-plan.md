# ScalaAPI Next Stage Plan

## Checkpoint

The next stage starts from Platform `7fca582`, Gateway `12bf8f1`, and read-only
reference `sub2api@43ec48d`.

The greenfield baseline now starts from empty volumes, uses PostgreSQL as authority,
Garnet as the only distributed projection/cache, S3-compatible object storage for
media, and a source-owned Provider mock. The checked-in gate proves idempotent
migrations, Chat settlement/replay, clean requests after Platform/Gateway
replacement, and evidence-based failure outcomes for non-stream and selected
streaming OpenAI Chat. An
actual non-stream and streaming 429/500 Provider rejections release; malformed
success, upstream disconnect, timeout, and invalid streaming media type make no
second attempt and retain their holds for reconciliation.
PostgreSQL now owns one ordered account per
user; administrative, payment/refund, redeem, and usage effects share one append
rule, SQL holds authorize dispatch, and Orleans is a retryable versioned projection.
Leases persist immutable `held`, `forwarded`, and `output_started` evidence. Only a
never-forwarded held lease may expire and release; all forwarded ambiguity becomes
`reconciliation_needed`, with its hold and idempotency key reserved until a late
completion or future operator decision. A globally serialized scheduled
reconciler checks the full account/ledger/usage/hold/projection boundary, performs
only provably safe repairs, persists incidents, and exposes Admin queries and
metrics. An Admin-only, token-protected operator command now settles or releases
one open unknown-charge incident exactly once with actor, evidence, reason, lease
event, and audit persistence in the same transaction; subsequent reconciliation
preserves that decision. Gateway and Platform now expose deterministic one-shot
fault hooks around dispatch, Provider completion, settlement commit, outbox claim,
and outbox acknowledgement. The source smoke also exercises the outbox-claim boundary and
intentionally crashed Platform before settlement
commit, after settlement commit, and before outbox acknowledgement, explicitly
restarted the same container, and recovered a single Orleans silo without a
duplicate debit; Gateway reconnect/backoff recovery drained the durable usage
outbox. Gateway now
has source-level terminal-event-gated SSE completion, incomplete chunked-body
classification, and client-cancellation classification. The empty stack proves
Provider disconnect, disconnect-before-output, malformed-usage, timeout before
response headers, and actual downstream client-cancellation and invalid-content-type
SSE retention with eleven total unknown-charge incidents. The pre-header timeout now
returns a bounded 502/provider_protocol_error and retains its hold; direct and
zero-output Provider resets now return 503/provider_unavailable, while partial SSE
resets retain their unknown-charge hold. Gateway CTest now independently proves the
inter-chunk and total-stream timers. The `disconnect_after_usage` profile emits valid
usage and ends before `[DONE]`; the empty-stack gate settles it exactly once through
the durable outbox. The latest source smoke also crashes Gateway after Provider
completion, restarts the same container, and retains the forwarded lease/hold as
one reconciliation incident without a debit. Gateway before Provider dispatch and
Platform before Provider dispatch now safely expire their unforwarded held leases
after the same container is restarted. A Platform worker crash immediately after
claiming a completed outbox event is also reclaimed and applied once. Platform
dispatch responses now expose a dedicated retryable `platformUnavailable` code;
Gateway retries it with bounded backoff under the existing deadline, and Platform
rebuilds the original active lease target after process loss. The Chat smoke proves
one lease, usage event, usage log, and debit after this recovery. The shared
Gateway source policy now covers HTTP and realtime dispatch retry with the same
identity and bounded backoff. The full empty-stack realtime probe now passes
through the clean Gateway runtime image, including exactly-once
lease/usage/hold/ledger settlement; long-connection/backpressure soak, remaining
boundaries, the worker/multi-silo matrix, and multi-instance scenarios are still
open.
The current source-built `scalaapi-classifier-20260809d` gate applies and
replays 31 migration records from empty volumes, proves Garnet-authenticated
request routing, staged request/response policy, the versioned Unicode evaluator,
external-classifier match/outage handling, the full Provider fault matrix, media
persistence, audited reconciliation, and exactly-once restart billing. The
Unicode request probe matches fullwidth/decomposed/confusable content, redacts its
audit, and creates no lease. The source-owned external classifier match probe
returns HTTP 400 and its outage fixture returns HTTP 503; both redact their audit
and still commit one normal Provider usage debit with exact response replay. A
response block withholds Provider output but still commits the
normal Provider usage debit and stores an exact client-facing 400 replay. Content
rule creation returns its persisted identity, and the Provider mock forces headers
before the zero-byte disconnect fixture so the intended 503 classification is
deterministic. Gateway `12bf8f1` additionally withholds each bounded SSE event
until response policy approval, emits an OpenAI/Anthropic/Gemini-shaped terminal
policy error on block or fail-closed evaluation, and preserves the unknown-charge
hold and idempotency evidence. The same empty-stack run proves the first event is
not leaked and the retained hold is reconciled later.

The completed policy-operations slice is committed as Platform `9fb449c`, with
worker-order serialization finalized in `caa719e` and the bounded external
classifier adapter/Provider contract finalized in `7fca582`.
Migration 030 makes Admin rule create/update/delete mutations append an actor/IP
audit row and a durable revision outbox atomically. A hosted worker claims the
outbox with expiry/retry state, publishes the revision and invalidation counter to
Garnet, and exposes protected change history. Runtime block, classifier-unavailable,
and unsupported-evaluator outcomes persist deterministic redacted alert evidence;
Admin exposes filtered alert queries. Host tests cover Garnet success and retry,
and `scalaapi-classifier-20260809d` proves empty-stack propagation, classifier
match/outage semantics, and alert queries. This closes the single-instance
operations and source-owned adapter package, not the production classifier,
multi-instance ordering, browser, or collector/dashboard gates.

Provider OAuth credentials now use encrypted versioned state with a single-account
refresh lease, compare-and-set completion, bounded error evidence, and scheduler
backoff. Regular dispatch recovery and media polling share one HTTPS-by-default,
bounded refresh-token adapter before Provider contact. Admin can create static or
OAuth accounts and inspect refresh health without reading stored secrets. Grain and
Host and Provider Mock HTTP contract tests prove rotation, concurrency exclusion,
failure state, strict form exchange, revoked/malformed/oversized response handling,
and error redaction; the empty-stack gate proves an expired credential rotates
before a billable dispatch. Provider-specific parameters, refresh audit history,
and multi-Silo contention remain open.
The current identity slice adds migration `022-auth-totp-state.sql`: TOTP
time-step replay, backup-code consumption, five-failure lockout, and lockout
recovery are transactionally tested across two service instances. User Web now
exposes password recovery, email verification, and TOTP setup/verify/disable
screens; browser evidence, backup-code sign-in UX, delivery providers, and the
remaining identity providers are still open.
The `AUTH-02` session slice now has PostgreSQL-backed single-use refresh rotation:
the old refresh hash is locked and revoked before one replacement is committed,
concurrent callers have one winner, replay is rejected, and JWT validation checks
the active session row. The `scalaapi-auth-session-verified` smoke proves login,
rotation, old-token rejection, replacement acceptance, and logout invalidation;
multi-device/browser session management, audit/retention, and hosted-CI evidence
remain open.
The `AUTH-01` registration/login slice now validates normalized email, bounded
password, and display-name input before database or BCrypt work, maps concurrent
duplicate registration to 409, and persists hash-only email/IP abuse counters in
PostgreSQL. Five failed logins lock one identity for 15 minutes (with a higher
shared-IP ceiling), ten invalid registrations lock one IP for an hour, success
clears the relevant counters, and 429 responses include `Retry-After`. Migration
`026-auth-abuse-counters.sql`, five real PostgreSQL Admin tests, schema coverage,
and the `scalaapi-auth-abuse-verified3` empty-stack assertions (400, five 401s,
then 429) are complete. The domain remains `partial` until browser/email
notification, anti-enumeration, and broader public-endpoint limits are covered.
Migration `023-auth-oauth-states.sql` now adds one-time OAuth state with S256 PKCE:
the Admin start flow returns provider-bound state/verifier/challenge material,
PostgreSQL stores only hashes, and callback consumption binds the exact redirect URI
and rejects replay or expiry before any upstream token exchange. Platform `c029b3c`
adds configurable authorization/token/user endpoints and a source-owned Provider
mock that binds one-time authorization codes to client, redirect, and S256 verifier;
the `scalaapi-oauth-20260809b` gate proves account creation and replay rejection.
Redirect allowlists, account-link collision policy, and browser UX remain release
work.
The first User Web slice now provides a standalone refresh-aware Solid client:
registration/password login, PKCE callback, dashboard balance/recent usage,
user-scoped usage history, API keys, billing/subscriptions, profile, and password
change, password recovery, email verification, and TOTP setup/verification/disable
with backup-code display. Billing lists active plans, purchases/cancels/renews
subscriptions, redeems promotion codes, and generates a referral code with summary
totals. The API adds user-scoped usage and balance reads and the Compose stack
serves User Web separately from Admin Web. Browser automation, backup-code sign-in
UX, real payment checkout, referral reward settlement, and commercial audit remain
open.
The `CORE-03`/`CORE-05` scheduling slice at Platform `c2d3cf9` persists group RPM
windows in Orleans state, resolves exact model routes before longest-prefix and
wildcard patterns, applies overnight peak multipliers to the primary dispatch,
and walks active multi-level fallback chains with cycle protection. Existing sticky
bindings, capability filtering, priority/load ordering, account and user
concurrency, and idempotent group spend remain covered by the Grain suite; HTTP
group CRUD validation, distributed rate-window contention, and multi-Silo fallback
fault evidence remain release work.

The `SEC-01` runtime slice is active at Gateway `12bf8f1` and Platform `7fca582`.
The canonical dispatch contract carries bounded request and response content.
Platform applies request `log`/`block` rules before scheduler/lease activity and
response rules after Provider validation but before non-stream delivery. Both stages
write rule-linked audits; request blocks create no lease, while response blocks hide
the Provider body, preserve one normal usage debit, and replay the client-facing
400. Migration 029 adds the versioned `unicode-confusable-v1` evaluator, classifier
selection, policy revision state, and audit redaction metadata. Gateway
event-boundary streaming enforcement is source-tested and empty-stack verified;
the classifier boundary uses a source-owned HTTP contract with explicit JSON fields,
bounded request/response bytes, timeout control, and deterministic fail-closed
status/schema/transport mappings. Migration 030 now provides durable single-instance
change propagation and alert evidence. The domain remains `partial` until a
production provider, multi-instance ordering, browser authorization, protocol
golden fixtures, and long-stream metrics are automated.

## Next implementation slice

1. Complete the classifier release boundary. Keep the source-owned adapter and mock
   contract plus the `12bf8f1` protocol goldens as deterministic CI paths; add a
   production provider adapter with measured latency budgets and prove
   bounded-buffer overflow, cancellation, and late usage settlement under every
   stream terminal event.
2. Extend policy revision propagation and operations to multiple Platform/Gateway
   instances. Verify ordered outbox claims, Garnet invalidation convergence,
   monotonic revisions under concurrent rule changes, and alert correlation after
   worker failure or Garnet outage. The single-instance outbox, retry, and alert
   evidence is complete in `7fca582`.
3. Add operator and browser evidence. Exercise Admin rule management with fresh
   identity, authorization, audit, redaction, and replay checks; add Admin/User Web
   browser tests for policy management and the user-visible 400/503 policy error
   contracts.
4. Close release reliability gates in parallel: Provider golden request/response
   fixtures, long WebSocket/backpressure soak, multi-Gateway/multi-Silo contention,
   Garnet TLS/outage/rebuild, PostgreSQL/Garnet recovery, and backup/restore drills.
   Every scenario must run from empty volumes or an explicitly created fixture and
   must make the top-level command non-zero on failure.

Exit for this stage: streaming and non-stream policy decisions are deterministic,
audited, fail closed, and replay-safe; all current 58 inventory domains have an
API/state machine, automated test, and source-built runtime evidence for their
claimed status. This stage contains no compatibility, cutover, dual-write, CDC,
snapshot import, old-key import, ID preservation, status mapping, or business-data
migration work.

## Objective

Close one production-shaped, billable OpenAI Chat vertical slice under cancellation,
partial output, process crashes, cache loss, and multi-instance operation:

```text
new user/session/key/group/account
  -> Gateway JSON or SSE
  -> Platform schedule/lease/hold
  -> Provider adapter/mock
  -> usage report and idempotent settlement
  -> PostgreSQL ledger/reconciliation
  -> Admin query and operational evidence
```

The slice remains `partial` until every exit scenario below is automated and a
failed assertion makes the top-level command non-zero.

## Work package 1: reconciliation and exact-boundary recovery

Accounting authority completed at `c15b53b`, reconciliation foundation at
`fddba62`, dispatch evidence at `6bfb974`/`84634d1`, audited resolution at
`0559659`, and deterministic fault boundaries at `1cad5b7`/`30b8c2b`/`8c3d2e0`,
with current streaming/empty-stack evidence in Gateway `b27965f` and Platform
`c029b3c`:

- Added one per-user `accounting_accounts` authority with NUMERIC posted balance
  and monotonically increasing ledger version.
- Routed administrative adjustments, payment credits/refunds, redeem bonuses, and
  usage debits through one per-user SQL serialization and stable effect contract.
- Moved hold reservation, availability checks, completion, abort, and TTL handling
  into the SQL authority; Grain no longer owns money or permits dispatch.
- Added versioned Grain snapshots, latest-only projection outbox, retry worker, and
  backlog/retry metrics. Stale snapshots cannot regress a newer balance.
- Proved 20 concurrent versions, replay/conflict, hold oversubscription, protected
  debit, account/ledger equality, projection drain, migration idempotency, service
  replacement, and isolated Provider fault accounting.
- Replaced unsafe ambiguous TTL release with `reconciliation_needed`, an active
  hold, blocked matching redispatch, a reconciliation outbox event, and exactly-once
  late usage completion; dispatch evidence now narrows safe expiry to `held` only.
- Added globally serialized scheduled/manual reconciliation of account balance and
  version, ledger contiguity, usage/debit equality, lease/hold state, and Grain
  projection. Safe terminal-hold and stale-projection drift is repaired; every
  unknown charge or unsafe mismatch is a durable incident.
- Added protected run/incident APIs and metrics for open count, unknown-charge count,
  oldest age, and last successful run. Real PostgreSQL tests prove repair,
  persistence, late settlement, and later incident resolution.
- Added the strict `held -> forwarded -> output_started -> completed` state machine,
  terminal `aborted`/`expired`/`reconciliation_needed` branches, and an immutable
  transition-event journal. Evidence writes and terminal writes are idempotent.
- Gateway persists `forwarded` before HTTP/realtime transport and reports the first
  successful streaming client write. Failure to persist forwarding evidence fails
  closed before Provider contact.
- Restricted no-charge release and failover to actual Provider 4xx/5xx responses.
  Transport loss, synthesized errors, malformed success, conversion failure, and
  media persistence ambiguity retain the hold and do not fail over.
- Proved migration 020 idempotency, safe never-forwarded expiry, retained unknown
  aborts, late exactly-once settlement, and a source-built fault matrix with three
  intentional unknown-charge incidents.
- Added migration 021 and a native resolution contract. `settle` validates bounded
  usage/evidence and calls the same completion transaction as normal Provider
  usage; `release` accepts only `never_forwarded`, `provider_rejection`, or
  `provider_confirmed_no_charge` evidence, checks the lease journal, releases the
  hold, and records no usage. Both actions lock incident/lease/account state,
  persist a resolution row, operator lease event, and actor audit, and use a global
  idempotency key plus request fingerprint for replay/conflict behavior.
- Added an Admin API endpoint and token-protected Platform internal bridge. A real
  PostgreSQL Host test covers settle/release atomicity, one debit/hold transition,
  invalid evidence, same-key conflict, and concurrent different-key serialization;
  source smoke settles one incident, replays it as `duplicate`, and verifies the
  next reconciliation preserves the decision.

Implemented in this package:

- Added Gateway and Platform one-shot, marker-backed fault hooks before/after
  Provider dispatch, after Provider completion, before/after settlement commit,
  after outbox claim, and before outbox acknowledgement. Unit tests prove exact
  hook matching, one-shot claims, and repeat mode; the empty-stack recovery harness now waits
  for post-commit, after-claim, and pre-ack process termination, restarts the same container,
  and proves the durable outbox produces one terminal debit. The
  `scalaapi-gateway-recovery-0907` source smoke additionally terminates Gateway
  after Provider completion, explicitly starts the same container, and proves the
  original lease remains reconcilable with no usage/debit or repeat crash.
- The `scalaapi-gateway-dispatch-recovery-0911` source smoke terminates Gateway
  before Provider dispatch, explicitly starts the same container, and proves
  safe `held -> expired` cleanup with released hold/idempotency and no incident.
- The `scalaapi-platform-dispatch-recovery-0912` source smoke terminates Platform
  after creating the SQL lease/hold but before returning the dispatch target,
  explicitly starts the same container, and proves the same safe `held -> expired`
  cleanup with released hold/idempotency and no incident. Its failure probe uses
  `curl -f` so a Gateway-wrapped HTTP error cannot be mistaken for success.
- The `scalaapi-platform-worker-recovery-0913` source smoke terminates Platform
  after claiming the completed settlement outbox but before any Grain side effect,
  explicitly starts the same container, and proves the expired claim is reclaimed
  and applied once with no duplicate financial effect.
- The `scalaapi-platform-dispatch-retry-0914` source smoke terminates Platform
  after the lease/hold commit. Gateway retries the same request and the replacement
  Platform rebuilds the active lease target; the request settles one lease, usage
  event, usage log, and NUMERIC debit. The full matrix passes. The smoke uses a
  temporary runtime image assembled from the verified local Gateway build because
  the pinned Photon commit is fetched with shallow checkout disabled because the
  upstream does not advertise it on a discoverable ref.
- Added explicit `Orleans:SingleSiloRecovery` for the development smoke path and
  a Podman-compatible harness restart. The source smoke proved
  `platform.before_settlement_commit`, `platform.after_settlement_commit`, and
  `platform.before_outbox_ack` crashes, durable usage replay, and exactly one
  usage debit; the Podman harness starts an exited container explicitly before
  waiting for settlement.

Earlier reliability follow-up (now covered by the current gate):

- Exercise every remaining hook independently with replay assertions for duplicate
  completion, abort, expiry, projection replacement, and process restart. Platform
  dispatch retry and active-lease recovery are proven for regular Chat, while
  Gateway source tests and the full-stack smoke cover the same policy for realtime.
  Long-connection/backpressure soak, replay-after-restart, remaining Gateway crash
  boundaries, and multi-silo recovery are carried into the release gates below.

Remaining package deliverables:

- Define authority contracts before adding subscription grants, affiliate rebates,
  or any new monetary effect. They must use the same account/version API and cannot
  write `balance_ledger` directly.
- Add a blocking negative probe for each new fault hook so a swallowed child failure or
  missing scenario makes the top-level gate non-zero.

Dependencies: migrations 018-024 accounting authority/reconciliation/evidence,
TOTP abuse state, and OAuth PKCE state,
versioned ledger effects, durable holds, response replay, settlement/projection
outboxes, persisted incident identity, and the audited resolution transaction.

Exit: every injected crash converges after restart to one terminal lease or one
documented `reconciliation_needed` lease, at most one usage debit, no unaccounted
hold, and a durable operator-visible reason when the Provider charge is unknowable;
an open incident can be resolved only through the audited settle/release contract.

## Work package 2: cancellation and streaming failure semantics

Progress in Gateway `1d03130` and Platform `c029b3c`: the streaming pipe now requires a source protocol
terminal event before treating Provider EOF as complete, classifies timeout/EOF as
incomplete (including Photon incomplete chunked-body `-1/errno=0`), treats
zero/error client writes as cancellation, records bounded
disconnect/cancellation reasons, and prevents Gateway failover or normal usage
settlement for ambiguous partial streams. The same pipe now buffers each bounded
SSE event for response policy approval and emits a protocol-shaped terminal policy
error without leaking blocked data. These behaviors are covered by 117 Gateway
CTest cases. Platform smoke proves Provider disconnect, disconnect-before-output,
malformed-usage, invalid content type, downstream client cancellation, and streaming
429/500 rejection outcomes with the expected hold/debit behavior. Exact
`text/event-stream` media type validation rejects JSON or lookalike media types
before client output and retains the authorized hold as unknown charge.
The public Provider availability contract and distinct inter-chunk/total timer
contract are now closed for direct and zero-output
resets: Gateway returns `503/provider_unavailable`, dispatch wait exhaustion uses the
same body, and bounded timeout/malformed protocol cases remain `502/provider_protocol_error`.
Final-usage settlement after a truncated stream is now proven through Platform. Actual downstream client socket cancellation
is now proven from an empty stack: the Provider emits one SSE event, a short-lived
client closes before the delayed second write, and the lease remains
  `reconciliation_needed` with its hold and idempotency key retained. A no-header
  Provider timeout is bounded by the first-token deadline and returns a non-empty
  502/provider_protocol_error response; the incoming client socket is extended for
  the configured streaming window.

Deliverables:

- Keep the normalized direct-reset and dispatch-exhaustion `503/provider_unavailable`
  contract stable while adding protocol-wide golden fixtures. The independent
  inter-chunk/total timer tests pass; freeze the retryable/non-retryable mapping
  before extending adapters.
- Propagate client cancellation through the HTTP/SSE transport and stop retrying as
  soon as any response bytes have reached the client. Gateway source behavior,
  stack-level socket/reconciliation evidence, and usage-before-EOF settlement now
  pass; add replay-after-restart assertions for the truncated-stream outbox.
- Cancellation before `forwarded` evidence may expire/abort and release without a
  usage debit. Once Provider transport has been authorized, absence of client output
  does not prove no charge: continue collecting final usage when possible or enter
  `reconciliation_needed`. After `output_started`, retries are always forbidden.
- Extend the isolated fault accounts to SSE: Provider disconnect before first event,
  disconnect after partial output, malformed usage, invalid content type, and established-SSE status
  retention now pass in the empty-stack gate, as do streaming 429/500 no-charge
  rejections. The no-header timeout and separate inter-chunk/total stream timers are
  now covered; usage-before-EOF behavior is covered by the late-usage profile. Add
  protocol-wide assertions for the remaining adapters. Actual client disconnect and
  invalid content type are covered by the current fault matrix.
- Add bounded-buffer/backpressure assertions and verify that partial output cannot
  be replayed as a complete response or retried against another account.

Dependencies: package 1 terminal/reconciliation states.

Exit: JSON and SSE fault matrices specify response status, retry count, output-start
state, terminal lease, hold, usage, debit, idempotency, and reconciliation outcome.

## Work package 3: Provider and protocol contract fixtures

The generic Provider OAuth runtime and user-login exchange are complete at
Platform `c029b3c`: the source-owned mock endpoint, real HTTP Platform-client
contract tests, and `scalaapi-oauth-refresh-20260809` empty-stack assertion prove
an expired encrypted credential rotates before dispatch, while the
`scalaapi-oauth-20260809b` gate proves authorization-code exchange, exact S256
binding, account creation, and replay rejection. The remaining work below is
provider fidelity and release evidence, not a compatibility layer.

The OpenAI Embeddings contract slice is also closed for this checkpoint. Gateway
`6243b2d` bounds input cardinality and dimensions, validates successful float and
base64 response shape and usage, and retains an unknown-charge lease for a
malformed Provider payload. Platform `c029b3c` provides deterministic dimension,
encoding, usage, and fault profiles with HTTP tests; the
`scalaapi-embeddings-20260809b` empty-stack gate proves two float vectors, one
base64 vector, NUMERIC settlement, and `502/provider_protocol_error` reconciliation.
Provider-specific dimensions, tokenizers, and versioned golden fixtures remain
before GW-07 can become `implemented`.

The model catalog/token-count contract slice is closed for this checkpoint at
Gateway `b27965f` and Platform `c029b3c`: OpenAI entries are unique and complete,
Gemini list/detail metadata carries supported methods and positive token limits,
and Anthropic count tokens requires a bounded positive `input_tokens`. The mock
and source tests cover malformed, duplicate, and zero-count failures. Provider-
specific catalog authority, tokenizers, golden fixtures, and provider-group E2E
remain before GW-06 can become `implemented`.

The direct non-stream OpenAI Responses contract is now fail-closed at Gateway
`b27965f`: a successful Provider payload must carry completed response metadata,
typed output items, and consistent positive usage before normal settlement. The
Gateway retains the lease for malformed envelopes. Gateway `12bf8f1` now freezes
matching Responses request/response/stream fixtures; subresource lifecycle and
cross-provider runtime E2E remain before GW-03 is `implemented`.

CORE-06 now has a native configuration contract at Platform `c029b3c`: bounded
runtime settings, secret rejection, boolean feature flags, independent snapshots,
optimistic version checks, and actor/IP audit persistence are implemented. Dynamic
consumers, reload propagation, and browser controls remain before the domain can
become `implemented`.

Deliverables:

- Add explicit OpenAI/Anthropic/Gemini token parameter profiles and provider
  revocation/rotation behavior; never introduce legacy credential-map conventions.
- The secret-free refresh attempt/audit history and generic timeout,
  malformed/oversized response, and revoked-grant profiles are complete. Add
  provider-specific revocation/rotation profiles and test multi-Silo lease
  contention and refresh failure recovery.
- Gateway `12bf8f1` freezes versioned OpenAI Chat/Responses, Anthropic Messages,
  and Gemini request/response/SSE/error fixtures, and tests parser normalization,
  usage/terminal events, response validation, and cross-protocol conversion.
  Add provider-specific catalog/tokenizer fixtures and live adapter evidence.
- Use the frozen goldens to complete same-protocol and cross-protocol normalization
  matrices at the Gateway boundary, including provider-specific errors and headers.
- Validate request IDs, idempotency fingerprints, Provider status mapping, proxy/TLS
  headers, response limits, and malformed payload rejection.
- Keep the revision-3 Cap'n Proto schema greenfield. Contract changes update the
  canonical Platform source, generated C#, Gateway vendor copy, and both digest
  gates as one coordinated release change; no deprecated compatibility fields are
  added.
- Extend SEC-01 from the proven pre-dispatch substring contract to normalized
  Unicode fixtures, response-side enforcement before client delivery, optional
  classifier adapters, rule-change propagation, alerting, and browser-authorized
  operator workflows. Every block must prove zero Provider contact or a defined
  post-response settlement outcome.

Dependencies: package 2 defines terminal streaming behavior; the generic OAuth
  transport and mock fixture are now available.

Exit: the source-owned fixtures remain deterministic and run without external
Providers; each supported provider profile has a versioned request/response/error
fixture, while provider-specific live adapters, refresh audit, and multi-Silo
evidence are separately proven before promotion to `implemented`.

## Current P0 slice: API-key authorization boundary

Platform `c029b3c` now treats API-key policy as a new product contract. A key
stores a normalized set of Gateway capability scopes (`messages`, `responses`,
`embeddings`, media, realtime, and the provider-specific model capabilities) and
an optional millisecond expiry. Platform checks the requested capability after
authentication but before idempotency, scheduler selection, account concurrency,
or balance-hold creation. Expiry is classified separately from an unknown key;
media control operations resolve their durable operation type before applying the
matching scope.

Admin and user create/update/rotate/revoke paths write the same scope/expiry
projection and append actor, action, scope, and reason data to the new
`api_key_audit_events` table. Runtime scope denials append a bounded audit event
with request ID and never persist plaintext credentials. The 66-case Grain suite,
schema assertion, Release build, full 156-test Platform run, and
`scalaapi-key-policy-verified` empty-stack proof pass. The smoke proves that
denied requests create no lease, hold, or Provider call and that the policy
denial audit row is persisted. The source smoke now also proves two concurrent
Chat requests with one idempotency key leave one completed lease/idempotency row,
that a short-lived key returns HTTP 401 before scheduling after expiry, and that
an authenticated Admin audit query returns the denied event without key material.
The lifecycle smoke `scalaapi-api-key-lifecycle-verified` now covers Admin
ownership-safe update/revoke, updated-key dispatch, revoked-key rejection, and
user self-service rotation with database state and audit invariants. The slice
remains `partial` until multi-instance contention and browser cases are covered.

Exit: one Admin create/update/revoke flow and one user rotate flow are exercised
against a fresh PostgreSQL/Garnet stack; a scoped key is allowed for exactly one
capability and rejected for another before scheduling; an expired key returns the
expired error; repeated and concurrent policy commands are serialized; and the
audit query shows actor, action, scope, expiry, capability, and request ID without
key material.

## Work package 4: Garnet and cluster resilience

Deliverables:

- Run at least two Gateway instances and two Orleans Silos against one authenticated
  Garnet service and PostgreSQL authority.
- Add Garnet TLS 1.2/1.3 tests with certificate-name validation, concurrent clients,
  password rejection, flush, stale invalidation version, restart, and projection
  rebuild.
- Prove cache loss fails new rate-sensitive dispatch closed but does not block usage
  settlement, hold recovery, or outbox drain.
- Exercise Silo removal, Gateway rolling replacement, and concurrent requests for
  the same API key/idempotency key without duplicate account leases or charges.
- Keep all cache keys under the documented `scalaapi:v1` namespace with owner,
  schema, TTL, and rebuild source recorded.

Dependencies: package 1 PostgreSQL authority and recovery.

Exit: multi-instance flush/outage/restart tests pass with zero cache-as-authority
behavior and no Redis process, package, image, CLI, or embedded fallback.

## Work package 5: blocking release workflow and operability

Deliverables:

- Give hosted CI a read-only sibling-repository checkout credential or move the
  cross-repository gate into a dedicated release repository. Run the exact
  `deploy/stack/smoke.sh` entry point from empty volumes.
- Record both commit IDs, source-built image IDs/digests, migration checksums,
  environment shape, scenario names, and top-level exit code.
- Emit structured correlation for client request, internal retry/lease,
  idempotency, account, Provider request, usage, and ledger effect IDs without
  logging secrets or raw API keys.
- Add metrics and alerts for active/aged holds, reconciliation-needed leases,
  settlement retry age, Gateway outbox backlog, Garnet readiness, Provider fault
  rate, and ledger mismatch.
- Retain benchmark integrity checks: zero selected benchmarks or any failed child
  process must return non-zero. Performance claims require a separate measured run.

Dependencies: packages 1-4 expose the states and scenarios to observe.

Exit: one hosted, blocking workflow recreates the local evidence and intentionally
failing probes demonstrate that migration, fixture, benchmark, and fault-scenario
failures all fail CI.

## Required acceptance matrix

Each scenario records client result, retries/accounts used, output-start state,
lease transitions, hold state, usage events/logs, request log, ledger effects,
idempotency state, outbox backlog, and reconciliation status:

1. JSON success and settled exact replay.
2. SSE success with complete usage.
3. Same key while active and same key with a conflicting fingerprint.
4. Provider 429 and 500 exhaustion.
5. Malformed usage and malformed/truncated successful JSON.
6. Timeout and upstream disconnect before output.
7. Upstream disconnect after partial SSE output.
8. Client disconnect before and after Provider output.
9. Gateway crash at dispatch and usage-report boundaries.
10. Platform crash around settlement commit and outbox acknowledgement.
11. Garnet flush, outage, TLS failure, and rebuild with concurrent Gateways.
12. Silo removal and rolling Gateway/Platform replacement.
13. Scope-aware content log/block, rule update, oversized input, response policy,
    audit correlation, and zero-lease pre-dispatch rejection.

## Sequence and commit discipline

1. Finish package 1 hook-matrix and recovery tests first; dispatch evidence, the
   authoritative unknown-charge state, audited incident decisions, and one
   pre-commit crash replay now exist. Provider-side streaming cancellation semantics
   and actual client cancellation are source- and empty-stack-tested, but cancellation
   cannot be release-complete without the public error contract, final-usage replay,
   every deterministic boundary, and multi-instance recovery.
2. Package 2 defines transport semantics; package 3 (`12bf8f1`) freezes the
   source-owned protocol fixtures and keeps them independent of external Providers.
3. Package 4 runs the state machines under concurrency and infrastructure failure.
4. Package 5 makes the same evidence mandatory in hosted release CI.

Implement each independently verifiable functional point in its owning repository,
with focused tests and a detailed commit message describing contract, failure
semantics, and evidence. Update `current-state.md`, `verification.md`, the affected
inventory acceptance row, and this checkpoint after each completed package.

## Stage exit and following expansion

The stage exits only when all 12 scenarios pass from an empty environment locally
and in hosted CI, all monetary invariants reconcile, and OpenAI Chat can be promoted
using the inventory's contract/test/runtime rule.

Then expand the remaining 58-domain work in this order:

1. Complete OpenAI Responses subresources, Embeddings/Images/video/realtime,
   Anthropic Messages, Gemini generation, model catalogue/token counting, and
   runtime cross-protocol E2E; source-owned protocol fixtures are frozen in
   Gateway `12bf8f1`.
2. Complete Provider-specific OAuth refresh profiles and runtime evidence, price/quota adapters, media recovery,
   and object reconciliation/restore.
3. Complete identity hardening beyond the TOTP and OAuth PKCE state machines, Passkeys,
   backup-code recovery UX, mail delivery, and browser tests for the new User Web/API-key/usage/order/
   subscription flows.
4. Complete payment adapters/reconciliation/refunds, subscription workers, redeem,
   affiliate, notification, and commercial audit flows.
5. Complete policy/security, observability, multi-region/HA, load and long-connection
   soak, backup/restore, signed updates, and rollback drills.

Every later domain uses new ScalaAPI contracts and clean seed data. Sub2API remains
read-only research material and is never an acceptance oracle for compatibility.
