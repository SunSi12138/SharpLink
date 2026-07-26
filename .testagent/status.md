# 0.8.11 test status

## Pre-fix evidence

- Unit 394 total: exactly five new regressions failed against unchanged production code while the prior 389 tests passed.
- Client/Server runtime registration and replacement each lost their structured Codec conflict when generated Adapter Scope cleanup threw.
- Server profile binding failed before entering its cleanup boundary, leaving the newly built Runtime Context undisposed.

## Final gate

- Verified P2-or-higher improvements: 5/5 (Client registration rollback; Server registration rollback; Client replacement rollback; Server replacement rollback; Server profile-binding Context rollback).
- Release build passed with 0 warnings/errors. Generator 83/83, Unit 394/394, Integration 228/228, package generation/analyzer verification, and independent package restore/run smoke passed.
- Two reversed-order A/B runs measured normal Client registration/unregistration at 6.535 → 6.518 µs and Server at 6.407 → 6.407 µs. Client allocation fell from 30.50 to 30.44 KB; the repeated Server run was 29.52 KB on both revisions.
- Version 0.8.11 and Chinese/English audit, migration, performance, README, and changelog documentation are complete.
