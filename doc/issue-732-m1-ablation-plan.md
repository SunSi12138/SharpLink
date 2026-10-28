# #732 M1: isolate cached descriptor projection before another release decision

Status: **proposed, not executed**. M0 remains performance No-Go. M1 changes only the private representation of `RpcMethodFacts` and its scalar projections. It does not change the once-only resolver, weak queued selection, original-generation lease, call-state pool, generated API5 identity, or wire.

## Question and arms

Does caching the complete observer descriptor reduce the M0 intercepted TCP stream cost, specifically where one descriptor projection occurs per call?

Use **A = committed M0** (`37548b7a`) and **B = reviewed M1** (production source at `652ffca7`, including its subsequently reviewed documentation-only commits). These are new campaign labels: the historical campaign's A was pre-M baseline `a6bd0352`, not this A. Freeze both new arms after the independent review; use the same SDK/runtime, harness source, build settings, default tiering/PGO, transport, payload, and timeout policy. Do not combine the rejected pool experiment or unrelated #931 changes in only one arm.

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

Before the first timed process, save one execution manifest containing:

- The exact reviewed plan revision, full A/B source commits, build-output paths/module identities, and the common harness source revision/path. Record the complete build and execution commands for each arm, including working directories and arguments.
- The actual SDK and JIT runtime versions, OS version, CPU architecture/model, and processor count. Use the same SDK/runtime for both arms; do not infer these identities from an earlier campaign.
- Default tiering, PGO, and GC, with the effective environment/runtime configuration recorded. No affinity, processor-count, tiering, PGO, or GC override is allowed.
- Power source and power/low-power mode, plus observable background load at the start and during execution. Record any power-mode change or competing build/test/publish activity with timestamps.

Reserve a build/test/publish-free measurement window among cooperating agents. Record other observed workload rather than killing user processes. These inputs and the approved plan must be fixed before timing; an unfilled execution manifest is not permission to start. A missing/failed cell stops the campaign and yields incomplete evidence; preserve the failed process stdout/stderr and all partial samples. It does not authorize replacement samples. Do not extend, discard outliers, rerun adverse pairs, or tune implementation during this campaign.

## Fixed aggregation and paired calculations

For process `j`, cell `c`, and sample `s = 1..5`, let `E[j,c,s]` be the whole-sample `elapsedTicks`, `F[j,c,s]` its recorded stopwatch frequency, and `N[j,c,s]` the successfully completed call count (required to be 2,000). The process mean in ns/call is:

```text
M[j,c] = 1e9 * sum_s(E[j,c,s] / F[j,c,s]) / sum_s(N[j,c,s])
```

This is the original whole-sample elapsed-time estimator, including the timed harness overhead. Do not substitute the mean of individual call ticks or an average of sample throughput values. CPU time and allocated bytes are also summed across the five measurement windows and divided by the total completed count. Warmup is excluded from every aggregate.

For each process/cell P99, convert every measured call tick to ns using its sample's frequency, pool all 10,000 calls from the five samples, and sort ascending. Use the nearest-rank quantile: one-based rank `ceil(0.99 * 10000) = 9900` (zero-based index 9899), with no interpolation. Do not average sample P99s or pool different processes. Preserve all five sample records and per-call values as well as these process aggregates.

For each of the four cross-arm process pairs `p`, orient the comparison as B/A even for a chronological BA pair. Define the mean-time log ratio and interceptor interactions:

```text
L[p,c] = ln(M[B in pair p,c] / M[A in pair p,c])
D[p,T] = L[p,I/T] - L[p,P/T]
D[p,U] = L[p,I/U] - L[p,P/U]
```

For each of the four same-arm pairs `q` (both AA pairs and both BB pairs), retain chronological second/first orientation:

```text
S[q,c] = ln(M[second in pair q,c] / M[first in pair q,c])
H[q,T] = S[q,I/T] - S[q,P/T]
H[q,U] = S[q,I/U] - S[q,P/U]
noise[T] = max_q(abs(H[q,T]))
noise[U] = max_q(abs(H[q,U]))
```

Report every `L`, `D`, `S`, and `H`, and the two noise envelopes. For each cell report all four cross-arm ratios `exp(L[p,c])` and their geometric mean `exp(sum_p(L[p,c])/4)`, plus every same-arm ratio `exp(S[q,c])`. Report T/U interaction summaries separately as `exp(sum_p(D[p,T])/4)` and `exp(sum_p(D[p,U])/4)`; never combine T with U. For P99, report each process value, all four B/A pair ratios and their geometric mean, plus each AA/BB second/first ratio. P99 is a separate diagnostic, not an input to the mean-time decision below.

## Descriptive mechanism decision and stop condition

Apply these rules mechanically after the prescribed campaign, without changing the envelope or selecting pairs:

- **Primary T favorable exploratory signal:** all four `L[p,I/T] < 0` and all four `D[p,T] < -noise[T]`. Thus every pair improves the intercepted timed mean and its interceptor interaction exceeds the largest absolute same-arm interaction observed in either arm.
- **Primary T adverse exploratory signal:** all four `L[p,I/T] > 0` and all four `D[p,T] > noise[T]`.
- **Otherwise:** primary T is **inconclusive**, including equality at zero or at the envelope. Favorable geometric means do not override a failed per-pair condition.
- Apply the same three-way rule separately to U, substituting `I/U`, `D[p,U]`, and `noise[U]`. U cannot rescue an inconclusive/adverse primary T. If T is favorable but U is not, report the signal as specific to the timed shape; the broader projection hypothesis lacks the untimed corroboration.

These are descriptive screening rules for an **exploratory mechanism signal**, not a statistical-significance test, equivalence test, or 3.0 performance tolerance. Four cross-arm pairs do not justify those inferential claims; individual RPCs are not additional independent treatment replicates. The same-arm envelope describes only the drift actually observed in this bounded run. Report adverse P99 pairs beside the mean-time conclusion, rather than hiding them in a favorable average. Stop after the fixed campaign regardless of the outcome; inconclusive or adverse results do not authorize extra samples.

Even a favorable result answers only M0 versus M1. It does not establish M1 versus pre-M baseline equivalence or clear the full 3.0 release gate. Any later release-performance campaign needs its own approved baseline comparison and acceptance margin. The #931 exact-head combined validation and the unexplained historical BootstrapManifest timeout remain separate acceptance dependencies owned by the main release work.
