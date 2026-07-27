# 0.8.36 test status

## Evidence status

- Pre-fix Unit was 479 existing passes plus exactly four new failures (483 total).
- Pre-fix Integration was 239 existing passes plus exactly one new failure (240 total).
- The bounded 192,000-schedule race probe witnessed admission after drain in about 0.47 seconds.
- Post-fix Unit is 483/483 and Integration is 240/240.
- A first three-state-read admission fix was rejected at +6.4%. The final exact-baseline A/B is
  5.1399 -> 5.1706 ns by median of three process medians (+0.60%), with 0 B for both.
- Consecutive complete audit rounds without a new improvement remain 0/3.

## Current gate

- Run exact-final non-incremental build, Generator/Unit/Integration, Chaos, NativeAOT, seven-package
  pack, exact commit metadata verification, and fresh-cache package smoke.
- Commit 0.8.36 locally, then resume whole-framework audit with the clean-round count still 0/3.
