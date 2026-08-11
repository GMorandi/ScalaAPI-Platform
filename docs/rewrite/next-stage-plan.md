# ScalaAPI Next Stage Plan

Current checkpoint override: Platform/Admin Web/User Web `024b215`, Gateway
`418da3a`, and read-only `sub2api@43ec48d`. The latest gate is
`scalaapi-provider-fidelity-0811j`; it passed durable image batch list/items,
Provider-backed cancellation, S3-backed ZIP download with manifest/error entries,
owner-scoped per-item objects and signed downloads, retention cleanup, Platform
restart recovery, fenced item integrity verification, one-fetch archive/item
creation, exact claims across two live Silos, MinIO outage/restart recovery,
force-replacement with volume preservation, and the full empty-volume matrix. The
same run stopped MinIO during retention, persisted retry evidence without changing
completed billing, restarted storage, and cleared parent/item metadata. Focused
tests prove partial-PUT deterministic-key convergence and mid-sequence DELETE
replay. A smoke-only private TCP proxy additionally reset a signed PUT after 16
request-body bytes and discarded a 200 response after MinIO committed another
PUT. Both retries preserved deterministic item/archive keys and exactly-once
settlement. The latest gate also disconnected only the secondary Silo from
object storage and PostgreSQL using a rootless private network. It proved one
fenced object-storage failure and recovery, then due PostgreSQL work surviving
the partition with `stored|completed|committed`, one usage event, and one debit
after rejoin. The same run also passed the new Anthropic/Gemini Provider-specific
fault matrix and final reconciliation/operator gate. The remaining plan below
starts after this completed protocol-fidelity boundary.

The Provider-specific fault slice is implemented at Platform `024b215`.
Independent groups cover Anthropic Messages and Gemini generation 429, 500,
malformed JSON/SSE, timeout, disconnect, and usage-before-EOF profiles.
Provider.Mock tests pass 69/69. The empty-stack gate routes ten new JSON/SSE
requests through Gateway -> Cap'n Proto -> Platform: explicit 429/500 responses
release the hold without usage/debit, while malformed, timeout, and disconnect
retain one unknown-charge lease/hold each. Live upstream adapters,
provider-specific pricing/tokenizers, and longer load/backpressure remain
next-stage work.

## Immediate execution order after `024b215`

1. Complete provider-native adapter boundaries for Anthropic and Gemini:
   version/auth/beta headers, base-URL and method selection, bounded response
   bodies, retry/cancellation classification, and secret-free errors. Keep every
   contract source-owned; do not add Sub2API headers, keys, routes, or fallback
   mappings.
2. Extend the current runtime matrix with provider-native cancellation,
   usage-before-EOF settlement, invalid content type, retry exhaustion, and
   credential refresh/revocation for both protocols. Every case must assert
   request/idempotency identity, lease journal, hold state, usage/log cardinality,
   NUMERIC debit, and reconciliation outcome.
3. Finish the remaining P0 protocol lifecycle gaps: OpenAI Responses mutation
   subresources and the complete video create/poll/cancel/delete/object-retention
   state machine. Reuse the same lease/accounting and S3-compatible lifecycle;
   no compatibility storage or legacy status mapping is allowed.
4. Run the documented 3600-second two-Silo media contention/rejoin gate, then add
   longer realtime/provider backpressure and process-replacement soak evidence.
5. Make the source-built Gateway + Platform empty-volume gate blocking in hosted
   CI after credentials for the private sibling checkout are available.

Dependencies: the revision-3 Cap'n Proto contract, PostgreSQL accounting
authority, Garnet projections, Provider mock, and the current incident/operator
workflow remain fixed foundations. Any contract change updates both repositories
and digest/generation gates in one release.

### Provider-native credential slice investigated after `024b215`

Repository evidence shows that Platform currently decrypts the account credential
dictionary and copies it verbatim to Cap'n Proto `authHeaders`; Gateway then inserts
every target pair into the upstream request. The seeded semantic key `api_key`
therefore does not authenticate either native Provider. Existing mock endpoints do
not validate authentication, so the passing runtime gate cannot close this gap.
Sub2API was inspected only as a requirements catalogue; none of its header aliases,
fallback keys, routes, or stored state are accepted as a compatibility contract.

Implement this slice in the following order:

1. Add one Platform credential compiler with case-insensitive platform
   normalization, bounded material, CR/LF rejection, deterministic header names,
   collision rejection, and secret-free error codes. Static `api_key` is required
   for native Anthropic and Gemini accounts. Anthropic uses `x-api-key` plus
   `anthropic-version` (default `2023-06-01`) and an optional bounded
   `anthropic-beta`; Gemini uses `x-goog-api-key`. No key is placed in a query,
   path, log, metric, or error.
2. Use the same compiler for initial dispatch, active-lease recovery, media polling,
   and Provider cancellation by compiling during credential hydration. Preserve
   OAuth rotation as an explicit header-name/scheme contract, and reject collisions
   between refreshed OAuth material and compiled static material.
