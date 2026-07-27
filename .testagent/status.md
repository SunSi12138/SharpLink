# 0.8.39 test status

## Evidence status

- Exact baseline is local 0.8.38 commit `d863dc31f74e49f5cb48de59ba43436510702933`.
- All five P2 roots have deterministic pre-fix evidence: Generator retained 117 existing passes and
  failed only the generated-wire proof; Unit failed only the empty-request proof; Integration
  retained nine interceptor passes and failed exactly four new proofs.
- Post-fix non-incremental Release is 0 warnings/errors; Generator is 118/118, Unit 484/484, and
  Integration 246/246 after the stream/OneWay pseudo-mutation controls were added.
- Exact-baseline/candidate interceptor process medians are 41.267/40.831 microseconds (-1.06%)
  with unchanged approximately 1,584.02-1,584.05 B/op.
- The 120-second shared-memory Chaos gate passed with 837,357 successes, 330,087 expected
  failures, zero unexpected failures, 23 restarts, zero Client/Server Errors, and all five final
  metrics at zero. NativeAOT TCP and fresh-cache 0.8.39 package TCP/shared-memory smoke passed.
- Consecutive complete audit rounds without a new improvement remain 0/3.

## Current gate

- The 0.8.39 batch is locally committed after final source review. Verify exact-commit package
  metadata and fresh-cache package gates, then begin the next audit batch from the clean exact HEAD.
