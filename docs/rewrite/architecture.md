# ScalaAPI Greenfield Architecture

Audit baseline: Platform `bc083d1` and Gateway `b6e4e02` on 2026-08-14.

This document defines ScalaAPI's own architecture. Sub2API is non-normative
research input used only to discover capability families. Its API behavior,
database, migrations, identifiers, keys, cache layout, deployment and commit
delta do not define ScalaAPI requirements or acceptance.

ScalaAPI is a new product. There is no upgrade, import, cutover, dual-run,
dual-read/write or compatibility path from Sub2API.

## System boundary

```text
Clients
  |
  | product-native HTTP / SSE / WebSocket
  v
Gateway
  | protocol validation and conversion
  | Provider transport and streaming
  |
  | product-native Cap'n Proto RPC
  v
Platform
  | identity / policy / scheduling / leases / pricing / accounting
  | Admin API / User API / background workflows
  |
  +--> PostgreSQL        durable business and accounting authority
  +--> Garnet            disposable projections and coordination hints
  +--> S3-compatible     media and backup bytes
  +--> Providers         catalogues credentials quota and inference
```

Gateway and Platform are released as a paired product. A release identifies both
immutable commits and the exact contract digest; neither repository can infer
compatibility from a local build alone.

## Ownership

| Concern | Authority | Rule |
| --- | --- | --- |
| Public protocol and streaming | Gateway | Validate bounded requests; translate supported protocol pairs; enforce backpressure and terminal stream semantics |
| Provider HTTP/WebSocket transport | Gateway | Apply only Platform-issued target path headers and credentials; classify transport and Provider outcomes without inventing financial truth |
| Identity keys policy and configuration | Platform | PostgreSQL-backed product-native models; secrets are encrypted or hashed and never become Gateway authority |
| Account selection and scheduling | Platform | Durable leases and fenced coordination; stale cache or process-local state cannot authorize dispatch |
| Pricing holds usage and ledger | Platform | PostgreSQL NUMERIC state is authoritative; Gateway never owns balance or settlement |
| Business concurrency | Orleans plus PostgreSQL | Orleans coordinates actors; PostgreSQL remains the listing reconciliation and recovery authority |
| Fast projections | Garnet | Rebuildable and non-authoritative; cache loss must not lose business state |
| Media and backup payloads | S3-compatible storage | PostgreSQL owns metadata ownership lifecycle checksum and accounting links |
| Operator and user experiences | Admin Web and User Web | Use only authenticated product APIs; browser success must reflect persisted backend state |

## Billable request lifecycle

1. Gateway validates the protocol envelope, requires a product API key and sends
   only its hash plus a speculative auth-version hint to Platform. Gateway does not
   decide whether the key is valid or authorized.
2. Platform authoritatively validates the key, scope, capability and policy, then
   selects an eligible Provider account under durable scheduler constraints.
3. Platform creates one idempotent request lease with an immutable price snapshot
   and a maximum hold before Provider contact.
4. Gateway obtains durable `forwarded` acknowledgement before sending upstream and
   durably records `output_started` no later than the first successful client write.
5. Gateway extracts bounded terminal usage or records an evidence-classified
   failure. Timeout disconnect and partial output do not prove no Provider charge.
6. Platform commits exactly one settlement or proven no-charge release. Unknown
   charge state retains the hold and creates operator-visible reconciliation work.
7. Outboxes project committed state to Orleans/Garnet and integrations. Projection
   failure cannot undo or duplicate the PostgreSQL transaction.

The idempotency identity spans retries and process replacement. A completed
non-stream response may retain a bounded replay body; active or unknown requests
cannot be silently re-dispatched.

## Durable data rules

- Product migrations are forward-only and must bootstrap an empty PostgreSQL
  database without any reference-project schema or data.
- Every monetary mutation locks the product account serialization domain and
  appends a stable-effect ledger entry.
- Price and usage values use PostgreSQL `NUMERIC` and immutable versioned
  snapshots rather than binary floating point.