3. Validate Cap'n Proto target auth headers in Gateway before creating an HTTP
   operation: bounded count/name/value, no hop-by-hop or routing headers, no
   duplicates, and only Platform-produced authentication may reach upstream.
   Inbound `Authorization` and `x-api-key` remain client authentication and must not
   override the target.
4. Make Provider mock Anthropic Messages/count-tokens and Gemini
   models/generate/stream routes validate exact native headers. Add negative HTTP
   contracts for absent/wrong key, version, and leaked generic `api_key`; keep
   failures secret-free.
5. Extend the empty-volume smoke with successful Anthropic JSON/SSE/count-tokens and
   Gemini models/JSON/SSE through the new header checks, then assert the existing
   exactly-once lease/hold/usage/debit outcomes and that Provider 401 rejection is
   no-charge. Run the complete existing matrix before promotion.

Exit requires source tests in Grains, Host serialization, Gateway forwarding, and
Provider.Mock plus a current-source empty-stack run. The successful run must prove
the native methods and escaped paths already selected by Platform, exact header
delivery, no client-key override, bounded secret-free failures, and unchanged
terminal accounting. This closes only the native credential transport slice;
provider-owned pricing/tokenizers, live external credentials, and longer soak remain.

Exit: provider-native fault/cancellation/refresh matrices pass for OpenAI,
Anthropic, and Gemini; Responses and video lifecycles have API/state-machine,
automated-test, and empty-stack evidence; the one-hour media and long-connection
soaks finish without duplicate effects or leaked holds; hosted CI fails on any
fixture, benchmark, migration, or smoke subscenario failure.

The Embeddings provider-profile slice is complete for this checkpoint. Its
source-owned OpenAI-compatible, Jina-compatible, and Gemini-compatible models
have bounded dimensions, deterministic token accounting, catalog and golden
fixtures, Provider HTTP contracts, and four-request empty-stack settlement
evidence. GW-07 remains `partial` until live adapter and provider-specific
production fidelity evidence is added.

The image batch list/items, cancellation, aged object-orphan cleanup, batch
download archive slices are also complete for this checkpoint. Gateway
`418da3a` sends the collection read to Platform and normalizes item responses;
Platform `1cc4538` carries the API-key isolation and bounded newest-first reads. The
source smoke proves the real batch create, list, item projection, object storage,
and Provider cancel path. Platform `7d0abc6` calls the Provider cancellation
endpoint before the durable state transition; cancellation replay remains
terminally idempotent and retains an unknown-charge hold until reconciliation.
Platform `1d7ec4f` signs and paginates S3-compatible `ListObjectsV2`, compares
the `media/` prefix with PostgreSQL references, and removes only unreferenced
objects older than the configured 60-minute grace period. Orphan cleanup is now
covered by database and HTTP contract tests. Platform `c1bbb4d` creates bounded
batch ZIPs from provider item URLs, writes manifest/error entries, stores the
archive in S3-compatible storage, and returns a signed download URL; Host and
empty-stack smoke cover this path. Platform `0134323` adds an independent
retention deadline and retryable terminal-object cleanup, and `b5586cf` exposes
those windows in Compose. The `scalaapi-media-restart-0810` smoke verifies a
running operation survives a Platform replacement, resumes its PostgreSQL-backed
poll, stores the object, and settles after restart. Platform `d797cb1` adds
migration 048, durable owner-scoped item rows and S3-compatible item objects,
fresh signed item reads, restart projection recovery, orphan protection, and
item-aware retention cleanup. Platform `57d33f8` adds migration 049, fenced
`SKIP LOCKED` claims, per-item signed `HEAD`, missing/mismatched-object repair with
retry, and one-fetch ZIP/item creation while preserving the completed parent lease.
Platform `ee6934c` proves two live Silos increment each forced item/archive attempt
exactly once, outage records retryable parent/item failure, restart repairs both,
and force-replacement preserves the signed bytes and later retention transition.
Platform `10adfb5` proves source-level partial PUT convergence, real-PostgreSQL
partial retention DELETE replay, and runtime retention outage/recovery with the
completed lease and committed hold preserved. Platform `fffc712` then proves real
mid-body request interruption and post-commit response loss converge without
duplicate objects or billing. Rootless single-Silo object-storage/PostgreSQL
partition recovery is now a completed gate; a one-hour worker-contention soak
and deployment-scale HA/offsite lifecycle remain partial.

## Checkpoint

