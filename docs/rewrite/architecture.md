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

## Platform

Platform owns identity, groups, API keys, provider accounts and encrypted
credentials, scheduler state, lease state machines, balance holds, decimal pricing,
append-only ledger entries, usage settlement, media metadata, and Admin/User APIs.
Orleans coordinates concurrency; PostgreSQL is the durable business and accounting
source of truth. Orleans storage internals are never used as a business listing API.

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
configuration.

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
incidents. Forwarded/output-started evidence is now durable; an audited idempotent
operator resolution command and exact-boundary crash evidence remain required
before the billing slice is release-complete.

## Garnet

Garnet is a separate Microsoft Garnet Server, pinned by image digest and reached by
TCP on the private service network. The product uses Garnet's RESP transport but
does not run Redis or an in-process RESP server. Development uses password
authentication. Both clients support TLS 1.2/1.3 with certificate-name validation;
production deployment must enable it and mount trust material through an override.

Key namespaces are prefixed with `scalaapi:v1`. Auth, model, route, sticky-session,
rate-window, and invalidation keys have explicit TTLs or are version counters.
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
fields. Revisions replace the single greenfield contract; they do not preserve old
wire behavior.
