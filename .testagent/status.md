# 0.8.42 test status

- Exact baseline: 0.8.41 commit `d0e0df4e3e81b9ead5c416c9a33ebd459c55f5a1`.
- Five candidates have deterministic source/load-boundary evidence; all 120 established Generator and
  490 established Unit tests remained passing outside the new/modified witnesses.
- The five scoped implementations and paired controls are complete. The P1 Throughput crash changed
  from two exact-baseline `operation=all` exits with code 134 to 16/16 successful candidate processes
  and 64/64 successful operation stages.
- Versioned non-incremental Release built with zero warnings/errors; Generator 121/121, Unit 493/493,
  and serial Integration 250/250 passed. One earlier Integration pass had two non-repeating timing
  failures; two subsequent complete serial passes were clean and the evidence remains preserved.
- Ten-process exact-baseline/candidate TCP unary medians changed -0.76%, with unchanged P50, stable
  P99, and slightly lower allocation. Nullable present decode improved 5.155 -> 5.090 ns/op; canonical
  null validation was 5.444 -> 5.937 ns/op with zero allocation. A 10.3/21.5 ns wrapper was rejected.
- The 120-second shared-memory Chaos gate completed 814,834 success, 319,230 expected, zero unexpected,
  23 restarts, no Client/Server Errors, 218 ms maximum recovery, and all final gauges zero. NativeAOT
  TCP, seven-package pre-commit pack, fresh-cache TCP/shared-memory package smoke, bilingual docs, and
  final diff/readiness review passed.
- Consecutive complete audit rounds without a new improvement remain 0/3.