The next stage starts from Platform/Admin Web/User Web `024b215`, Gateway
`418da3a`, and read-only reference `sub2api@43ec48d`.

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
SSE retention with nineteen total unknown-charge incidents, including a malformed
non-stream OpenAI Responses 2xx payload mapped to public `502/provider_error` with
one retained reconciliation hold and no usage/debit. The pre-header timeout now
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
lease/usage/hold/ledger settlement. The source-built `scalaapi-realtime-soak-0810b`
gate also holds four concurrent upgraded connections for three seconds and proves
one terminal financial effect per session; longer backpressure/load soak, remaining
boundaries, the worker/multi-silo matrix, and multi-instance scenarios are still
open.
The provider-group slice is now runtime-proven by
`scalaapi-provider-groups-0810l`: Anthropic count-tokens, Messages JSON/SSE,
and Gemini catalog, JSON/SSE generation pass through the new Gateway -> Platform
-> Provider mock path. Four billable requests settle exactly once; catalog and
token-count controls abort and release without billing. Gateway `7b8b03c` keeps
retryable usage reports in the durable outbox without FIFO starvation, while
Platform `7c4836a` releases terminal account/user slots and adds the seeded
Claude price alias. Provider-specific error/disconnect fixtures, real adapters,
and broader cross-protocol runtime coverage remain open.
The OpenAI Responses root, compact operation, and read/input_items/cancel/delete-subresource runtime
slice is now complete in `scalaapi-responses-input-items-0810b`: JSON and SSE both
pass envelope/terminal/usage validation and settle exactly once, while
`GET /v1/responses/{id}`, `GET /v1/responses/{id}/input_items`, `POST
/v1/responses/{id}/cancel`, and `DELETE /v1/responses/{id}` traverse Gateway to
Platform to Provider. Input-item retrieval preserves the submitted text and is
removed with its response; cancellation is idempotent, retrieval retains the
`cancelled` state, and all four controls release their routing leases without
billing. The same gate sends a malformed
non-stream success through the source-owned Provider mock, maps it to
`502/provider_error`, retains the ambiguous lease for reconciliation, and proves
no usage/debit before the audited resolution pass. The compact operation is now
a source-owned product contract: exact routing, strict input validation,
deterministic JSON/SSE compaction items, Provider fault fixtures, and
exactly-once financial effects are covered. The next Responses work is broader
provider-specific fault coverage, cross-protocol golden/runtime fixtures, and
real adapter evidence; the whole GW-03 domain remains `partial` until its
remaining provider and lifecycle matrix is complete.
The current source-built `scalaapi-openai-moderation-0810e` gate applies and
replays 44 migration records from empty volumes, proves Garnet-authenticated
request routing, staged request/response policy, the versioned Unicode evaluator,
OpenAI Moderation match/unavailable handling, the full Provider fault matrix, media
persistence, audited reconciliation, and exactly-once restart billing. The OpenAI
response match returns HTTP 400 and the upstream-unavailable fixture returns HTTP
503; both redact their audit and retain normal lease/usage/idempotency evidence.
The same run verifies warning/critical alerts, Chat/Embeddings/Realtime, S3 object
persistence, and complete cleanup. Production OpenAI remains HTTPS-only; the smoke
uses an explicit development-only HTTP switch. Gateway `8f33790` additionally
withholds each bounded SSE event until response policy approval, emits an
OpenAI/Anthropic/Gemini-shaped terminal policy error on block or fail-closed
evaluation, and preserves the unknown-charge hold and idempotency evidence.
The current source baseline then adds migration 044: a PostgreSQL
instance/sequence-idempotent snapshot table and hosted retryable flush for the
OpenAI classifier counters. `/metrics` merges persisted snapshots from all
instances with live counters and emits fixed-label unavailable-ratio, bucketed p95,
and configuration-backed breach gauges. Migration 045 atomically updates durable
budget alert state with each snapshot append. A temporary empty PostgreSQL run
applied and replay-skipped all 45 product records; the Compose baseline count is
now 46 including Orleans. The hosted-worker test covers two instances and a
restarted instance with one sequence-one snapshot each. The source-built
`scalaapi-metrics-process-0810f` gate additionally runs two Platform silos and two
Gateway processes, restarts the secondary pair, and proves two persisted instance
snapshots, two requests, two usage events, and two NUMERIC debits exactly once.
Credential rotation/redaction, deployed malformed/oversized/timeout/cancellation
scenarios, and long-stream metrics remain open.

Platform `6ae059b` and `c7bd987` now provide the multi-Gateway shared-idempotency
and controlled secondary Silo/Gateway outage/rejoin evidence. Platform `1f0f9ef`,
`840636e`, and `0acdf9c` add migration 046, an AdminOnly PostgreSQL backup/restore
command boundary, a bilingual Admin page, and a PostgreSQL 17 client image. The
`scalaapi-backup-0810b` empty-volume gate creates a non-empty checksum-verified
artifact, replays the create and restore keys, restores a fresh user into a
separate `platform_restore` database, and cleans all containers. This is a local
isolated-target checkpoint only: S3/offsite storage, encryption/signing, measured
RPO/RTO, TLS ingress, rolling replacement, and rollback recovery remain release
gates.

Platform `a5cb552` adds the first UI-06 public slice: User Web routes for the
Gateway model directory and readiness status plus versioned Terms and Privacy
pages. Platform `ec502a2` adds a source-built Compose-only Chromium gate; project
`scalaapi-public-ui-0810b` passed the live nginx-to-Gateway catalog/readiness
proxies and all four public routes (`1/1`) and removed its containers afterward.
Deployment-specific accessibility scanning, legal-text configuration, and
authenticated User Web workflows remain open.

