# 0.8.15 test status

## Evidence status

- The full pre-fix Unit probe retained 410 passing cases and failed seven focused cases across five findings: Unix path preservation, mutable endpoint snapshotting, built-in endpoint-factory option freezing, direct Client resource transfer (transport and resolver), and Server listener ownership (second build and failed rollback).
- Style-only findings and extreme-duration validation are explicitly excluded from the version threshold.

## Current gate

- Verified P2-or-higher improvements: 5/5 with failing evidence against commit `b32f846`.
- Unit is 417/417 after the fixes.
- Assertion/pseudo-mutation review passed: restoring pre-bind deletion, retaining the supplied endpoint, retaining any tested option object, leaving either Client resource in its builder, omitting Server transfer, or omitting listener rollback is detected by a focused assertion.
- Reversed same-machine A/B found no RPC hot-path regression. Configuration snapshots add bounded one-time allocation/cost documented in `doc/performance-0.8.15.md`; Client/Server Build allocation is unchanged.
- Version, changelog, README, and Chinese/English audit, migration, and performance documentation are complete.
- Non-incremental Release build passed with 0 warnings and 0 errors; Generator 83/83, Unit 417/417, Integration 228/228, seven-package pack, and fresh-cache package smoke all passed.
- Targeted formatting and final diff review passed; the 0.8.15 batch is ready for its local commit.
