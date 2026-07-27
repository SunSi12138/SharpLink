# 0.8.26 test status

## Evidence status

- Five P2 candidates identified against clean 0.8.25 commit `0773496`.
- Complete pre-fix Generator run: 100 total, all 96 existing tests passed, and exactly four new Generator probes failed.
- The initial Unit probe failed, but source inspection proved its premise wrong: nested item lengths are fixed UInt32, not VarUInt32. The probe and attempted bound relaxation were removed and do not count.
- Revised fifth evidence captured a private default interface helper appearing in Proxy, Stub, and Manifest output. The final five recommendations are now fully evidenced.

## Current gate

- Focused Generator 101/101 and Unit 449/449 pass after implementing all five fixes; the exact/ambiguous DTO and valid/invalid Oneway branches are covered.
- Assertion/pseudo-mutation review covers Task<T>/ValueTask<T>/stream Oneway shapes, chained local-name collisions, exact and ambiguous case-insensitive DTO mapping, dictionary null/duplicate keys, private default helpers, and protected abstract methods.
- The first Generator candidate regressed median latency by about 10% and was rejected. After combining attribute traversals and removing a captured naming lambda, the 101-sample median is 14.755 → 13.530 ms; allocation is +76,640 B (0.27%). The isolated 16-key dictionary guard is 171.891 → 170.941 ns.
- The exact final tree passed a non-incremental Release build with 0 warnings/errors, Generator 101/101, Unit 449/449, Integration 237/237, seven-package pack, SDK analyzer-content inspection, and fresh-cache package smoke resolving only 0.8.26 SharpLink packages.
- Version and Chinese/English audit, migration, performance, changelog, README, SDK XML, and analyzer-release documentation are complete. The local 0.8.26 commit is ready.
- Consecutive complete audit rounds without a new improvement: 0/3.