The completed policy-operations slice is committed as Platform `9fb449c`, with
worker-order serialization finalized in `caa719e` and the bounded external
classifier adapter/Provider contract finalized in `7fca582`.
Migration 030 makes Admin rule create/update/delete mutations append an actor/IP
audit row and a durable revision outbox atomically. A hosted worker claims the
outbox with expiry/retry state, publishes the revision and invalidation counter to
Garnet, and exposes protected change history. Runtime block, classifier-unavailable,
and unsupported-evaluator outcomes persist deterministic redacted alert evidence;
Admin exposes filtered alert queries. Host tests cover Garnet success and retry,
and the earlier classifier checkpoint proves empty-stack propagation, classifier
match/outage semantics, and alert queries. Platform `dcdca5e` adds the optional
OpenAI Moderation adapter and an official-shaped `/v1/moderations` fixture;
Platform `2992964` adds migration 043 and the tested Admin rule normalizer so the
adapter is explicitly selectable and persists redacted audit evidence. Platform
`94e0db8` removes the policy revision TTL and restores the authoritative revision
plus invalidation version through authenticated cache rebuild after Garnet loss. The
latest source smoke at Platform `3da2e29` closes single-instance runtime execution
for both source-owned and OpenAI adapter match/unavailable paths; pricing selection
also prefers an effective administrative quote over a later provider refresh and has
PostgreSQL test evidence. Cross-process policy ordering/failure, browser,
collector/dashboard, credential rotation/redaction, and long-stream metrics remain
release gates.

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
AUTH-05 now adds migration `034-email-delivery-outbox.sql`: password-reset and
email-verification requests persist only AES-GCM protected token material, cancel
superseded pending notifications, and are delivered by a bounded SMTP/filesystem
worker with retry/backoff and terminal status. User Web consumes the action links
directly. Three real PostgreSQL tests cover ciphertext redaction, one-shot delivery,
stale-token suppression, and retry recovery. Live SMTP/provider delivery, browser
receipt, delivery metrics, and broader abuse limits remain, so AUTH-05 is still
`partial`.
The BILL-03/COM-02 quota slice is now closed for this checkpoint at Platform
`ad6ac20`: migration `035-subscription-quota-reservations.sql` adds NUMERIC
subscription reservations and lease ownership. The dispatch transaction locks the
active subscription row, reserves the maximum lease hold, returns the existing
`quotaExhausted` protocol rejection on concurrent over-allocation, and consumes or
releases the reservation in the same transaction as usage settlement, safe abort,
expiry, or operator release. A real PostgreSQL test proves two concurrent 0.60 USD
requests against a 1.00 USD grant produce one reservation, then proves settlement
and no-charge release. User Web now displays reserved and remaining quota. This
does not close BILL-03/COM-02: rolling-window cross-protocol enforcement,
payment-provider coupling, quota reconciliation,
multi-Silo evidence, and browser workflows remain the next commercial gates.
Platform `e05ed40` adds the missing internal lifecycle worker: due rows are claimed
with `SKIP LOCKED`, non-renewing subscriptions expire, internal grants reset only
after reservations drain, stale `expired` auto-renew rows recover, and held rows
become `past_due` until terminal lease evidence exists. Deterministic subscription
events and a concurrent PostgreSQL test make the transition replay-safe. External
payment confirmation and quota reconciliation remain separate gates.
Platform `d71fe8b` adds the BILL-02 source boundary: migration 036 records provider,
source-model, and checksum metadata; the bounded catalog adapter validates fixed-scale
decimal quotes over HTTPS by default; changed snapshots close only the prior open
version for that provider/model; and identical snapshots replay without new rows.
Provider Mock plus real PostgreSQL tests prove the contract and history. Provider-
specific price rules, tokenizer/golden fixtures, and multi-provider runtime E2E remain.
Platform `44d2096` adds migration 037 and the first media object integrity boundary:
a `SKIP LOCKED` worker verifies signed object `HEAD` existence, size, and ETag for
stored media, retries missing/mismatched/transient metadata, and restores a row to
`stored` after the object becomes valid without changing the settled operation or
lease. Platform `1d7ec4f` adds signed paginated listing and a `media/` orphan
pass with a one-hour grace period and PostgreSQL reference protection. Platform
`0134323` adds independent retention deadlines and retryable terminal-object
deletion; `b5586cf` proves running-operation restart recovery. Platform `d797cb1`
adds durable item object ownership, fresh owner-scoped signed reads, projection
recovery, orphan protection, and item-aware retention. Platform `57d33f8` adds
fenced per-item verification/repair and a single-fetch archive/item pipeline.
Platform `ee6934c` adds real two-Silo claim contention, object-store outage/restart,
and force-replacement with volume persistence. Platform `10adfb5` adds
deterministic-key partial-PUT convergence plus partial/runtime retention DELETE
recovery while preserving terminal accounting. Platform `fffc712` adds real
mid-body request interruption and committed-response-loss recovery. Partition
handling, a one-hour contention soak, and deployment-scale MinIO HA/offsite
evidence remain explicit follow-on gates.

