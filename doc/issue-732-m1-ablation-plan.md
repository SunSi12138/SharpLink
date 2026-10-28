# #732 M1: isolate cached descriptor projection before another release decision

Status: **proposed, not executed**. M0 remains performance No-Go. M1 changes only the private representation of `RpcMethodFacts` and its scalar projections. It does not change the once-only resolver, weak queued selection, original-generation lease, call-state pool, generated API5 identity, or wire.

## Question and arms

Does caching the complete observer descriptor reduce the M0 intercepted TCP stream cost, specifically where one descriptor projection occurs per call?

Use **A = committed M0** (`37548b7a`) and **B = reviewed M1** (the final child commit of A). These are new campaign labels: the historical campaign's A was pre-M baseline `a6bd0352`, not this A. Freeze both new arms after the independent review; use the same SDK/runtime, harness source, build settings, default tiering/PGO, transport, payload, and timeout policy. Do not combine the rejected pool experiment or unrelated #931 changes in only one arm.

The hypothesis predicts an interceptor-specific improvement; no-interceptor calls make no descriptor projection. Actual M1 cold allocation/codegen and semantic validation are prerequisites, not timing evidence. A 1–2 ns isolated projection estimate is far smaller than the historical several-microsecond adverse cell, so disappearance of that regression would require stronger evidence than this mechanism alone.

## Four cells, fixed before execution

All use TCP, one returned integer, concurrency 1, the original synchronous `Range` service, no admission controller, and telemetry disabled.

| Cell | Interceptor | Method | Role |
|---|---|---|---|
| I/T | pass-through | declared timeout + cancellation | Primary; historical adverse shape |
| P/T | none | declared timeout + cancellation | Negative projection control |
| I/U | pass-through | untimed/noncancellable | Projection without initial per-call tracking |
| P/U | none | untimed/noncancellable | Negative projection and tracking control |

Before timing, run the separate blocking preflight once per arm. It must verify request-id/state/token association and the synchronous untimed no-state control. Do not add probes, counters, reflection, or gates to the timed service loop.

## Eight pairs, including A/A and B/B controls

Run the exact process-pair order **AA, AB, BB, BA, BA, BB, AB, AA**: 16 fresh processes, four cross-arm pairs and four same-arm pairs. Each process executes the four cells in the same published order above, 1,000 warmup calls/cell, then five samples of 2,000 completed calls/cell. Total measured calls: **640,000**. Keep every per-call tick, sample, failure, GC/allocation/CPU record, and process start/end timestamp. Report B/A for each cross-arm pair regardless of chronological direction; report second/first for same-arm pairs.

Reserve a build/test/publish-free measurement window among cooperating agents. Record other observed workload rather than killing user processes. Freeze the plan and arms before the first timed process. A missing/failed cell stops the campaign and yields incomplete evidence; it does not authorize replacement samples. Do not extend, discard outliers, rerun adverse pairs, or tune implementation during this campaign.

## Interpretation and stop condition

Report per-process means and tail quantiles; the four process pairs, not individual RPCs, are the independent comparison units. Show all pair ratios and their geometric mean, alongside AA/BB ratios. Compare log(B/A) in I/T versus P/T and I/U versus P/U to separate the interceptor interaction from broad machine drift. Treat P99 as a separately reported diagnostic with its own pair ratios.

A credible mechanism signal requires consistent cross-pair direction and an interceptor-specific difference larger than the observed same-arm drift. If same-arm variation is comparable to the apparent treatment effect, or the plain control moves similarly, conclude **inconclusive** and stop this bounded campaign. Do not call statistical non-significance equivalence, and do not derive a release tolerance from the observed samples.

Even a favorable result answers only M0 versus M1. It does not establish M1 versus pre-M baseline equivalence or clear the full 3.0 release gate. Any later release-performance campaign needs its own approved baseline comparison and acceptance margin. The #931 exact-head combined validation and the unexplained historical BootstrapManifest timeout remain separate acceptance dependencies owned by the main release work.
