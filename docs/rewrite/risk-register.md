# ScalaAPI Rewrite Risk Register

| Risk | Severity | Current state | Required control |
| --- | --- | --- | --- |
| Lease, hold, or ledger double settlement | P0 | Partial | Lease creation persists an active hold; completion/abort/expiry and NUMERIC debit are transactionally idempotent; add crash injection, reconciliation, and historical replay tests |
| PostgreSQL and Orleans authority split | P0 | Partial | Product `entity_registry` now owns business discovery; add full PostgreSQL aggregate repositories and accounting authority, with no Orleans storage introspection |
| Redis or embedded cache reintroduced | P0 | Controlled | Official Garnet digest, external TCP probe, dependency scan, no fallback implementation |
| Garnet outage or stale projections | P0 | Partial | Fail-closed readiness and authenticated `scalaapi:v1` rebuild pass; add flush, stale-version, TLS, and multi-client tests |
| Credential disclosure or weak rotation | P0 | Partial | Envelope encryption, redacted logs/API responses, rotation drill, security scan |
| Provider response and usage differences | P0 | Partial | Deterministic mock JSON/SSE and malformed-usage fail-closed settlement pass; add adapter contract, golden fixtures, retry/timeout/disconnect reconciliation |
| Request idempotency replay or fingerprint drift | P0 | Partial | Durable key/fingerprint rows reject duplicate charge and conflict before scheduling; bounded non-stream response replay is implemented; add restart-before-expiry and streaming semantics tests |
| Pricing version or decimal precision error | P0 | Partial | Decimal-only business DTOs and projection precision tests pass; fixed-scale Cap'n Proto fields are enforced; add immutable price-version and NUMERIC historical settlement tests |
| Scheduler split-brain or stale sticky route | P0 | Open | Orleans lease ownership, Garnet rebuild, multi-silo failure drills |
| Media bytes lost or unauthorized | P0 | Open | S3 lifecycle, signed access, metadata/object reconciliation, restore test |
| Long connection leaks or backpressure | P1 | Open | Streaming/WebSocket soak, bounded buffers, cancellation and disconnect assertions |
| Session and auth abuse | P1 | Partial | Hashed rotating sessions and revocation checks are implemented; add replay/concurrency tests, TOTP hardening, Passkey controls, and distributed limits |
| Benchmark or test false positive | P1 | Controlled | Child-report validation and non-zero propagation are implemented; retain CI failure tests |
| Contract source or generated artifact drift | P1 | Partial | Schema digests match; add deterministic C# generation and generated-output comparison |
| Observability gaps | P1 | Open | Structured audit, metrics, traces, alerts, and dashboard smoke tests |
| Backup, restore, or rolling release regression | P1 | Open | Signed artifacts, measured RPO/RTO, rollback and recovery drills |