The completed bounded media slice was batch listing, item projection, and
Provider-backed cancellation. The read-only
Sub2API batch handlers define owner-scoped, cursor-bounded list/items reads that
return public metadata rather than provider response envelopes or reference-image
bytes. ScalaAPI now routes `GET /v1/images/batches` to a PostgreSQL-backed
Platform query, returns a stable `object: list` envelope, and projects a successful
batch's `data` array for `/items`. Repeated reads must be side-effect free and a
wrong API key must return 404 without contacting the Provider. The implementation
retains ScalaAPI's PostgreSQL operation identity and imports no Sub2API schema or
key. Cancellation calls the provider task/batch endpoint before local state is
made terminal; any ambiguity retains the hold and produces an operator-visible
reconciliation incident. Object orphan cleanup is now a bounded background pass.
Platform `c1bbb4d` adds bounded S3-backed batch-download ZIPs with manifest/error
entries and a signed redirect smoke assertion. Platform `0134323` adds terminal
retention cleanup with retryable deletion; `b5586cf` proves running-operation
restart restore and Compose retention windows. Platform `d797cb1` persists
dedicated batch-item objects and rows, serves fresh signed URLs, and makes orphan
and retention passes item-aware. Platform `57d33f8` closes bounded per-item `HEAD`
verification, missing/mismatched repair, retry state, stale-worker fencing, and
duplicate Provider downloads while preserving settled billing. The next package
must partition storage and then PostgreSQL from one Silo during active writes and
reconciliation, run a one-hour lifecycle contention soak, and prove no
referenced object is deleted while every orphan/retention transition converges
after recovery. The completed source and database fault injection remains a fast
regression gate for those runtime drills.
Platform `6bc411b` adds the first Admin Web operator workflow for this authority:
open/resolved incident filters, a manual reconciliation trigger, and
evidence-backed settle/release submission with a stable idempotency key per selected
command, so network retries replay the same decision.
Browser authorization, operator audit visibility, and the wider monitor/backup
surface remain separate release gates.
Platform `d8788dd` extends the COM-01 native checkout/refund boundary: migrations 038-042,
pending local order persistence before the provider call, deterministic mock
merchant references, a bounded HTTPS/Bearer mock adapter, a Stripe Checkout Session
adapter using Basic secret auth and minor-unit form fields, provider payment ID
persistence, raw-body Stripe signature timestamp verification, checkout/payment-intent
success and full charge-refund normalization, checkout URL persistence, pending-order
retry, provider selection, and idempotency conflict semantics. Migration 040 adds a
new refund command state machine: the refund row is committed before Provider
contact, ambiguous timeout/unavailable outcomes remain retryable under one command
key, distinct active commands for one order are rejected, Stripe refund amounts are
checked in minor units, and successful completion uses one audited NUMERIC
`payment_refund` effect. Migration 041 adds an actor-bound recovery row, expiring
`SKIP LOCKED` claims, original Provider idempotency replay, and a hosted retry worker;
expired claims are reclaimable without creating a second command. Migration 042 adds
the `refunded_amount` accumulator, allows multiple bounded partial commands while
serializing unresolved work, and gives every refund its own Provider/ledger effect
identity; orders transition through `partially_refunded` before `refunded`. Stripe
`refund.created` uses incremental amounts and `charge.refunded` uses cumulative
amounts, both through the same order-level accounting rule. More production
adapters, exact-boundary crash injection, and browser payment completion remain.
Migration `023-auth-oauth-states.sql` now adds one-time OAuth state with S256 PKCE:
the Admin start flow returns provider-bound state/verifier/challenge material,
PostgreSQL stores only hashes, and callback consumption binds the exact redirect URI
and rejects replay or expiry before any upstream token exchange. Platform `c029b3c`
adds configurable authorization/token/user endpoints and a source-owned Provider
mock that binds one-time authorization codes to client, redirect, and S256 verifier;
the `scalaapi-oauth-20260809b` gate proves account creation and replay rejection.
Redirect allowlists, account-link collision policy, and browser UX remain release
work.
The `AUTH-04` Passkey slice adds migration `031-passkeys.sql` and Fido2-backed
registration/authentication routes. PostgreSQL stores bounded five-minute
flow-scoped challenges with atomic consumption plus credential public keys,
user handles, signature counters, display names, and last-use timestamps; actor/IP
registration and revocation audits commit with credential mutations. The targeted
empty-schema test proves challenge replay rejection, credential deletion, and
monotonic counter updates. User Web `45b75f8` now converts browser creation/assertion
payloads and exposes passkey registration, revocation, and sign-in. Browser WebAuthn
ceremony, public-endpoint anti-enumeration, and distributed abuse limits are the next
identity exit conditions, so AUTH-04 remains `partial`.
The first User Web slice now provides a standalone refresh-aware Solid client:
registration/password login, PKCE callback, dashboard balance/recent usage,
user-scoped usage history, API keys, billing/subscriptions, profile, and password
change, password recovery, email verification, and TOTP setup/verification/disable
with backup-code display. Billing lists active plans, purchases/cancels/renews
subscriptions, redeems promotion codes, and generates a referral code with summary
totals. The Dashboard now also lists published, unexpired announcements and marks
each item read through the authenticated idempotent read endpoint. The API adds
user-scoped usage and balance reads and the Compose stack
serves User Web separately from Admin Web. Source-built project
`scalaapi-user-portal-0810b` now proves login, Dashboard balance/identity, Usage,
API keys, and Profile navigation through the real proxy; backup-code sign-in UX,
recovery-mail delivery, real payment checkout, signup referral attribution/
anti-abuse, and commercial audit remain
open.
- COM-05 now has a bounded read-tracking slice at Platform `acb1c66`: migration 033
  stores one user/announcement read state, the authenticated list/read endpoints
  filter published and unexpired rows, and the first read writes one audit event while
  duplicate reads replay the timestamp. User Web renders unread items on Dashboard;
  targeting, scheduling, browser authorization, and commercial delivery evidence are
  still required before moving COM-05 to implemented.
