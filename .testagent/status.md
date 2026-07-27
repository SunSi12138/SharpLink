# 0.8.22 test status

## Evidence status

- Five P2 candidates were proven against clean 0.8.21 commit `481989c`.
- Pre-fix Integration was 231 existing passes plus exactly five new failures; pre-fix Generator was 83 existing passes plus exactly one new structural failure.
- The probes observed malformed Boolean, Rune, decimal, DateOnly, DateTime, TimeOnly, and DateTimeOffset acceptance, plus raw DateTimeOffset padding on the generated wire.

## Current gate

- Generator 84/84, Unit 445/445, and Integration 236/236 pass after the focused fixes.
- Assertion-quality and pseudo-mutation review covers each semantic reader, DateTimeOffset padding, and nullable generated siblings.
- The rejected length-delimited implementation measured 66/109 ns; final fixed-wire A/B retained allocations and reduced the total cost to about 1–2 ns.
- Non-incremental Release build passed with 0 warnings and 0 errors; seven-package pack and fresh-cache package smoke passed.
- A resource-contended parallel suite run observed one unrelated shared-memory early EOF; the complete Integration suite passed again when rerun independently. Truncated-handshake error normalization is retained as a separate next-audit candidate.
- Version and Chinese/English audit, migration, performance, changelog, and README documentation are complete.
- Consecutive complete audit rounds without a new improvement: 0/3.
