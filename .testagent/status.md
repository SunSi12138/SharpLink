# 0.8.3 test status

## Pre-fix evidence

- Unit 347 total: 3 failures for synchronous Stop callback, masked connect failure, and masked Hosted startup failure.
- A fourth mutation probe failed for nested endpoint Attributes (348 total).
- Metadata baseline: construction 10.47 ns / 80 B; decode 68.33 ns / 280 B.

## Focused result

- Unit 348/348 after the fixes.
- Metadata candidate: construction 10.13 ns / 80 B; decode 61.89 ns / 224 B.
- Public params-span experiment had no allocation benefit and was withdrawn before release.

## Final gate

- Versioned Release passed with 0 warnings and 0 errors.
- Generator 83/83, Unit 348/348, and Integration 227/227 passed.
- `git diff --check` and final source/assertion review passed.