The `CORE-03`/`CORE-05` scheduling slice at Platform `c2d3cf9` persists group RPM
windows in Orleans state, resolves exact model routes before longest-prefix and
wildcard patterns, applies overnight peak multipliers to the primary dispatch,
and walks active multi-level fallback chains with cycle protection. Existing sticky
bindings, capability filtering, priority/load ordering, account and user
concurrency, and idempotent group spend remain covered by the Grain suite; HTTP
group CRUD validation, distributed rate-window contention, and multi-Silo fallback
fault evidence remain release work.

The `SEC-01` runtime slice is active at Gateway `8f33790` and Platform `94e0db8`.
The canonical dispatch contract carries bounded request and response content.
Platform applies request `log`/`block` rules before scheduler/lease activity and
response rules after Provider validation but before non-stream delivery. Both stages
write rule-linked audits; request blocks create no lease, while response blocks hide
the Provider body, preserve one normal usage debit, and replay the client-facing
400. Migration 029 adds the versioned `unicode-confusable-v1` evaluator, classifier
selection, policy revision state, and audit redaction metadata. Gateway
event-boundary streaming enforcement is source-tested and empty-stack verified;
the classifier boundary uses a source-owned HTTP contract plus an optional OpenAI
Moderation HTTPS adapter. The latter sends Bearer-authenticated `input` and
configured `model`, validates one `results[].flagged` result, and applies bounded
request/response bytes and timeout control with deterministic fail-closed
status/schema/transport mappings. Migration 043 and the Admin validator now accept
only `local`, `external`, or `openai`; a real empty-schema test proves an `openai`
rule can be persisted, evaluated, and audited. Migration 030 provides durable single-instance
change propagation and alert evidence; Platform `15cdfc0` adds a concurrent
two-worker PostgreSQL claim/publication assertion. Platform `94e0db8` removes the
revision TTL and makes authenticated cache rebuild restore the authoritative
PostgreSQL revision plus an invalidation version; a dedicated RemoteGarnet test
proves both deleted keys recover. The domain remains `partial` until separate-process
ordering/failure, live browser authorization/API evidence, and long-stream metrics are
automated.

## Next implementation slice

1. **Media worker contention soak (P0).** The source-owned fault proxy and
   `scalaapi-media-partition-0811d` gate now cover mid-body and post-commit
   response-loss PUT outcomes plus rootless object-storage and PostgreSQL
   partitions of one secondary Silo. Platform `7768132` adds a configurable
   repeated-due-work gate with deterministic parent/item key and accounting
   invariants plus optional secondary restart/rejoin. The 60-second
   `scalaapi-media-contention-rejoin-0811f` check passed two cycles and two
   rejoins. Run the documented 3600-second gate before closing this slice; exit
   only when it records zero duplicate billing effects, duplicate final objects,
   or premature deletion.
2. **Protocol and Provider fidelity (P0).** Add provider-specific malformed,
   timeout, disconnect-before-output, partial-output, cancellation, and retry
   fixtures for Anthropic Messages and Gemini generation, then complete the
   remaining OpenAI Responses mutation subresources and video cancellation/
   retention settlement. Keep each external protocol independent at Gateway and
   normalize only the internal revision-3 contract. Exit when JSON/SSE goldens,
   Provider mock HTTP tests, empty-stack settlement/reconciliation, and error
   envelopes pass for every added operation without importing a Sub2API route or
   state mapping.
3. **Distributed authority and operator recovery (P0).** Extend the existing
   multi-process policy, scheduler, and accounting evidence through Garnet
   partition/rebuild with TLS enabled, primary-Silo replacement, stale-worker
   rejection, and operator settle/release at crash boundaries. Add live
   authenticated Admin browser evidence for reconciliation, monitor, backup, and
   recovery workflows. Exit when every ambiguity becomes exactly one settlement,
   an evidence-backed no-charge release, or one durable incident, and audit/
   idempotency rows survive process replacement.
4. **Independent release gate (P0).** Move the exact empty-volume Compose matrix
   into blocking hosted CI, then add the longer realtime/backpressure soak,
   PostgreSQL backup plus isolated restore, Garnet rebuild, security scans, and
   container/resource cleanup checks. A sibling-repository checkout token or an
   independent release repository is a prerequisite. Exit only when any failed
   child test or benchmark makes the top-level job non-zero, all images are tied
   to source commits/digests, and `podman ps -a` is empty after local runs.

