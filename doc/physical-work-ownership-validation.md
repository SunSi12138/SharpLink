# Physical RPC ownership: scope and validation

Production source comparison: `13928a35df4bff3cd99b14bd1df131aa3eb3bcdb`
to `520b544b56af4d4b298446b96c3a39446abedfa4`, using .NET SDK 10.0.102,
runtime 10.0.2, Linux x64. The latter hash is the local source seal; this branch
contains the same production source, with separate validation/report additions.
This is an ownership correction, not a completed
cold-control redesign or a claim that every cancellation has bounded latency.

## Ownership contract

- A framework-abandoned operation remains owned until its real task finishes.
  Stream cleanup awaits the outstanding operation before calling `DisposeAsync`,
  then awaits disposal before releasing cancellation sources and physical leases.
- Exceptional cleanup starts asynchronously before cancellation callbacks or
  user disposal can run synchronously. A selected public terminal need not wait
  for that cleanup. Unassociated application exceptions retain ordinary disposal
  precedence; they are not treated as locally selected deadlines/cancellation.
- Dynamic client transfer retains the call/producer leases and combined token
  source. Server terminal publication precedes physical cleanup, while the
  existing dispatch task retains request, module, service and DI-scope ownership.
- Failure-only associations follow nested framework terminals. Recycled public
  terminals can form dependency cycles; strongly connected owners finish every
  local cleanup and every linked-source finalizer before any external completion.
  Finalizers run outside the cold graph lock. Completed groups clear their roots.
- Explicit carrier metadata preserves the original logical input to the server
  error mapper, including after cleanup completes. Arbitrary `InnerException`
  chains are not unwrapped. Retry/arbitration replacement exceptions remain new
  logical terminals rather than transparent aliases.

No successful call registers with the owner graph or exception table. There is
no new per-call epoch, catalog lookup, process-wide drain, counter, or retained
full interceptor-state wrapper. The existing exceptional-path flags are reused.
The new failure association and owner objects **do allocate on exceptions**.
Exception handling and native layout change, so this is not an assertion that
every successful instruction sequence is unchanged.

### Deliberate limits

- Static-client `Stop` closure is not solved by this patch. No unused global
  exceptional registry is presented as a reclamation certificate.
- Transitivity follows framework-associated terminals, not arbitrary detached
  application work or application swallowing/replacing of nested failures.
- Untimed uncooperative operations can still delay cancellation. A blocked bare
  response deserializer can keep existing dispatch/disposal draining and delay
  the public terminal. Neither case is permission to reclaim live resources.
- Truly uncooperative cleanup may retain ownership indefinitely.
- A selected terminal may be a fresh carrier. Standard OCE/TCE kind, token and
  message, or SharpLink code/detail/message, are preserved; identity, custom
  subclasses and arbitrary metadata are not cloned. The original is retained
  as the inner exception. In particular, a fresh TCE does not copy its `Task`
  property. User exception/token metadata can itself retain objects independently
  of the cleared physical-owner graph.

## Correctness evidence

The executed filter `/*/*/Abandoned*/*` contains **105** added unit cases, all
passing in ten consecutive final-source runs (**1,050 executions**) and an
independent no-build rerun. The selected inventory is recorded in
[`selected-unit-inventory.json`](../eng/physical-ownership-probes/results-520b544b/selected-unit-inventory.json).
This count comes from execution, not unfiltered test discovery.

- Final client baseline comparison: **42 cases**, baseline **33 fail / 9 pass**,
  candidate **42 pass**. The nine baseline positives include retention and
  ordinary-disposal-precedence controls.
- Final generated-stream baseline comparison: **29 fail** on baseline,
  **29 pass** on candidate.
- Six additional unassociated server-mapper/disposal controls pass on both
  baseline and candidate. Nested mapper tests cover pending and already-finished
  physical children, explicit carrier origins, and nontransparent retry aliases.
- Real TCP dynamic-module/per-call-service tests: baseline **3 fail**, candidate
  **3 pass**, including sequential MoveNext/disposal, module/service/request
  retention and eventual collectible-ALC collection while runtimes remain alive.
  The complete `RuntimeAssemblyIntegrationTests` class independently passes **47/47**.