- Holds can be released automatically only for a proven never-forwarded or
  Provider-confirmed no-charge outcome.
- Business listing and reconciliation query PostgreSQL rather than Orleans
  storage internals or Garnet.
- Cross-process workers use PostgreSQL advisory locks or fenced expiring claims;
  process-local leadership is insufficient.
- Schema names and internal contracts are free to change atomically before
  release. No legacy aliases or compatibility branches are retained.

## Provider contract

A Provider label is not a support claim. Each advertised capability requires an
explicit matrix covering:

- native path method headers request response stream and error semantics;
- credential lifecycle and target-header compilation;
- account health cooldown quota and catalogue behavior;
- usage units price selection and terminal settlement evidence;
- bounded success authentication rate-limit timeout malformed and disconnect
  fixtures;
- explicit upstream-error exposure, normalization, monitoring-suppression and
  redaction rules rather than reference-system pass-through behavior;
- feature gates surfaced consistently by Admin Gateway and scheduling.

OpenAI-shaped transport may be reused where correct but does not imply native
xAI/Grok Search voice image video realtime OAuth or quota support. Unsupported
capabilities return a stable ScalaAPI-native error and remain unadvertised.

Provider credentials are semantic encrypted fields. Platform compiles a bounded
target credential set; Gateway prevents inbound client authentication from
replacing it and excludes values from logs metrics errors and response headers.

## Specialized and asynchronous operations

Search audio image video and realtime are separate product capabilities rather
than aliases for Chat:

- Search owns bounded query filters normalized results source metadata history
  privacy and per-query settlement.
- TTS and STT own audio byte limits object metadata signed access retention voice
  authorization and character or duration pricing.
- Image and video operations own durable operation/item state deterministic
  object keys polling cancellation repair retention and specialized usage.
- Each advertised Realtime protocol (for example Responses WebSocket, Live sideband
  or a Provider-native session) is a separate contract. It owns concurrency,
  backpressure, cancellation, terminal usage and one durable lease per session. Its
  initial request and every bounded text frame cross the same request/response policy
  boundary; binary/audio and attestation behavior are explicit product decisions.

Object completion requires both durable metadata and verified bytes. Partial PUT
or a lost success response must converge without a duplicate object or debit.

## Identity commercial and policy boundaries

Password sessions OAuth TOTP Passkeys reset and verification flows are native
ScalaAPI state machines. Tokens are hashed for lookup or encrypted only when a
delivery worker needs recoverable material. Public entry points share explicit
anti-enumeration captcha domain and distributed-rate policies.

Payments subscriptions redeem and referral effects enter the same accounting
authority through idempotent effect IDs. A checkout or webhook does not grant
balance until its provider-specific authenticity and state transition are durable.

Content policy coverage is an explicit capability matrix. Every advertised textual
request and response path runs at its bounded pre-Provider/pre-client point; binary
and opaque media have an explicit allow, block or classifier decision rather than an
implicit omission. Policy state and audits live in PostgreSQL; Garnet only
distributes revision projections. A classifier outage follows the configured
fail-closed contract and cannot silently fall back to a different policy.

## Operations

Active monitoring requires one fenced cluster owner and real bounded probes.
Passive monitoring requires durable watermarks deduplication privacy-aware
rollups and fenced backfill ownership.

Backup completion means an artifact was created encrypted or signed as configured
and its bytes and checksum were verified. Offsite completion requires actual
transfer and readback evidence. Restore targets are isolated from live authority
and must pass schema identity and accounting checks.

Runtime acceptance uses source-built images and an empty volume. Load and fault
harnesses must fail when any child exits early any query is invalid or settlement
does not converge. A one-hour run is evidence only when its final durable
invariants and cleanup succeed.

Initial installation has one ScalaAPI-native bootstrap contract for dependency
checks and the first administrator. It may be an authenticated deployment command
or a bounded setup UI, but it cannot depend on Sub2API state or silently create
production authority from default credentials.

## Contract and release discipline