Packages 1 and 2 may proceed independently. Package 3 consumes their fault
fixtures, and package 4 promotes the same commands without replacing them with a
different CI-only path. No package may add Redis, CDC, Debezium, legacy schema,
old IDs/keys, compatibility routes, or Sub2API runtime/data dependencies.

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
- Added migration 035 and subscription entitlement reservation to the same authority
  boundary. Active grants are row-locked before dispatch; completed usage consumes
  actual cost, never-forwarded/no-charge terminal paths release the maximum hold,
  and unknown Provider outcomes retain the reservation for reconciliation. The Host
  test proves concurrent over-allocation rejection, settlement usage, and abort
  release; external payment coupling, quota reconciliation, and browser evidence
  remain separate work.
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
  The four-session/three-second WebSocket soak now passes; longer
  backpressure/load soak, replay-after-restart, remaining Gateway crash boundaries,
  and multi-silo recovery are carried into the release gates below.

Remaining package deliverables:

- The referral reward command is now an authority-compliant slice at Platform
  `6344f88`: deterministic dual-user locks, one attribution, NUMERIC AccountingStore
  effect, actor/IP audit, exact replay, and changed-payload conflict are covered by
  a real PostgreSQL test. Extend it with signup-code attribution and anti-abuse
  policy before moving COM-04 to implemented. Subscription grants and any other
  new monetary effect must use the same account/version API and cannot write
  `balance_ledger` directly.
- The operational-metrics command is now an authority-compliant slice at Platform
  `9848427`, and Platform `0dd49cf` adds the authenticated Admin Operations
  dashboard with typed summary rows, kind/severity alert filters, and explicit
  refresh. The real PostgreSQL store test and Admin Web Chromium suite both pass
  (`2/2`). Add projection collectors, alert rules, cross-service correlation,
  alert delivery/recovery, credential rotation/redaction, long-stream metrics,
  and live authorization before moving OPS-02 to implemented.
- Admin audit reads are now a bounded safe-output slice at Platform `becf189`:
  list/export limits, recursive sensitive-field redaction, and removal of the
  generic client insert path are covered by a real PostgreSQL test. Add retention
  and immutable-storage controls, authorization/browser export tests, and a
  security scan before moving SEC-02 to implemented.
- Proxy/TLS administration is now a secured configuration slice at Platform
  `db770e2`: AES-GCM proxy secrets, redacted list views, bounded proxy/TLS input,
  generic probe errors, and actor/IP audit are covered by a real PostgreSQL test.
  Add provider-specific outbound adapters, actual TLS fingerprint application,
  secret rotation/retention, browser authorization, and security scans before
  moving SEC-03 to implemented.
- Channel-monitor writes are now an audited bounded slice at Platform `326fc43`,
  with the authenticated Admin Web history/filter/check workflow at Platform
  `4f78b71`. Active-account validation, health/latency/error bounds, paged history,
  actor/IP audit, and the Chromium contract are covered. Add scheduled runners,
  monitor templates/history, feedback notifications, and live authorization before
  moving OPS-03 to implemented.
- OPS-05 now has a bounded maintenance slice at Platform `80ab783`: repeatable-read
  user export omits credential material, while cleanup migration 032 removes only
  expired auth/session/Passkey data under explicit retention and row limits. The
  Admin command is dry-run capable, actor-scoped and idempotent, with audit evidence;
  add scheduler integration, immutable retention/object cleanup, browser authorization,
  and maintenance metrics before moving OPS-05 to implemented.
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

- The runtime `realtime_soak.py` gate now covers four concurrent sessions at the
  seeded concurrency limit with a three-second hold and exactly-once lease/usage/
  hold/ledger assertions. Extend it with longer-duration load, backpressure
  pressure, and reconnect/replay after process replacement.

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

The Embeddings provider-profile contract slice is now closed for this checkpoint. Gateway
`6243b2d` bounds input cardinality and dimensions, validates successful float and
base64 response shape and usage, and retains an unknown-charge lease for a
malformed Provider payload. Platform `c029b3c` provides deterministic dimension,
encoding, usage, and fault profiles with HTTP tests; the
`scalaapi-embeddings-20260809b` empty-stack gate proves two float vectors, one
base64 vector, NUMERIC settlement, and `502/provider_protocol_error` reconciliation.
Platform `5f04bfd` and Gateway `22f65d4` add source-owned OpenAI-compatible,
Jina-compatible, and Gemini-compatible profiles, model catalog entries,
deterministic per-profile token accounting, profile dimension ceilings, versioned
goldens, and four pricing-versioned empty-stack requests with exactly-once
settlement. Live adapter/provider-header fidelity and multi-instance contention
remain before GW-07 can become `implemented`.

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
Gateway retains the lease for malformed envelopes. Gateway `8f33790` now freezes
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
- Gateway `7b8b03c` freezes versioned OpenAI Chat/Responses, Anthropic Messages,
  and Gemini request/response/SSE/error fixtures, and tests parser normalization,
  usage/terminal events, response validation, all sixteen request/response pairs,
  cross-protocol error normalization, same-format Provider error passthrough,
  and inbound translation of Gateway-generated failures. The
  `scalaapi-provider-groups-0810l` gate now proves Anthropic JSON/SSE/count-token
  and Gemini catalog/JSON/SSE runtime routing, exactly-once billable settlement,
  and no-charge control release. Commit `024b215` adds independent Anthropic and
  Gemini 429/500/malformed/timeout/disconnect profiles, HTTP/SSE contract tests,
  and the full empty-stack billing matrix. Add provider-specific
  catalog/tokenizer fixtures, live adapter evidence, and multi-Silo/load
  contention before promotion to `implemented`.
