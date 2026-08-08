# ScalaAPI Rewrite Current State

This is the active baseline for the new ScalaAPI product. It is not a Sub2API
compatibility statement. The Sub2API repository is a read-only requirements
reference and is excluded from builds, runtime configuration, schemas, seeds, and
release artifacts.

## Source snapshot

| Repository | Commit | Worktree | Responsibility |
| --- | --- | --- | --- |
| `gateway` | `d5cf804` | local changes present | C++ HTTP/WebSocket edge, protocol conversion, streaming, Provider transport, Cap'n Proto client |
| `platform` | `6a1f47a` | local changes present | C# Orleans control plane, PostgreSQL persistence, leases, usage, media lifecycle, Admin API |
| `sub2api` | `43ec48d` | read-only clean reference | Functional requirements catalogue only |

The current source inventory is 49 Gateway implementation files, 8 Gateway test or
benchmark files, 82 CTest cases, 55 hand-written Platform production C# files plus
3 generated Cap'n Proto files, 16 Platform test or benchmark source files, 62
Platform test cases, 84 mapped Admin API endpoints, 31 product tables, 20 SQLSugar
entity types, and 24 Admin Web source files with 11 page views. Admin Web has no
browser test runner yet.

The reference inventory is 612 route registration calls, 39 concrete Ent schemas,
82 Vue view/component files, and 240 SQL migrations. These numbers describe scope,
not implementation parity or a migration target.

## Current implementation

- Gateway has protocol routing and conversion for Anthropic, OpenAI Chat/Responses,
  Gemini, embeddings, images, videos, model discovery, token counting, realtime,
  proxy/TLS hooks, and a durable local usage outbox.
- Platform has Orleans grains for users, API keys, groups, accounts, scheduling,
  pricing, leases, usage, media operations, and invalidation, plus an Admin API.
- CDC consumers, Debezium configuration, migration fences, migration write gates,
  migration-control endpoints, CDC-only tables, and their tests are removed from
  active code. Their documents remain under `docs/archive/migration`.
- Platform and Gateway use authenticated TCP clients for the external Garnet
  service, support TLS with certificate-name verification, and have no embedded
  cache implementation or Microsoft.Garnet package dependency.
- A source-owned Provider mock now supplies deterministic JSON, SSE, failure,
  delay, disconnect, and malformed-usage scenarios. Its image, health endpoint,
  success response, and 429 response passed the isolated Compose smoke.
- Platform owns the revision-1 Cap'n Proto schemas under `contracts/capnp`; Gateway
  vendors byte-identical copies and both repositories enforce the recorded SHA-256
  schema digests.
- `deploy/stack` is the versioned empty-environment launcher. PostgreSQL, Garnet,
  MinIO, and the health helper are pinned by image digest.

## Known gaps

- The current baseline creates a clean product schema and is checksum-idempotent,
  but its broad table set has not yet been reviewed against explicit aggregate
  ownership and repository contracts.
- PostgreSQL business state and opaque Orleans storage are still split; business
  listing must not query Orleans internal tables.
- Public DTOs and grain contracts still contain floating-point balances, quotas, and
  costs; all monetary paths must be converted to decimal/NUMERIC.
- Generated C# Cap'n Proto files are checked in but are not regenerated and digest
  verified by CI yet.
- Garnet key TTL policy, projection rebuild, cache-flush recovery, and multi-client
  integration tests remain incomplete even though connection outage/recovery passes.
- Holds, usage debits, pricing versions, reconciliation, refresh sessions, provider
  adapters, object storage, User Web, commercial workflows, and operational release
  controls remain partial or skeletal.
- Admin Web typecheck/build is now a blocking CI step, but browser coverage is absent.

## Current runtime evidence

On 2026-08-08 the isolated `scalaapi-build` project was built from the current
worktrees and started with new volumes. Platform image
`b999930ed8f11760d509ffbe856ec0cec221d4cf152e0f77026b8473bcf66756`, Gateway image
`7b8f0d63e81337565967b287bf50b17df0bc399f1b20b9ab1d18f3041a778746`, and Provider
mock image `425e1430cc32f8756a688d176f1d542c9026603c37e0cb609e55b5ee49d6bcb8`
were used. Every long-running service became healthy and the migrator exited zero;
the first execution applied all three files and repeated executions skipped the
same checksums.

Authenticated Garnet `PING`, `SET/GET`, PX expiry, `INCR`, and `DEL` passed. Stopping
Garnet changed Platform readiness to 503; restarting it restored readiness to 200.
Gateway readiness returned 200 and an unknown API key traversed the current dispatch
path to a stable 401. This is bootstrap evidence, not evidence for a successful
billable request or settlement.

## Historical runtime boundary

The old running stack was built on August 1/2 and uses `/var/run/sub2api`, a
`sub2api` PostgreSQL database, and migrations through 007 containing CDC and fence
tables. Its healthy probes are historical information only. New ScalaAPI smoke
tests must use isolated project names, volumes, an empty database, the current
images, and the external Garnet service.

## Acceptance rule

A feature is `implemented` only when its API or state-machine contract, automated
tests, and current-source runtime evidence all exist. Route registration, a table,
or a placeholder response alone is not implementation evidence.
