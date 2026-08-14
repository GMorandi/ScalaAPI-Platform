# ScalaAPI Rewrite Documentation

Authoritative audit snapshot: 2026-08-14, Platform `30d82d0`, Gateway `98c62fd`,
ScalaAPI pair `032721b`.

ScalaAPI is an independent greenfield product. Sub2API is non-normative research
input only: it does not define compatibility, automatic scope, migration or release
acceptance. This directory is the only active product documentation set.

| Document | Purpose |
| --- | --- |
| [Current state](current-state.md) | Executive result, pinned repositories, present surface and current blockers |
| [Architecture](architecture.md) | Product-native ownership and target invariants, clearly separated from current deviations |
| [Feature status](feature-gap-report.md) | Evidence-based status for 65 selected domains plus explicit research-candidate decisions |
| [Inventory CSV](feature-inventory.csv) | Machine-readable mirror of the same IDs, priorities and statuses |
| [Next-stage plan](next-stage-plan.md) | Dependency-ordered implementation and release plan |
| [Implementation tasks (Chinese)](implementation-task-list.zh-CN.md) | Executable task cards and acceptance requirements |
| [Risk register](risk-register.md) | Open, partial, controlled and accepted risks |
| [Verification](verification.md) | Commands actually run, results, limitations and post-repair gates |

Current release posture: **implementation in progress; paired release not
certified**. The empty-schema migration, canonical/vendor contract, database test
gate and Scheduler benchmark now pass on the latest Platform head. Remaining gaps
are the uncommitted Gateway worktree/build, stale superproject pins, runtime/browser/
fault-load evidence, live Provider/operations proof and a high-severity `nanoid`
dependency advisory in both Web applications.

The selected inventory still contains 65 domains, but statuses are being re-promoted
against the current evidence in [current-state](current-state.md) and
[verification](verification.md). Historical logs and commit messages do not
override this snapshot, and no row is promoted merely because its source exists.
