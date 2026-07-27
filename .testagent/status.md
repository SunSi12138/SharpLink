# 0.8.44 test status

- Exact baseline: 0.8.43 commit `9789fbedd5af5e4b2b21be84684150047f26c6e2`.
- Three findings are proven and fixed under independent-root-cause accounting. The Server/Client
  `Task.WhenAll` manifestations are one shutdown-join defect, not three findings. The other two
  findings cover Server call-admission cleanup after a rejected terminal response and flow-control
  slot cleanup after a rejected terminal stream frame.
- All deterministic lifecycle witnesses fail on the baseline and pass after their fixes; cleanup
  still completes and the original unexpected failures remain observable.
- The first convergence scan found and closed the static endpoint-cluster worker manifestation of
  the same shutdown-join root cause; it does not increase the three-finding count.
- The former five-finding release threshold has been retired. This round closes with its three
  actual root causes; additional call sites, theoretical races, defensive-only changes, and syntax
  modernization neither inflate the count nor delay the release.
- The rejected multi-cluster cancellation-callback hypothesis left no source or test changes.
- Final gates pass: non-incremental Release has zero warnings/errors; Generator 121/121, Unit
  503/503, Integration 252/252; exact-baseline performance, 120-second shared-memory Chaos,
  independent-process SharedMemory NativeAOT, seven-package pack, and fresh-cache PackageSmoke.
- Bilingual audit, migration, performance, changelog, version, README, plan, and todo updates are
  complete; final diff/readiness review passes and the tree is ready for the local 0.8.44 commit.
- Consecutive complete audit rounds without a new improvement remain 0/3.
