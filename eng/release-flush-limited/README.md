# FLUSH1 — limited current-head #964 follow-up

Dormant until exact tool/workflow review and a sole authorized request child.
This does not execute the 378-shard matrix and never assigns performance acceptance.

## Frozen scope

- Baseline `57f06f172d29fa5cfd4ea8e9b86a23f122df24ab` (dev, **not official2.0.3**).
- Candidate `8ae9ee36ebec82f82e03185432601a830c865e95`, tree `1f0c9291c7394d012d3241a9cf31482b0734b697`.
- Ubuntu24.04 standard hosted VM, x64, exact SDK10.0.114 and actual runtime10.0.14.
- Existing `RpcSessionFlushPolicyEvidenceRunner` reused: loopback TCP generated
  unary EchoBytes, Balanced, payload32 and4096, concurrency1, effective flush
  threshold16384/maxLatency1ms. 32B is a nearby control;4096B/c1 is the adverse cell.
- States A=dev57 no-update (historical CLI label `static-baseline`), B=current runtime-no-update, C=current post-update. Both sources already have runtime-capable immutable flush generations; this is not a static-to-runtime implementation comparison or causal attribution.
  C retains the original eight alternate→original flush-policy replacement cycles
  on both live endpoints, ending with identical effective policy before measurement.
  This is RPC flush policy replacement, **not cooperative implementation maintenance**.

Each fresh process measures both payloads in original32→4096 order. Each cell
retains original1024-operation warmup, five4096-operation rounds, fullGC before
each round, perRPC Stopwatch samples, process-wide GC allocation, and original
interpolated P50/P99 formula. No sample capacity, percentile algorithm, operation
count, failure handling or comparison threshold was relaxed. Added serialization
happens after measurements; raw arrays are the same original sorted arrays.

The six predeclared process orders are ABC, ACB, BAC, BCA, CAB, CBA. Every state
occurs once in every three-process block. This is18 processes,36 cells,180 raw
rounds,737280 measured RPCs plus36864 warmup RPCs. No retries, replacement samples,
selective dropping or adaptive stopping. A failure retains artifacts and stops
later launches; incomplete evidence is not accepted. All comparisons retain each
block's per-cell five-round medians and both ratios; no pooled global average.

Connection/setup, policy updates, warmup, fullGC and teardown are outside the
steady-state timer. QPS is logical completed RPC count / round walltime, not the
inverse of a percentile. Latency is request-await walltime measured for each RPC.
Allocation covers the full process/client/server/background/harness during the
same round; it is not isolated policy-generation allocation. Raw exact allocated
bytes (including counter start/end), CPU TimeSpan counter start/end, stopwatch frequency, elapsed stopwatch and TimeSpan ticks and all sorted latency ticks allow
independent recomputation. Timing-only floor is workload-dependent, unlike the
9-second official2.0.3 fixture. Each process is capped at180s; build at900s/arm;
the hosted job cap is45minutes. No other workload is run concurrently in that VM;
physical isolation or noise-free measurements are not claimed.

The existing benchmark runner source is copied to an evidence-only overlay; only
its finite arrays, raw-output fields and runtime/provenance guard differ. Existing
Program and admission-smoke helper are copied byte-for-byte as in the old workflow.
Product `src/**` is untouched. Exact source hashes, overlay hashes, initial git
status, executed closure hashes and closure copies, tool/commit/tree, actual host/
CoreLib/CoreCLR/JIT/hostpolicy hashes, OS/CPU and command/launch receipts remain.
Candidate/baseline build identities and runtime files are not inferred from labels.

## Interpretation and retention

Historical #964 run40983957901 compared dev57→402ae and found Balanced4096/c1
QPS−31.86%, P99+652.87% (baseline162.19us); it retained only five-round medians.
This is a **new current-head observation**, not a rerun to erase that signal, not
causal attribution, and not proof of official2.0.3 equivalence. The old failure
signal remains regardless of these new values. Five rounds per process and six
order-balanced blocks do not establish confidence bounds or general equivalence.
No new pass/fail performance threshold or noise waiver is introduced.

Three-day temporary Actions artifacts preserve raw successes and failures; no
external evidence branch, evidence PR comment, release/tag/NuGet or permission
change is included. Workflow is request-only first-attempt push, read-only token,
pinned actions, and no retry/overwrite. Initial dormant-tool push cannot execute.

Offline controls run no CLR or workload:

```sh
PYTHONDONTWRITEBYTECODE=1 python3 -m unittest discover \
  -s eng/release-flush-limited -p 'test_*.py' -v
```