- Full local unit execution: **2,228 total, 2,200 pass, 28 fail, 0 skipped**.
  The 28 failures concern unavailable named-pipe/shared-memory/UDS operations and
  resulting expected-exception mismatches; all 105 added cases pass in that run.
  This is not a green full-suite claim. Hosted full CI remains necessary.
- Release builds report zero warnings. Project-reference and maintainability
  guards pass; production/test whitespace and diff checks pass.

The unit/performance DLLs were frozen immediately before committing the matching
source. A later integration rebuild stamps the new informational revision and has
different PE/PDB hashes. All executable method bodies, signatures, local signatures,
MaxStack/initLocals and EH regions were compared across those sets and match:
4,427 client, 2,533 runtime and 2,238 server method bodies. Production source hashes
also match the source revision above; different binary hashes are not treated as
identical artifacts.

## Low-core CI fixture correction, 2026-10-22

The published candidate exposed a test-controller scheduling dependency: several
fixtures deliberately block a synchronous user callback or disposer on a framework
worker, but also required another shared-pool continuation to observe entry and
release that gate. The correction moves those **controllers**, not framework
cleanup, onto the existing dedicated test worker. Bounded synchronous task waits
observe completion without a pool-scheduled proxy. Cancellation workers and
publication owners are joined, including assertion-failure cleanup.

The public cyclic-terminal test releases whichever real synchronous disposer enters
first, then keeps its peer blocked and checks that **both** modules and linked token
sources remain owned. It no longer requires two blocked pool workers simultaneously.
Entry and handoff still share the original five-second budget. User callbacks and
disposers remain synchronous; no production code, watchdog, allocation threshold,
parallelism setting, suite serialization, or negative-control expectation changes.

Reproduce with both environment settings explicit, using the intended installed
runtime host:

```sh
DOTNET_PROCESSOR_COUNT=1 TUNIT_MAX_PARALLEL_TESTS=8 \
  dotnet test/SharpLink.UnitTests/bin/Release/net10.0/SharpLink.UnitTests.dll \
  --treenode-filter '/*/*/Abandoned*/*' --progress off --output Detailed
# Omit --treenode-filter for the complete unit inventory.
```

The final test DLL SHA256 is
`8b3e0604a2031b0fc2c39e205d4aaf5f37d46c2a61b97e962e1c5b684ec8e460`.
Five focused runs on official runtime 10.0.13 pass **105/105** each. An independent
no-build run passes **105/105**, the complete LocalAbort class **6/6**, and worker
helper controls **2/2**, with source and DLL hashes unchanged. The exact revised
client fixtures retain **33 baseline failures / 9 baseline positives**; generated
stream fixtures retain **29 baseline failures**. Release build, whitespace,
reference-boundary, and maintainability checks pass.

This correction adds no cases. Including the separate retry-timer registration
witness, the full inventory is **2,229**, or **2,237** with the historical driver's
eight existing injected probes. All final full runs retain their actual results:

| Runtime | Full-run pass / fail | Remaining failures |
|---|---:|---|
| 10.0.2, first | 2,200 / 29 | 28 known IPC failures plus bootstrap-handshake timeout |
| 10.0.2, second | 2,201 / 28 | The same 28 known IPC failures |
| 10.0.13, first | 2,201 / 28 | The same 28 known IPC failures |
| 10.0.13, second | 2,201 / 28 | The same 28 known IPC failures |
| 10.0.13, third | 2,199 / 30 | 28 known IPC failures, bootstrap timeout, and allocation outlier |

Every physical-ownership case passes in these runs. The final run reproduces the
existing `SuccessfulSuspensionsShouldNotAllocateAfterWarmup` failure exactly:
**24,624 bytes instead of zero**. Its same-thread measurement and the runtime's
`PoolingAsyncValueTaskMethodBuilder` implementation are unchanged from the pinned
baseline. Concurrent test activity is context, not an allocation diagnosis. The
existing bootstrap test also intermittently exceeds its two-second handshake
bound. Neither assertion is relaxed or relabeled as an accepted failure. These
observations remain open; this is **not** a green full-suite or production-acceptance
claim, and exact-head hosted checks are still required.

## Successful-path representation and code generation

Reproducible harness: [`scalar/README.md`](../eng/physical-ownership-probes/scalar/README.md).
Recorded rows, fields, DLL hashes and individual native compilations:
[`scalar-results.json`](../eng/physical-ownership-probes/results-520b544b/scalar-results.json).

