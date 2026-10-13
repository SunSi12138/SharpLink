# Issue #775 contract routing performance evidence

Status: the historical #802 regression has been independently reproduced from retained raw evidence. The new implementation's matched comparison is pending completion and review. Successful compilation or successful benchmark execution alone is not a performance acceptance decision.

This document separates sender queue completion, receiver business execution, request latency, and allocation. It records the investigation requested by [issue #775](https://github.com/SunSi12138/SharpLink/issues/775) and the reproducible evidence for the new implementation in [PR #861](https://github.com/SunSi12138/SharpLink/pull/861).

## Historical #802 signal

The earlier implementation compared production base `eb99fe887cf2129d9b88441245ca0a4a6406b6c2` with head `3aedcfc71e85a43fa71a266b7fd0e6d18939d4ff`. These are the historical production identities, not the new PR's baseline or the workflow-definition commits.

The original investigation is described in [comment 6105089467](https://github.com/SunSi12138/SharpLink/pull/802#issuecomment-6105089467). Its raw timing and CPU profiles are retained by [run 38107805166](https://github.com/SunSi12138/SharpLink/actions/runs/38107805166); the receiver-completed allocation control is in [run 38108548843](https://github.com/SunSi12138/SharpLink/actions/runs/38108548843).

| Artifact | ID | ZIP SHA-256 verified during this investigation |
|---|---:|---|
| `issue775-oneway-abba-3aedcfc-38107805166-1` | 11690314710 | `d620c2a27dae2475c28ec379444372063f727f26841f06ed7f9c8e2ebe6b03f9` |
| `issue775-oneway-shm-3aedcfc-38108548843-1` | 11690611654 | `49dba9d07a77a4afee8303d7d194357b6f1b68923749a73ef86c4044827f8751` |

### Sender timing independently reproduced

The timing job used one Intel Xeon Platinum 8573C host, Ubuntu 24.04.5, .NET 10.0.12, SDK 10.0.112, CPU affinity `0,1`, and `DOTNET_PROCESSOR_COUNT=2`. The order was `base, head, head, base, head, base, base, head`: an ABBA block followed by BAAB. Each slot and inherited parameter label had three launches, six warmup iterations per launch, and fifteen actual iterations of 4,096 calls. The TCP connection pool had exactly one connection.

The independent parser checks all 720 raw `Workload/Actual` iteration records, including launch indices, iteration indices and operation counts: **2,949,120 sender-queued calls**. It reproduces the historical aggregation rather than substituting BenchmarkDotNet's filtered `Statistics` values: take the median of fifteen actual `Nanoseconds / Operations` observations for each launch, then the median of three launches for each slot, then the median of four slots for each arm.

| Inherited label | Base ns/queued call | Old head ns/queued call | Head/base | First ABBA block | Second BAAB block |
|---|---:|---:|---:|---:|---:|
| `PayloadSize=16` | 1,670.534 | 3,139.852 | **1.87955** | 1.75322 | 1.87584 |
| `PayloadSize=256` | 1,668.307 | 3,179.113 | **1.90559** | 2.00825 | 1.87786 |

Both labels execute `PublishEventAsync(7, Environment.TickCount64, "bench")`, as shown in the [historical benchmark source](https://github.com/SunSi12138/SharpLink/blob/eb99fe887cf2129d9b88441245ca0a4a6406b6c2/test/SharpLink.Benchmarks/UnaryBenchmarks.cs). `PayloadSize` does not change this expression or its payload shape; the timestamp naturally varies between calls. These results establish an approximately 88–91% increase in the historical sender completion cost under this workload. They do not measure confirmed delivery, isolated receiver throughput, a payload-size effect, or per-request P99/P99.9. Profiled launches are excluded from these ratios.

### What the CPU profiles show

`analyze_history.py` also parses all four retained speedscope exports. It integrates the non-overlapping intervals of each evented stack, retaining only stacks that contain both `Activity WorkloadActual(...)` and the synchronous `UnaryBenchmarks.Rpc_OnewayPublish()` frame. Nested matching methods are counted once per interval. The denominator is this tagged sender sample weight, not total process CPU or wall time.

| Profile label | Base send-queue `Monitor.Enter_Slowpath` share | Old head send-queue `Monitor.Enter_Slowpath` share | Old head admission subtree share | Old head admission or call-control subtree share |
|---|---:|---:|---:|---:|
| `PayloadSize=16` | 37.52% | 45.80% | 5.72% | 6.16% |
| `PayloadSize=256` | 27.32% | 40.03% | 8.21% | 10.40% |

The queue category requires both `RpcSession+SendPump.TryEnqueue(...)` and `System.Threading.Monitor.Enter_Slowpath(...)` on the same stack. The admission category is the union of `AdmitContractCallAsync`, `ReserveLiveContractConnection`, `GetReadyConnection`, `SelectContractConnection`, `IsContractCompatible`, `TryReserveCompatible`, and `ThrowIfContractWaitTerminated`. The final column additionally includes `ResolveCallControlForInvocation`; this broader classification is reported explicitly so that the original approximate 6–8% admission observation is not mistaken for all client control work.

The sampled sender spends a larger share in the existing send-queue lock slow path in the old head. The contract admission subtree is visible, but these profiles do **not** establish that per-call Manifest scanning causes the full timing regression. Nor do they prove that the lock alone causes it. CPU sampling projections do not provide lock wait durations, complete async worker attribution, or causal isolation. Changes in sender/receiver pacing, enqueue contention, wire work and scheduling remain plausible contributors. The retained `.nettrace` files permit a more detailed follow-up if a comparable signal persists in the new implementation.

The new design's frozen route lookup can remove a particular per-call search. That source-level property is useful independently, but does not by itself demonstrate that the historical approximately 1.9× signal has disappeared.

### Receiver-completed allocation control

The separate SharedMemory run used the unchanged canonical `rpc-oneway-sharedmemory-c1` allocation case: 512 warmup operations, five samples of 4,000 measured operations per slot, one worker, and ABBA order. Each sample waits for the service's publication counter before reading process-wide allocated bytes. All **80,000 measured business operations completed**.

| Arm | Slot medians, B/completed operation | Mean of slot medians, B/completed operation |
|---|---|---:|
| Base | 800.392; 800.444 | 800.418 |
| Old head | 800.430; 800.194 | 800.312 |

The independently reproduced delta is **−0.106 B/completed operation**. All original five-sample limits passed: 960 B/op median ceiling and 20 B/op spread ceiling. This evidence shows no visible per-operation allocation increase for that controlled path. It does not attribute allocations separately to sender and server, nor prove that every allocation path is unchanged.

The sender BenchmarkDotNet memory numbers are not used as allocation acceptance evidence: asynchronous receiver progress can move process allocation work across its sender-only timing boundaries. Likewise, the allocation control's elapsed times are not used as throughput measurements; its GC setup and completion barriers define a different workload.

### Reproduce the historical analysis

Download the two artifact ZIPs from the linked runs and extract them into separate directories. Run the in-tree parser with the downloaded files and their extraction roots:

```bash
python3 eng/rpc-contract-routing-performance/analyze_history.py \
  --timing-artifact /path/to/timing-artifact \
  --allocation-artifact /path/to/allocation-artifact \
  --timing-zip /path/to/timing.zip \
  --allocation-zip /path/to/allocation.zip \
  --output artifacts/issue775-history-recomputed.json
```

It verifies ZIP hashes, production identities, the empty historical harness diff, all raw actual-iteration counts, all allocation completion counts and normalizations, and the published aggregates. It emits per-slot and per-launch timing medians, allocation samples, and the profile categories used above. It rejects incomplete or inconsistent evidence.

## New implementation: matched comparison protocol

The new comparison freezes base `3332559cf4db455e0249448cf16ad43f4fc36564` and checks out the exact PR head recorded by `github.sha`. Both arms run sequentially on the same `ubuntu-24.04` job, with the same SDK, Release configuration, two available CPUs, `DOTNET_PROCESSOR_COUNT=2`, tiered compilation and tiered PGO. Host, runtime, command lines, selected environment, source SHA, assembly hash, input hashes and process exits are retained.

The script checks that dependencies, build properties, allocation budgets, generated benchmark contract, service, transport fixture, historical Unary benchmark, allocation runner and idle-wake workload are byte-identical between arms. It overlays only the additive `ContractRoutingEvidenceRunner.cs` measurement file and its exact CLI route onto the baseline. Production code, dependency versions, queue capacity and allocation policies are untouched by the measurement overlay. Protocol-specific raw parser fixtures in the new head are not used as timed workloads.

`eng/rpc-contract-routing-performance/run.py` executes eight ABBA/BAAB slots. Each slot contains the historical OneWay BenchmarkDotNet workload with the same three launches, six warmups, fifteen actual iterations and 4,096 invocations, followed by generated RPC scenarios on TCP and SharedMemory and all four unchanged allocation cases. Execution failures and failed budgets are retained and fail the job; they are never converted into successful timing rows.

### Completion boundaries and measured operations

The generated RPC runner uses one connection, one sequential worker, the Balanced performance profile, and the real generated `IBenchmarkRpc` proxy. It validates returned values and all 32 ordered items for each streaming operation.

| Scenario, on both transports | Warmup calls per slot | Measured calls per slot | Latency and throughput boundary |
|---|---:|---:|---|
| Unary | 1,024 | 8,192 | Validated `AddAsync(20, 22)` response |
| OneWay sender | 4,096 | 65,536 | Completion of the local `PublishEventAsync` call |
| OneWay receiver | 256 | 2,048 | Publication counter observed after each call |
| Client streaming | 128 | 1,024 | Response after consuming and summing 32 inputs |
| Server streaming | 128 | 1,024 | All 32 ordered outputs and terminal completion |
| Duplex streaming | 128 | 1,024 | All 32 echoed outputs and terminal completion |

For OneWay sender measurements, the queue-completion stopwatch stops before waiting for the final publication count. A separate observation records how many business operations have completed at that point, the final exact receiver count, additional drain time, and throughput through that final count. The receiver scenario waits after every call, so its throughput describes a single-in-flight completion-barrier workload. It is not a claim of maximum server throughput.

`BenchmarkRpcService.PublishEventAsync` increments its counter as its final business effect and returns a completed `ValueTask`. The counter proves that the benchmark business operation executed. It does not wait for all transport/dispatch cleanup, provide a wire ACK, or change OneWay's public delivery guarantee. Unary responses and stream terminal consumption provide the other scenarios' completion evidence.

Per-operation stopwatch ticks are retained. P50/P99/P99.9 are recomputed from those individual observations, separately from BenchmarkDotNet's batch iteration means. The 1,024-operation stream samples provide sparse extreme-tail observations, so these tails are descriptive and are not production SLO estimates. The report retains all four samples per arm for dispersion review instead of presenting one aggregate as a distribution guarantee.

Focused process-wide allocation and CPU measurements cover both endpoints, through the final OneWay business counter when applicable. They are diagnostic: the canonical allocation gate remains the allocation acceptance source. Both endpoints share the benchmark process, so these numbers do not isolate client-only or server-only costs.

### Unchanged allocation policy

The checked-in policy is `eng/perf/allocation-budgets.json`. Every slot runs its original cases and five-sample policy:

| Canonical case | Operations per sample | Median ceiling, B/op | Spread ceiling, B/op |
|---|---:|---:|---:|
| `rpc-add-sharedmemory-c1` | 4,000 | 1,450 | 50 |
| `rpc-add-sharedmemory-c8` | 4,096 | 650 | 90 |
| `rpc-oneway-sharedmemory-c1` | 4,000 | 960 | 20 |
| `send-pump-idle-wake-balanced` | 2,000 | 700 | 5 |

The final negative control injects 512 bytes per operation into `rpc-add-sharedmemory-c1`. It must fail specifically because its median exceeds the existing allocation ceiling. The summary validates completed-operation counts, sample counts, normalization, identical policy across all slots, and the negative-control failure reason. No timing, tail or allocation budget is weakened to make the new admission path pass.

### Current evidence and review decision

The first comparison attempt was scheduled by [run 38329394037](https://github.com/SunSi12138/SharpLink/actions/runs/38329394037) for head `72c30a8b5399a43eb2ea6c6ce7aeb0f096ef26a4`. Its compile gate found two test-fixture constructor migrations, so the comparison job was skipped and produced no timing evidence. Results are **pending**. The final reviewed source SHA and its completed artifact must replace this provisional status before making a performance acceptance claim.

The workflow uploads `issue775-performance-<sha>-<attempt>` with raw BenchmarkDotNet JSON, per-operation ticks, separate sender/receiver counts, allocation samples, build logs and provenance. `summary.json` retains base/head samples and ratios for throughput, receiver-completed throughput, tails, CPU, diagnostic allocation and drain time. `summary.md` presents the main comparisons. To independently validate a downloaded artifact:

```bash
python3 eng/rpc-contract-routing-performance/summarize.py /path/to/performance-artifact
```

Review must account for every RPC shape and both transports. In particular, client-stream and duplex calls now wait for stream acceptance before advancing producers; short-stream measurements must retain the actual cost of that ordering. A persistent regression requires investigation against the unchanged control workload, including sender/receiver pacing and queue contention when indicated by the old profiles. Passing the execution and allocation checks establishes evidence integrity; the measured throughput and tail ratios still require an explicit, source-matched performance decision.