- Use the completed pairwise request/response/error goldens as the deterministic
  baseline for the remaining provider-specific header/error matrix, then extend
  the passing runtime provider-group gate without external compatibility
  assumptions.
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
- The source gate now proves concurrent cross-Gateway idempotency and a controlled
  secondary Silo/Gateway stop, primary settlement with one active Silo, original
  container rejoin, and post-rejoin settlement. Treat these as baseline evidence,
  not as promotion to `implemented`.
- Platform `e5c341d` now loads a configured PEM CA trust anchor, enforces the
  configured Garnet DNS server name, and covers authenticated TLS RESP traffic
  plus wrong-name rejection with an in-process listener. Compose passes the CA
  path into Platform alongside the existing Gateway setting.
- Platform `bcf80e7` closes the server-side deployment slice with
  `docker-compose.tls.yml` and `garnet_tls_smoke.sh`: Garnet PFX server TLS,
  explicit password authentication, read-only relabeled CA/PFX mounts, and
  Platform/Gateway DNS-validated readiness are exercised in the complete
  source-built smoke. The client unit boundary is not deployment evidence.
- The `scalaapi-garnet-rotation-0810` source gate now covers same-CA PFX rotation
  through Garnet's refresh period, wrong-name and expired-certificate rejection,
  valid-certificate recovery, client reconnect, and a post-recovery billable
  request. Keep default development Compose plaintext and select TLS only via the
  checked-in override.
- The `scalaapi-realtime-soak-0810b` source gate now covers four concurrent
  realtime sessions, bounded connection holds, Provider usage validation, and
  exactly-once billing after the sessions close.
- Extend that gate with cache flush and stale-version recovery after restart, and
  concurrent clients during Garnet outage and Silo replacement. Media partition
  recovery is already covered by the dedicated source smoke; retain its network
  helper as a reusable failure-injection primitive.
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
- Add the backup/restore gate to the blocking workflow: create a fresh target
  database, run an idempotent PostgreSQL backup, verify artifact size/checksum,
  restore into the isolated target, replay the command, and remove all stack
  volumes. Follow with signed/offsite artifact, RPO/RTO, rollback, and restore
  failure-injection gates before disaster recovery is considered complete.

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
2. Package 2 defines transport semantics; package 3 (`8f33790`) freezes the
   source-owned protocol fixtures and keeps them independent of external Providers.
3. Package 4 runs the state machines under concurrency and infrastructure failure.
4. Package 5 makes the same evidence mandatory in hosted release CI.

Implement each independently verifiable functional point in its owning repository,
with focused tests and a detailed commit message describing contract, failure
semantics, and evidence. Update `current-state.md`, `verification.md`, the affected
inventory acceptance row, and this checkpoint after each completed package.

## Stage exit and following expansion

The stage exits only when all 13 scenarios pass from an empty environment locally
and in hosted CI, all monetary invariants reconcile, and OpenAI Chat can be promoted
using the inventory's contract/test/runtime rule.

Then expand the remaining 58-domain work in this order:

1. Complete the remaining OpenAI Responses mutation subresources beyond the
   implemented read/input_items/cancel/delete/compact slice, then Embeddings/Images/video/realtime,
   Anthropic Messages, Gemini generation, model catalogue/token counting, and
   runtime cross-protocol E2E; source-owned protocol fixtures are frozen in
   Gateway `8f33790`.
2. Complete the remaining media lifecycle after the now-finished batch
   list/items/cancellation/orphan-cleanup/archive/retention/item-storage,
   transport-loss, and single-Silo partition slice: a one-hour worker contention
   soak, Provider-specific OAuth refresh and pricing/tokenizer
   adapters, deployment-scale HA/offsite object storage, and full lifecycle evidence.
3. Complete identity hardening beyond the TOTP, OAuth PKCE, Passkey, and encrypted
   mail-outbox state machines, including backup-code recovery UX, live SMTP/provider
   delivery, anti-enumeration,
   and browser tests for the new User Web/API-key/usage/order/
   subscription flows.
4. Complete production payment adapters/reconciliation/refunds, subscription payment confirmation
   and quota reconciliation, redeem, signup referral
   attribution/anti-abuse, notification, and commercial audit flows.
5. Complete policy/security, observability, multi-region/HA, load and long-connection
   soak, backup/restore, signed updates, and rollback drills.

Every later domain uses new ScalaAPI contracts and clean seed data. Sub2API remains
read-only research material and is never an acceptance oracle for compatibility.
