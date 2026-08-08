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

`request_leases`, `balance_holds`, `request_idempotency`, usage events, the NUMERIC
ledger, and the outbox form one durable billing boundary. Media operations use their
own idempotency key and lifecycle table because asynchronous response metadata must
survive provider polling. A repeated synchronous/streaming key is checked before
scheduling and returns replay or fingerprint conflict; completed non-stream
responses additionally retain a bounded body for replay after settlement. Active
duplicates remain 409 until the completion report is durable, and streaming replay
is a separate protocol concern.

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
delay, disconnect, and malformed usage. S3-compatible storage owns media bytes;
PostgreSQL owns object keys, metadata, retention, and authorization.

## Internal contract

Platform owns the single Cap'n Proto source under `platform/contracts/capnp`.
Gateway vendors byte-identical schemas so its repository builds independently, and
both repositories check the same schema digest. Checked-in Platform C# generation is
the remaining contract-supply-chain gap. The contract starts at revision 1 and
contains no compatibility branches or deprecated fields.
