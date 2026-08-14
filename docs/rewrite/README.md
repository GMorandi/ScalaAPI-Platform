# ScalaAPI Rewrite Documentation

Authoritative audit snapshot: 2026-08-14, Platform `bc083d1`, Gateway `b6e4e02`.

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

Current release posture: **blocked**. The reproduced blockers are the empty-schema
migration, canonical/vendor Cap'n Proto drift, the Scheduler benchmark dependency
failure, false-green database test reporting and incomplete operational/runtime
paths. Gateway evidence durability/readiness and current publish scripts also bypass
or overstate required guarantees.

Current selected inventory: **65 domains** = 1 `verified`, 52 `partial`, 7
`scaffold`, 5 `blocked`, 0 `missing` (P0 37 / P1 24 / P2 4).
Historical logs and commit messages do not override this snapshot.
