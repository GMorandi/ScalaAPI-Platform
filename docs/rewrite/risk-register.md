# ScalaAPI Rewrite Risk Register

| Risk | Severity | Current state | Required control |
| --- | --- | --- | --- |
| Lease or ledger double settlement | P0 | Open | Transactional NUMERIC holds, unique lease/effect keys, crash/retry tests, reconciliation |
| PostgreSQL and Orleans authority split | P0 | Open | PostgreSQL repositories for business listings and accounting; no Orleans storage introspection |
| Redis or embedded cache reintroduced | P0 | Controlled | Official Garnet digest, external TCP probe, dependency scan, no fallback implementation |
| Garnet outage or stale projections | P0 | Partial | Fail-closed readiness passes; add explicit TTLs, rebuild job, flush and stale-version tests |
| Credential disclosure or weak rotation | P0 | Partial | Envelope encryption, redacted logs/API responses, rotation drill, security scan |
| Provider response and usage differences | P0 | Partial | Deterministic mock exists; add adapter contract, golden fixtures, malformed-usage reconciliation |
| Pricing version or decimal precision error | P0 | Open | Immutable price versions, decimal-only DTOs, NUMERIC round trips, historical settlement tests |
| Scheduler split-brain or stale sticky route | P0 | Open | Orleans lease ownership, Garnet rebuild, multi-silo failure drills |
| Media bytes lost or unauthorized | P0 | Open | S3 lifecycle, signed access, metadata/object reconciliation, restore test |
| Long connection leaks or backpressure | P1 | Open | Streaming/WebSocket soak, bounded buffers, cancellation and disconnect assertions |
| Session and auth abuse | P1 | Open | Rotating sessions, TOTP/Passkey controls, distributed limits, anti-enumeration tests |
| Benchmark or test false positive | P1 | Controlled | Child-report validation and non-zero propagation are implemented; retain CI failure tests |
| Contract source or generated artifact drift | P1 | Partial | Schema digests match; add deterministic C# generation and generated-output comparison |
| Observability gaps | P1 | Open | Structured audit, metrics, traces, alerts, and dashboard smoke tests |
| Backup, restore, or rolling release regression | P1 | Open | Signed artifacts, measured RPO/RTO, rollback and recovery drills |
