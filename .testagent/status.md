# 0.8.20 test status

## Evidence status

- Five P2-or-higher candidates were identified from the clean `0.8.19` commit `2d7cd95`.
- The complete pre-fix Unit run executed 441 cases: all 436 prior cases passed and exactly five focused probes failed.
- The probes directly observed accepted over-range handshake configuration, immediate failure of far-future readiness and pending-slot deadlines, an early Server graceful wait, and replacement decoding of malformed generated string bytes.

## Current gate

- Verified P2-or-higher candidates with failing evidence against `2d7cd95`: 5/5.
- All five production fixes pass the complete 441-case Unit suite.
- Assertion and pseudo-mutation review confirmed that each probe independently detects removal of the timer bound, either Client wait slice, Server wait slicing, or strict malformed UTF-8 handling.
- Alternating A/B runs retain 64 B/op for contiguous and 112 B/op for segmented valid string decode. Contiguous latency overlaps baseline; segmented replacement-marker detection adds about 3.5 ns (roughly 3%) with no extra allocation.
- Always-strict decoding and pre-validating every byte were rejected after measuring roughly 8% and 10% regressions; strict revalidation now occurs only for decoded values containing U+FFFD.
- Version, changelog, README, and Chinese/English audit, migration, and performance documentation are complete.
- Non-incremental Release build passed with 0 warnings and 0 errors; Generator 83/83, Unit 441/441, Integration 230/230, seven-package pack, and fresh-cache package smoke all passed.
- Targeted formatting, whitespace validation, final tests, and diff review passed; the 0.8.20 batch is ready for its local commit.
- Consecutive complete audit rounds without a new improvement: 0/3.
