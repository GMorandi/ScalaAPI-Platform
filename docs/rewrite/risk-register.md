# ScalaAPI Rewrite Risk Register

| Risk | Severity | Current state | Required control |
| --- | --- | --- | --- |
| Lease, hold, or ledger double settlement | P0 | Partial | Lease creation persists an active hold; completion/abort/expiry and NUMERIC debit are transactionally idempotent; outbox claims recover after restart and financial events never auto-dead-letter; Admin reconciliation detects missing/orphan debits and a clean seeded run passes; automate historical repair/backfill and add crash injection/replay tests |
| PostgreSQL and Orleans authority split | P0 | Partial | Product `entity_registry` now owns business discovery; add full PostgreSQL aggregate repositories and accounting authority, with no Orleans storage introspection |
| Redis or embedded cache reintroduced | P0 | Controlled | Official Garnet digest, external TCP probe, dependency scan, no fallback implementation |
| Garnet outage or stale projections | P0 | Partial | Fail-closed readiness, authenticated `scalaapi:v1` rebuild, Gateway deleted-version flush recovery, and usage-triggered API-key invalidation pass; add TLS, multi-client, and deployment restart tests |
| Credential disclosure or weak rotation | P0 | Partial | Envelope encryption, redacted logs/API responses, rotation drill, security scan |
| Provider response and usage differences | P0 | Partial | Deterministic mock JSON/SSE, model discovery, embeddings, token counting, Gemini/Anthropic shapes, and pollable media pass; add independent provider adapters, golden fixtures, retry/timeout/disconnect reconciliation, and provider-specific usage validation |
| Request idempotency replay or fingerprint drift | P0 | Partial | Durable key/fingerprint rows reject duplicate charge and conflict before scheduling; bounded non-stream response replay is implemented; add restart-before-expiry and streaming semantics tests |
| Pricing version or decimal precision error | P0 | Partial | Decimal-only business DTOs, fixed-scale Cap'n Proto fields, immutable NUMERIC lease snapshots, Admin publish/close validation, and Platform active-version refresh pass; add provider price adapters and historical backfill tests |
| Payment webhook replay or forged settlement | P0 | Partial | Raw-body HMAC verification, provider/event deduplication, exact order amount/currency checks, paid/refunded transitions, unique credit/refund ledger effects, and pending-event recovery pass; add provider secrets rotation, adapter reconciliation, and exact-boundary crash injection |
| Subscription entitlement or renewal drift | P1 | Partial | One-active subscription constraint, NUMERIC quota grants, expiry transition, and idempotent purchase/cancel/renew events pass; couple provider settlement, quota consumption, renewal workers, and reconciliation |
| Scheduler split-brain or stale sticky route | P0 | Open | Orleans lease ownership, Garnet rebuild, multi-silo failure drills |
| Media bytes lost or unauthorized | P0 | Open | Current mock polling preserves `image/png`/`video/mp4` metadata but still returns provider-owned URLs; copy bytes to S3-compatible storage, sign access, reconcile metadata/objects, and run restore tests |
| Long connection leaks or backpressure | P1 | Open | Streaming/WebSocket soak, bounded buffers, cancellation and disconnect assertions |
| Session and auth abuse | P1 | Partial | Hashed rotating sessions, self-service password/profile/delete flows, and revocation checks are implemented; add replay/concurrency tests, API-key revocation/retention assertions, TOTP hardening, Passkey controls, and distributed limits |
| Benchmark or test false positive | P1 | Controlled | Child-report validation and non-zero propagation are implemented; retain CI failure tests |
| Contract source or generated artifact drift | P1 | Partial | Schema digests match; add deterministic C# generation and generated-output comparison |
| Observability gaps | P1 | Open | Structured audit, metrics, traces, alerts, and dashboard smoke tests |
| Backup, restore, or rolling release regression | P1 | Open | Signed artifacts, measured RPO/RTO, rollback and recovery drills |
