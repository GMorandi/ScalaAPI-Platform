# G1 Execution Plan

G1 starts only after G0 migration and contract gates pass. It does not change
the public Gateway route or send production traffic to Platform.

1. Apply the source semantic-outbox migration, run
   `emit_api_key_migration_cdc_snapshot()` once, create the source replication
   role/publication, and record the initial WAL LSN. Verify API-key rotation as
   old-hash revoke plus new-hash create, and soft deletion as old-hash revoke
   without a tombstone aggregate.
2. Start Debezium with `snapshot.mode=initial`, route all selected source topics
   to `sub2api.cdc.v1`, and verify the source LSN checkpoint is monotonic.
   For control-domain synchronous confirmation, add a source transaction
   outbox/correlation ID so the request can wait for the matching `SyncAck`; a
   post-commit Debezium record alone cannot provide that request-level wait.
3. Provision the ACL-protected `sub2api.credentials.v1` topic and target key
   version. Publish only `CredentialEnvelope v1` ciphertext for account
   hydration; verify decryption, plaintext hash, and `IAccountGrain` apply in a
   separate controlled consumer.
4. Run the Platform consumer in disabled/shadow mode first; validate envelope
   hashes, aggregate counts, duplicate replay, restart, and dead-letter replay.
5. Enable target application for a staging snapshot. Compare users, API keys,
   groups, accounts, account membership, leases, and balance ledgers by source
   identity and payload hash.
   Keep `target_canary` observation-only until a scoped tenant writer and
   source-side tenant fence are implemented; do not open the global target
   business write gate during canary observation.
6. Enable semantic source outboxes and measure async lag. G1 exit requires p99
   CDC lag below 5 seconds for 72 hours, zero unexplained P0 mismatches, zero
   credential findings, and a successful restore/replay drill.

Before any target-primary experiment, add and rehearse the source-side guard:
quiesce Sub2API writes, revoke its write-capable database role (or deploy a
read-only mode), verify no old-writer activity, and only then advance the target
fence. The target fence cannot enforce this requirement inside the legacy
process by itself.

G1 explicitly excludes canary routing, reverse synchronization, legacy read-only
mode, and old-database separation. Those are G2/G3/G4 decisions gated by the
fence and reconciliation evidence.
