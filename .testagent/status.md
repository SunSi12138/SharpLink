# 0.8.41 test status

## Evidence status

- Exact baseline is local 0.8.40 commit `dd431f5b18fc0e98b24a5f6edddd45a320d8fe23`.
- All five candidates have deterministic pre-fix failures, causal fixes, required/nullable or
  concrete/reserved controls, and reviewed pseudo-mutation coverage.
- The 0.8.40 exact-commit build/package/AOT gates are complete and the working tree was clean before
  this Research/Plan update.
- Consecutive complete audit rounds without a new improvement remain 0/3.

## Current gate

- Final candidate: non-incremental Release 0 warnings/errors; Generator 120/120, Unit 490/490,
  and serial Integration 250/250 passed. A parallel Integration run exposed existing certificate
  import/test-resource races; the unchanged tests all passed in the serial release gate.
- Exact-0.8.40/candidate five-process TCP medians changed +0.36% without interceptors and +0.98%
  with interceptors, with unchanged allocations. Required-reference stream dispatch measured an
  identical 13.860 ns/op process median and 1.333 B/op.
- 120-second shared-memory Chaos passed with 815,964 success, 316,929 expected, zero unexpected,
  23 restarts, no Client/Server Error, 221 ms maximum recovery, and all final gauges zero.
- NativeAOT TCP, seven-package pre-commit pack, and fresh-cache TCP/shared-memory functional smoke
  passed. The legacy two-argument public stream-dispatcher Rent overloads remain present for binary
  compatibility; an exact-0.8.40-compiled stream harness ran successfully after swapping in the
  0.8.41 Runtime and Abstractions assemblies.
- Bilingual audit, migration, and performance documentation and the final diff/readiness review are
  complete; the batch is ready for its local 0.8.41 commit.
