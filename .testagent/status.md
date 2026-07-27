# 0.8.23 test status

## Evidence status

- Five P2 candidates were proven against clean 0.8.22 commit `3a4338d`.
- Pre-fix Unit was 445 existing passes plus exactly four new failures; pre-fix Integration was 236 existing passes plus exactly one new failure.
- The probes observed malformed semantic collection acceptance, DateTimeOffset padding propagation, and raw EOF leakage from Client Connect.

## Current gate

- Focused Unit 449/449 and Integration 237/237 pass after the fixes; Generator remains 84/84.
- Assertion-quality and pseudo-mutation review covers every collection shape, each semantic family, DateTimeOffset padding, and handshake error classification.
- Final A/B restored ordinary int/bool writes to baseline and retained all allocations; bounded validation costs about 0.3/1.4 ns per Boolean/DateTimeOffset element.
- Non-incremental Release build passed with 0 warnings and 0 errors; final Generator 84/84, Unit 449/449, and independently run Integration 237/237 passed.
- Seven-package pack and fresh-cache package smoke passed.
- An intermediate repeated Integration run hit macOS certificate-loader and unrelated transport flakes; the unchanged binaries passed the complete independent final run. These are retained as test-infrastructure audit signals rather than counted as product fixes.
- Version and Chinese/English audit, migration, performance, changelog, and README documentation are complete.
- Consecutive complete audit rounds without a new improvement: 0/3.
