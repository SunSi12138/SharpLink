# 0.8.12 test status

## Pre-fix evidence

- A filtered pre-fix run executed exactly the five new tests and failed 5/5; the committed 0.8.11 baseline had Unit 394/394.
- Direct Client transport and dynamic resolver failures skipped their owned resource cleanup.
- Server validation lost its primary error when Runtime Context cleanup failed, while logger construction failure skipped Context cleanup entirely.

## Final gate

- Verified P2-or-higher improvements: 5/5 (direct Client profile rollback; direct Client constructor rollback; dynamic resolver rollback; Server validation rollback; Server constructor rollback).
- Targeted tests 5/5 and full Unit 399/399 pass after the fix. Non-incremental Release build has 0 warnings/errors; Generator 83/83 and Integration 228/228 pass.
- Pseudo-mutation review: removing any of the five cleanup calls, swapping the cleaned owner, losing either exception, or double-disposing is killed by message plus disposal-count assertions. Existing successful builder tests cover ownership transfer after a successful return.
- Assertion review: all five tests verify two diagnostic facets plus one lifecycle side effect; there are no assertion-free or trivial-only tests in the new file.
- Reversed A/B runs retained 6.37/7.38 KB for direct/dynamic Client Build/Dispose and showed no stable sub-1% latency regression. Server allocation fell from 12.94 to 12.88 KB with flat-or-better latency. The first branch-local exception-boundary candidate was rejected before the final cold outer rollback design.
- One intermediate full-Unit rerun exposed a pre-existing shared-memory stale-file scan race; the isolated test and immediate full rerun passed. The race is retained as a 0.8.13 audit candidate rather than hidden with a skip or serialization attribute.
- Version 0.8.12, Chinese/English audit, migration, performance, README, changelog, package, and independent smoke documentation are complete.