Platform owns the canonical Cap'n Proto schemas. A paired change updates canonical
schemas Gateway vendors generated bindings digests and tests atomically. Handwritten
numeric enums are not an independent contract authority.

A releasable manifest records:

- Platform and Gateway immutable commits;
- canonical contract and generated-binding digests;
- migration manifest and empty-schema double-run result;
- executed passed failed and skipped test totals;
- Web build and backend-backed browser evidence;
- source-built image digests and short plus one-hour runtime evidence;
- security supply-chain restore and cleanup results.

Sub2API refs data and artifacts never appear in that manifest.
Roll forward and rollback replace the paired immutable Platform/Gateway deployment.
The services do not download and mutate their own binaries; any Admin endpoint that
claims an update must control and verify the external deployment transaction or be
removed.

## Current deviations

The architecture above is the target invariant not a claim that every path is
complete. At the audit baseline:

| Deviation | Consequence |
| --- | --- |
| Migration `055-search-history.sql` references `users` and `api_keys`; migration 056 repeats those names | An empty product database cannot reach the current schema |
| Gateway's vendored `dispatch.capnp` omits audio enum values present in Platform | Cross-repository contract verification fails |
| Database tests directly return when the connection variable is absent | The 502-test no-database run is not integration evidence |
| Scheduler benchmarks cannot resolve `ISlotLeaseStore` in their Orleans Silo | All four required cases exit without valid reports |
| Greenfield CI does not pass a Gateway path to contract verification | Hosted greenfield verification never proves canonical/vendor equality |
| Platform and Gateway each have independent tag publishers, and local release scripts bypass or invent evidence | Images/tags and pass reports can be produced without one paired pre-publication gate |
| Realtime dispatch omits request content, raw-relays later frames, skips ordinary query validation and uses the direct peer IP | Content policy and trusted-proxy identity do not cover the WebSocket path |
| HTTP response policy is selected by a chat-only predicate | Search, Antigravity, audio/media, embeddings and models do not receive equivalent response evaluation |
| Gateway accepts 32 MiB but Platform RPC accepts only 1 MiB | Large multipart/media can disconnect before a durable dispatch decision |
| `output_started` is a one-shot RPC after client output and failures are log-only; non-retryable unacknowledged usage is deleted | Provider-charge evidence can lack a durable reconciliation record |
| Gateway construction tolerates failed dependencies or bind/listen and readiness checks only dispatch UDS | A live process or successful `/ready` response does not prove Garnet, usage durability or every listener is usable |
| Anonymous model discovery returns an empty 200 response when Garnet is unavailable | Cache failure can masquerade as an authoritative empty catalogue |
| Platform target method/path/general headers and TLS profile fields are not fully enforced by Gateway | Invalid target compilation or metadata-only TLS profiles can reach outbound transport |
| Search is registered as stream-capable but the handler enables streaming only for chat-classified capabilities | Advertised Search streaming is unreachable in the current path |
| Admin `/admin/system/update` fetches only release metadata and reports a binary as downloaded without downloading or installing it | The UI/API can claim an update that never happened; this conflicts with paired immutable deployment ownership |
| No selected inventory row previously stated the initial-admin/setup or configurable upstream-error decision | Reference breadth could be silently omitted or mistaken for an implicit compatibility requirement |
| Channel monitor and passive monitor use process-local or placeholder leadership | Multi-process scheduling is not proven |
| Provider quota refresh rewrites seeded snapshots instead of calling Provider adapters | Quota freshness is not production behavior |
| Scheduled backup marks a claim complete without creating an artifact | Scheduler success is not backup success |
| Offsite upload records a destination without transferring bytes | Offsite status can overstate durability |
| Stress scripts query tables outside the actual ownership model and tolerate some child failures | Historical stress completion claims are invalid |

These deviations are tracked in
[feature-gap-report.md](feature-gap-report.md),
[implementation-task-list.zh-CN.md](implementation-task-list.zh-CN.md),
[risk-register.md](risk-register.md) and
[verification.md](verification.md). They must be resolved or removed from advertised
scope; none may be hidden with compatibility data or reference-project services.
