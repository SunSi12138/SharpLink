# 0.8.16 test status

## Evidence status

- Five P2-or-higher candidates were identified from the clean `0.8.15` commit `8b6eeaa`.
- The complete pre-fix Unit run executed 422 cases: all 417 prior cases passed and exactly five focused probes failed.
- The native timer proof produced `ArgumentOutOfRangeException` with a 4,294,967,294 ms ceiling; the other probes directly observed retained buffers, a successful Stop despite listener failure, hosted shutdown after startup-token cancellation, and acceptance of a 2,097,152-slot pending table.

## Current gate

- Verified P2-or-higher candidates with failing evidence against `8b6eeaa`: 5/5.
- Unit is 422/422 after the fixes.
- Assertion/pseudo-mutation review passed: removing timer slicing, pool draining/closure, Stop failure propagation, Hosted lifetime separation, or either pending-capacity boundary is detected by a focused assertion.
- Reversed same-machine A/B found no stable hot-path regression and no allocation change. Buffer rent/return and 32-byte packets remain allocation-free; full results are in `doc/performance-0.8.16.md`.
- Version, changelog, README, and Chinese/English audit, migration, and performance documentation are complete.
- Non-incremental Release build passed with 0 warnings and 0 errors; Generator 83/83, Unit 422/422, Integration 228/228, seven-package pack, and fresh-cache package smoke all passed.
- Targeted formatting, whitespace validation, and final diff review passed; the 0.8.16 batch is ready for its local commit.
- Consecutive complete audit rounds without a new improvement: 0/3.
