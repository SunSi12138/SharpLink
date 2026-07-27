# 0.8.43 test status

- Exact baseline: 0.8.42 commit `cd2de157e05fd4b3d97dd34f056871c2c9d95ee8`.
- Findings 1–5 are proven and fixed: shared-memory cleanup race; redundant empty flow-credit lock;
  implicit connection-close code loss; successful telemetry on early client stream disposal; and
  stale dynamic selection recreating retired admission-policy state.
- Preserved pre-fix logs are under `artifacts/`; the flow-control causal A/B and 0.7.11 boundary
  evidence are in the isolated performance checkout
  `/Users/sunsi/Desktop/SharpLink-agent-workspace/feature-perf-0.7.11-vs-0.8.41-20260727-a`.
- Final non-incremental Release builds with 0 warnings/errors; Generator 121/121, Unit 496/496,
  and Integration 252/252 pass serially.
- Three alternating exact-0.8.42/candidate Balanced full-stream pairs completed without failures.
  Paired-median QPS changed +1.5% c2s, -1.8% s2c, +4.0% duplex, and -0.6% for the unary control;
  P50/P99 show no material regression.
- The 120-second shared-memory Chaos gate passed with 812,602 successes, 325,934 injected expected
  failures, zero unexpected failures, 23 restarts, zero Client/Server Errors, 324 ms maximum
  recovery, a successful drain, and all five final gauges at zero.
- NativeAOT TCP reports `AOT_SMOKE_PASS`; seven 0.8.43 packages, SDK Generator contents, and a
  fresh-cache TCP/shared-memory/static/dynamic functional package smoke pass.
- Bilingual audit, migration, performance, changelog, version, README, plan, and todo updates are
  complete. Versioned final rebuild/retests and diff/readiness review pass; the tree is ready for
  the local 0.8.43 commit.
- The isolated flow-control Integration failure is retained as an observation only; source ordering
  and two clean reruns do not meet the evidence threshold for a product change.
- Consecutive complete audit rounds without a new improvement remain 0/3.
