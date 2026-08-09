# ScalaAPI Rewrite Architecture

ScalaAPI is a new product implemented by Gateway and Platform. No internal or
database compatibility with Sub2API exists.

```text
Client protocols
      |
      v
Gateway (HTTP/WebSocket, conversion, streaming, Provider transport)
      | Cap'n Proto over protected local RPC
      v
Platform (identity, scheduler, leases, pricing, ledger, media, Admin/User API)
      |             |                    |
PostgreSQL      Garnet             S3-compatible storage
  authority      projection/cache    media bytes
      |
 Provider accounts and settlement metadata
```

## Gateway

Gateway owns external protocol semantics, request limits, format conversion,
streaming/backpressure, WebSocket lifecycle, upstream headers, Provider adapters,
and safe error translation. It never owns balances, leases, provider credentials,
media metadata, or authoritative usage.

Gateway reads auth, model, and route projections from Garnet. It never writes those
keys. On a cache miss it calls Platform RPC. Garnet failures cannot make billable
requests fail open.

Provider errors stay byte-preserving when the inbound and upstream protocols are
the same. When Gateway crosses protocol boundaries, it extracts the bounded
status/type/message contract and emits the target OpenAI, Anthropic, or Gemini
error envelope; standard HTTP status semantics win over a conflicting provider
label. This is a new product contract and does not define compatibility with
Sub2API error bodies.

## Platform

Platform owns identity, groups, API keys, provider accounts and encrypted
credentials, scheduler state, lease state machines, balance holds, decimal pricing,
append-only ledger entries, usage settlement, content policy, media metadata, and Admin/User APIs.
Orleans coordinates concurrency; PostgreSQL is the durable business and accounting
source of truth. Orleans storage internals are never used as a business listing API.

Provider pricing is an explicit source boundary. A bounded HTTPS-by-default catalog
adapter accepts only the new JSON decimal quote contract, authenticates without
logging credentials, and normalizes each snapshot to a deterministic checksum. The
Platform persists source/provider/model metadata and immutable NUMERIC versions in
PostgreSQL, closes only the prior open version for the same source/model, and leaves
identical snapshots as idempotent replays. Provider-specific adapters and tokenizers
must publish into this boundary rather than changing lease settlement logic.

Passkey authentication is a native Fido2/WebAuthn boundary. PostgreSQL stores only
short-lived flow-scoped challenge options and credential public material; private keys
remain in authenticators. Challenge consumption is atomic, signature counters are
monotonic, and credential registration/revocation audits are written with the state
mutation. The public login endpoint issues the same rotating session contract as
password/OAuth login; browser ceremony and anti-abuse policy remain release gates.

Password-reset and email-verification notifications use a separate Platform-owned
outbox. Token hashes remain the authentication lookup authority; only AES-GCM
protected token material is stored for delivery. A bounded worker claims rows with
PostgreSQL leases, sends through the configured SMTP adapter (or an explicit local
filesystem capture provider), retries with backoff, and records sent/failed evidence.
Superseded tokens cancel pending notifications in the same issuance transaction.
Live provider delivery, metrics, and abuse limits remain release gates.

Maintenance is a Platform-owned data boundary. `/user/export` uses a repeatable-read
snapshot and returns only bounded non-secret account, usage, session, and Passkey
metadata. Admin cleanup is a separate idempotent command with explicit retention and
row limits; it removes only expired authentication/ceremony records and writes the
operation plus actor audit in the same transaction. Media/object retention and
immutable audit retention are separate lifecycle controls.

Announcements are also Platform-owned. Admin remains the publisher of announcement
content and lifecycle state; User Web reads only published, unexpired rows through an
authenticated user-scoped query. The `announcement_reads` table is a user/announcement
unique acknowledgement state, and the first acknowledgement writes its audit event in
the same transaction. Read tracking is deliberately separate from publication,
targeting, and scheduling so those policies can be added without changing billing or
identity state.

