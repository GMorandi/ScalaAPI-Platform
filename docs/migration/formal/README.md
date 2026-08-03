# Migration fence formal model

`migration_fence.fst` is a small F* model of the control-plane state machine in
`MigrationFenceStore.ValidateTransition` and `PromoteAsync`.

The model captures the safety properties that matter during a database cutover:

- primary and mode must always be a consistent pair;
- only the documented transition edges are legal;
- an accepted transition advances the epoch exactly once (`transition` returns
  `None` for an invalid request);
- `target_primary` requires a completed snapshot, no outstanding inbox work, and
  no unreplayed dead letters;
- Platform writes are enabled only in `platform/target_primary`.

The model is machine-checked locally with F* 2026.03.24 and Z3 4.13.3; the
postconditions at the end of the file discharge the state-consistency, epoch,
readiness, and write-enable obligations. The executable C# transition tests
remain the runtime evidence. Run `./verify.sh` from this directory (or set
`FSTAR`/`Z3` explicitly) in CI before treating formal verification as a release
gate. The model does not claim anything about CDC throughput, WAL retention, or
the latency of PostgreSQL locks.

The cutover performance boundary is intentional: snapshot and CDC catch-up are
preparation work, while the final fence transaction is constant-size. Any
high-throughput claim still requires production-like lag, lock-wait, and WAL
retention measurements.