Five FullOpts runs agree for every 0/1/100-item row. Bytes per complete call:

| Channel | No deadline, baseline → candidate | Five-minute deadline, baseline → candidate |
|---|---:|---:|
| Static intercepted | 752.0016 → 752.0016 | 776.0016 → 776.0016 |
| Dynamic intercepted | 1272.0016 → 1264.0016 | 1296.0016 → 1288.0016 |

There is no measured per-item allocation slope in this synchronous short-circuit
probe. The intercepted wrapper remains 112 bytes, its iterator remains 200 bytes
with 17 fields, and the dynamic iterator changes from 504 to 496 bytes with 31
fields. The rejected full-state-owner representation is not used.

Main iterator IL grows: intercepted 1514 → 1920 bytes, EH regions 4 → 7;
dynamic 962 → 1008 bytes, EH regions 3 → 5. Representative same-mode native bytes:

| Compilation mode | Intercepted main, baseline → candidate | Dynamic main, baseline → candidate |
|---|---:|---:|
| FullOpts | 3973 → 4527 | 2096 → 2131 |
| Default instrumented Tier0 | 4319 → 5785 | 2758 → 3166 |
| Default PGO Tier1 | 4285 → 4834 | 2458 → 2422 |
| PGO disabled, Tier0 | 3512 → 4755 | 2381 → 2592 |
| PGO disabled, Tier1 | 3988 → 4542 | 2096 → 2131 |

Repeated tiered runs vary by a few bytes with observed profiles/layout. A redundant
deadline-wait wrapper is removed, including its 107-byte entry and 1615-byte
FullOpts state-machine body. The observed intercepted successful-scenario compiled
subset totals 6510 → 5342 bytes; that excludes unexecuted exceptional helpers and
is **not** the total implementation footprint. Neither native size nor allocation
establishes throughput or latency.

## Actual TCP: bounded, not performance equivalence

Reproducible harness: [`tcp/README.md`](../eng/physical-ownership-probes/tcp/README.md).
All 48 sample metrics are retained in
[`tcp-samples.csv`](../eng/physical-ownership-probes/results-520b544b/tcp-samples.csv);
both paired estimators and medians/ranges are in
[`tcp-summary.json`](../eng/physical-ownership-probes/results-520b544b/tcp-summary.json).

The matrix validates **4,581,444 full streams / 131,893,662 items**, with zero
failures. Both changed client wrappers and the real generated server pump are
verified. Probe and generated module binaries are identical between arms. Each
sample uses loopback TCP, default tiering/PGO, workstation GC, affinity CPUs 2/3,
one closed-loop worker, exactly one ready connection with pool min=max=1, one
pass-through client interceptor, no timeout, and 1 or 100 int items. Six paired
rounds alternate arm order; each fresh process warms 3 seconds and measures 6.

| Path/items | Baseline median streams/s | Candidate median streams/s | Paired mean change, descriptive 95% interval |
|---|---:|---:|---:|
| Static / 1 | 24,650 | 22,965 | +0.25% [-5.37%, +5.59%] |
| Static / 100 | 9,213 | 9,228 | +3.95% [-2.40%, +10.31%] |
| Dynamic / 1 | 22,106 | 21,520 | -3.80% [-11.97%, +5.64%] |
| Dynamic / 100 | 8,341 | 8,707 | +5.82% [+1.52%, +9.82%] |

Paired means and ratios of medians are different estimators; do not substitute
one for the other. Tiny dynamic median p99 is 148.89 → 155.33 microseconds.
Tiny-stream managed allocation is effectively unchanged for static calls and
about 8 bytes lower for dynamic calls. Moderate-stream allocation varies with
dispatch scheduling and is not a fixed per-item allocation result.

The host was not isolated: affinity-CPU steal was 2.35–4.81%, median 3.50%.
The intervals are descriptive bootstrap intervals, not an equivalence test or a
correction for host dependence. **The tiny dynamic result remains inconclusive
and does not exclude a meaningful regression. Performance acceptance is open.**
A separately recorded, CPU-saturated diagnostic could help distinguish scheduling
noise from persistent cost; it must not replace or discard these unfavorable
samples. This matrix is not certification for separate hosts, larger payloads,
more connections, other transports, asynchronous producers or server GC.