`accounting_accounts` is the monetary authority: one row per product user stores a
NUMERIC posted balance and monotonically increasing ledger version. The account,
append-only `balance_ledger`, `request_leases`, `balance_holds`, request idempotency,
usage events, and outboxes form one durable billing boundary. All current monetary
effects acquire the same per-user PostgreSQL transaction lock. Media operations use their
own idempotency key and lifecycle table because asynchronous response metadata must
survive provider polling. A repeated synchronous/streaming key is checked before
scheduling and returns replay or fingerprint conflict; completed non-stream
responses additionally retain a bounded body for replay after settlement. Active
duplicates remain 409 until the completion report is durable, and streaming replay
is a separate protocol concern. Each lease also stores an immutable price version
and NUMERIC unit-rate snapshot; settlement never reprices from mutable process
configuration. An active subscription adds a second NUMERIC entitlement boundary:
the lease transaction locks the selected subscription row and reserves its maximum
hold before Provider dispatch. `quota_reserved_usd` is consumed by the same normal
usage settlement or released by a proven no-charge/never-forwarded terminal path;
unknown Provider outcomes retain the reservation for reconciliation. This prevents
distributed concurrent requests from overselling a grant without duplicating
accounting SQL in Gateway or Admin. A zero grant is finite, while users without an
active subscription continue to use account balance only.
The Admin-owned subscription lifecycle worker consumes due `renewal_at` rows with
`SKIP LOCKED`: non-renewing rows become `expired`, internal plans reset the next
grant only after `quota_reserved_usd` reaches zero, and rows with unresolved holds
become `past_due` until the lease terminal state is known. Expiry and renewal events
use deterministic period keys in `subscription_events`; external payment providers
must explicitly advance a future adapter before a `past_due` row can renew.

After authentication and API-key capability authorization, Platform evaluates the
bounded request content against active, scope-aware `log`/`block` rules. Matches
are persisted in `content_audit_logs` with an explicit `request` or `response`
stage and rule identity. A request blocking decision terminates dispatch before
group rate accounting, scheduling, credential hydration, lease/hold creation, or
Provider contact. For successful non-stream Chat responses, Gateway evaluates the
Provider body through the same lease-bound RPC before delivery. A response block
replaces the body with HTTP 400 `content_policy_violation` while normal Provider
usage still completes the lease and the policy response is retained for exact
idempotency replay. For SSE, Gateway buffers one bounded event until the same
lease-bound decision allows it, then emits a protocol-shaped terminal policy error
for block or fail-closed outcomes without leaking the blocked event. A blocked or
failed stream retains unknown-charge evidence for reconciliation. The shared
`unicode-confusable-v1` evaluator performs deterministic NFKC/case-folding,
format-character removal, and bounded confusable mapping before local matching.
Rules persist evaluator version, classifier choice, redaction, and a monotonic
policy revision. The configured `external` classifier uses the source-owned HTTP
adapter contract `POST /v1/classifier/evaluate` with JSON fields `content`,
`pattern`, and `evaluator_version`. Platform bounds the UTF-8 request to 129 KiB,
the pattern to 1024 bytes, the response to 8 KiB, and the timeout to 100-5000 ms.
HTTP 429/5xx and transport/timeout failures map to retryable
`content_policy_classifier_unavailable`; non-success or malformed/unknown JSON
maps to `content_policy_classifier_protocol_error`. The Provider mock implements
match, no-match, outage, malformed, oversized, and timeout fixtures for this
contract. An unavailable adapter fails closed; it is not a silent local fallback.
Every Admin rule create/update/delete increments the
revision and appends an actor/IP audit row plus a PostgreSQL change-outbox event
in the same transaction. A hosted Platform worker claims those events with
`FOR UPDATE SKIP LOCKED`, publishes the latest revision and invalidation counter
to authenticated Garnet, and clears or retries the claim with bounded error
evidence. Policy blocks and classifier/evaluator failures append deterministic,
redacted alert events in the same policy-decision transaction. Admin exposes
protected paged change and alert queries; PostgreSQL remains authoritative when
Garnet is unavailable.

A request begins `held`. Gateway must persist `forwarded` before contacting a
Provider and records `output_started` after its first successful client write. The
immutable `request_lease_events` journal records these facts and abort disposition.
A TTL may safely expire and release only a lease that remained `held`. A timed-out
`forwarded` or `output_started` lease enters `reconciliation_needed`, keeps its hold
active, blocks the same idempotency key, and may still accept one late durable usage
completion. Elapsed wall-clock time, connection loss, or a synthesized Gateway
error is never evidence of no Provider charge.

