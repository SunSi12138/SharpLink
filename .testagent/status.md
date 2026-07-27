# 0.8.40 test status

## Evidence status

- Exact baseline is local 0.8.39 commit `8fffab767ce76c70a6c459148a288a07594e69ea`.
- All five candidates have deterministic pre-fix failures against unchanged 0.8.39 production,
  causal fixes, negative controls, and reviewed pseudo-mutation coverage.
- Non-incremental Release build: 0 warnings, 0 errors.
- Complete post-fix suites: Generator 119/119, Unit 486/486, Integration 250/250.
- Exact pre-existing retry regression that caught duplicate exception aggregation: 1/1 passed after
  preserving shared exception identity.
- Exact-baseline/candidate intercepted process medians: 39.845 -> 40.234 microseconds (+0.98%,
  overlapping ranges); allocation: approximately 1,584 -> 1,560 B/op. Plain allocation remains
  approximately 320 B/op and `RpcMethodDescriptor` is 40 versus 48 bytes.
- Final 120-second shared-memory Chaos: 817,230 success, 318,950 expected, zero unexpected, 23
  restarts, zero Client/Server Errors, 222 ms maximum recovery, drained with five zero metrics.
- NativeAOT TCP, seven-package pre-commit pack, and fresh-cache TCP/shared-memory functional smoke
  passed.
- Consecutive complete audit rounds without a new improvement remain 0/3.

## Current gate

- Finish documentation review, run the final versioned build/test gate, commit locally, then repack
  from the exact commit and verify package repository/version metadata.
