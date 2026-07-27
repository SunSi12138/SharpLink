# 0.8.21 test status

## Evidence status

- Five P2-or-higher candidates were proven against clean 0.8.20 commit `726992c`.
- Pre-fix Unit was 441 existing passes plus exactly four new failures; pre-fix Integration was 230 existing passes plus exactly one new failure.
- The probes observed malformed path replacement, null-collection trailing acceptance, two independent outbound Unicode mutations, and a leaked module call lease.

## Current gate

- All five fixes pass Unit 445/445 and Integration 231/231; Generator remains 83/83.
- Pseudo-mutations independently fail the corresponding focused probe.
- Metadata construction retained 136 B/op and baseline latency. Strict metadata sizing/string output intentionally add about 2/4 ns at 0 B/op; the slower extra-scan design was rejected.
- Two zero-reference internal helpers were removed without public or runtime consumers.
- Version, changelog, README, and Chinese/English audit, migration, and performance documentation are complete.
- Non-incremental Release build passed with 0 warnings and 0 errors; seven-package pack and fresh-cache package smoke passed.
- Consecutive complete audit rounds without a new improvement: 0/3.