User creation and configuration never carry a balance. Administrative funding,
payment credit/refund, redeem bonus, and usage debit use stable effect IDs and one
repository. An accepted effect atomically updates the account, appends the next
versioned ledger row, and upserts the latest projection snapshot. Administrative
commands additionally require an idempotency key/reason, verify active holds, and
persist actor audit. Dispatch availability is computed from posted balance minus
active SQL holds in the same serialization domain.

Orleans is not a monetary authority. The user Grain stores only the last projected
ledger version and balance, ignores older snapshots, and permits same-version repair
when its stored value is corrupt. Admin requests may project immediately; a Platform
hosted worker drains `accounting_projection_outbox` with expiring claims and bounded
backoff. A failed projection never rolls back or duplicates committed money. A
PostgreSQL advisory lock serializes periodic reconciliation across all Silos and
Admin-triggered runs. Each run proves account balance/version and ledger contiguity,
usage/debit equality, lease/hold terminal state, and Grain projection state. It may
repair only terminal holds and stale projections whose expected outcome is proven;
all other drift and unknown Provider charges become durable operator-visible
incidents. Admin operators may resolve an open `unknown_provider_charge` incident
through a token-protected Platform command: `settle` reuses the normal usage effect
and price snapshot, while `release` is accepted only with explicit no-charge
evidence. The resolution row, lease terminal transition, hold/accounting effect,
immutable operator lease event, and actor audit are committed atomically; the
incident remains resolved on later reconciliation runs. Gateway and Platform have
opt-in, one-shot fault hooks around dispatch, Provider completion, settlement
commit, and outbox acknowledgement. A claim marker prevents a restarted process
from repeating the same injected crash. The Podman single-silo smoke enables an
explicit Orleans membership recovery mode to retire stale active rows before the
replacement silo joins; multi-silo deployments retain normal liveness voting.
One post-settlement-commit crash is source-smoke proven; the full hook matrix and
multi-instance evidence remain release gates.

## Garnet

Garnet is a separate Microsoft Garnet Server, pinned by image digest and reached by
TCP on the private service network. The product uses Garnet's RESP transport but
does not run Redis or an in-process RESP server. Development uses password
authentication. Both clients support TLS 1.2/1.3 with certificate-name validation;
production deployment must enable it and mount trust material through an override.

Key namespaces are prefixed with `scalaapi:v1`. Auth, model, route, sticky-session,
rate-window, content-policy revision, and invalidation keys have explicit TTLs or
are version counters.
All keys are projections and may be rebuilt from the product registry and Orleans
aggregate projections through the protected Platform rebuild operation. Garnet outage makes
new rate-sensitive dispatch fail closed while settlement and recovery outboxes stay
available.

## Provider and object storage

Gateway owns the streaming Provider transport behind a common adapter contract.
Platform owns account selection, credential protection, scheduling, and settlement.
The source-owned Provider mock deterministically exercises JSON, SSE, 429, 500,
delay, disconnect, and malformed usage. Normalized request fields select faults,
while separate seeded accounts isolate scheduler cooldown and retry state. Gateway
rejects an incomplete payload-bearing 2xx before usage extraction. S3-compatible
storage owns media bytes; PostgreSQL owns object keys, metadata, retention, and
authorization.

## Internal contract

Platform owns the single Cap'n Proto source under `platform/contracts/capnp`.
Gateway vendors byte-identical schemas so its repository builds independently, and
both repositories check the same schema digest. Platform CI restores the pinned
`capnpc-csharp` 1.3.118 local tool, builds the official Cap'n Proto 1.0.2 compiler at
commit `1a0e12c0a3ba1f0dbbad45ddfef555166e0a14fc`, regenerates all C# artifacts in a
temporary directory, and byte-compares them with the checked-in output. The contract
is currently revision 3 and contains no compatibility branches or deprecated
fields. Its dispatch request carries the request body for the pre-dispatch policy
decision, and Platform rejects bodies above 128 KiB before billable work. Revisions
replace the single greenfield contract; they do not preserve old wire behavior.
