# ScalaAPI Greenfield Architecture

Audit baseline: Platform `30d82d01c2daed1ff0460fa020cad5d9ff434cdd`,
Gateway `98c62fdec99836929f1ab47412ef46c7f2c67683` and ScalaAPI
superproject `032721b65a3960171ce66a390451b98364f4b94a` on 2026-08-14.
The superproject currently pins Platform `e73a5d8` and Gateway `777278e`, not the
latest standalone component heads.

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
| The ScalaAPI superproject still pins `e73a5d8/777278e` while standalone heads are `30d82d0/98c62fd` | The latest component implementations are not yet one supported or released pair; standalone branch state must not be presented as paired release evidence |
| The shared Gateway worktree has two user edits and does not compile because it refers to nonexistent `LeaseAbortDisposition::Safe` | Those realtime-frame changes cannot enter a release until reconciled and reverified; clean `98c62fd` is unaffected and passes 161/161 CTest cases plus 16 benchmarks |
| Gateway accepts 32 MiB but Platform RPC accepts only 1 MiB | Large multipart/media can disconnect before a durable dispatch decision |
| Clean unit and benchmark evidence exists for both component heads, but a source-built two-Silo/two-Gateway deployment has not been exercised from the intended pair | Listener, dependency, failover, usage durability and cross-process behavior are not promoted to verified runtime behavior |
| Provider quota clients, model-catalogue refresh and bounded channel probes are implemented, but this audit did not execute live OpenAI, Anthropic, Gemini or xAI acceptance contracts | Provider-specific auth, rate-limit, timeout, malformed-response and catalogue/quota semantics remain runtime evidence gaps |
| Active/passive monitor leadership is PostgreSQL-fenced in source, but no controlled multi-process ownership/failover drill was run | Leadership uniqueness, lease expiry, backfill fencing and recovery remain partially evidenced |
| Scheduled backup creation and offsite upload paths exist, but no isolated object-store partition, readback, restore or reconciliation drill was run | Backup and offsite completion cannot yet be treated as operational durability proof |
| Authenticated Admin/User browser workflows were not run against the source-built backend | Passing TypeScript checks and production bundles do not establish persisted end-to-end product behavior |
| Both Web dependency trees report the high-severity `nanoid <3.3.18` advisory | Production release needs an upgrade or an explicit, documented security gate decision |
| The complete 3600-second mixed fault/load test has not been run against the latest exact pair | Long-duration settlement convergence, cleanup and failure-detection claims remain unverified |
| The centralized ScalaAPI CI/release workflow currently validates and publishes its older pinned pair | A new manifest, exact image digests and release tag are required after deliberately advancing and verifying both gitlinks |
| Release evidence records fixed gate names and `skipped: []` without parsing uploaded TRX or other job results | The artifact cannot substantiate executed/passed/failed/skipped totals even when workflow dependencies are green |

The audit also confirms that several former deviations are closed at the latest
Platform/Gateway heads: an empty PostgreSQL 17 database applies all 66 migration
inputs and skips all 66 on rerun; the four database-backed Platform assemblies
pass 502/502 and fail visibly without their required database; all six Platform
benchmarks execute; canonical and vendored contracts and generated C# output
match; and clean Gateway `98c62fd` builds, passes 161/161 CTest cases and runs all
16 benchmark entries. These checks are prerequisites, not substitutes for the
remaining paired runtime and release evidence above.

Such deviations must be resolved or removed from advertised scope; none may be
hidden with compatibility data or reference-project services.
