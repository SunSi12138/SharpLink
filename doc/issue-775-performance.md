# Issue #775 contract routing performance evidence

Status: the historical #802 investigation and the candidate comparison are complete. Exact production head `b806d892ef9d204f823e7bdafad23ef835859afd` passes the original execution, allocation and canonical verification gates described below. The candidate removes demonstrated redundant ordinary-path copies and parsing. It still costs approximately 18% more per OneWay sender call and retains receiver-throughput, short-stream latency and diagnostic allocation increases. The implementation is ready for review with those measured costs explicit; baseline performance parity and a merge-time performance acceptance decision are not claimed.

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

The allocation command uses the existing `eng/run-allocation-gate.sh` entry point and the inherited canonical CI environment. The fixed two-CPU and tiering overrides apply to timing and CPU diagnostic workloads. They do not apply to this allocation gate. Provenance records both configurations. The first attempt accidentally applied the timing overrides to the allocation commands as well; that failed evidence is retained and described below.

### First complete new comparison: `dd0881ae`

[Run 38329690391](https://github.com/SunSi12138/SharpLink/actions/runs/38329690391), job `115076766935`, compared base `3332559cf4db455e0249448cf16ad43f4fc36564` with head `dd0881ae3ec34bce4e8793cc051360dedf0c5a05`. Artifact `11782461349`, `issue775-performance-dd0881ae3ec34bce4e8793cc051360dedf0c5a05-1`, has verified ZIP SHA-256 `e60fc416a7750087498d1e4fae2720907fce54ca36f4e5d9968b4d28808562d9`.

The host was an Intel Xeon Platinum 8573C with four available logical CPUs. Timing used CPUs `0,1` and two reported .NET processors, .NET 10.0.12 and SDK 10.0.112. All eight timing slots, all 96 focused RPC processes, **1,261,568 receiver-verified focused RPC operations**, and **2,949,120 BDN sender-queued calls** completed. The downloaded raw ticks, counts and metadata independently reproduce the report.

| BDN inherited label | Base ns/queued call | Head ns/queued call | Head/base |
|---|---:|---:|---:|
| `PayloadSize=16` | 1,749.676 | 2,276.784 | **1.30126** |
| `PayloadSize=256` | 1,744.124 | 2,249.340 | **1.28967** |

Every head slot's BDN median exceeds every base slot's median for both labels. The approximately 29–30% signal is therefore retained as a regression requiring investigation. Its ratio is numerically below the historical #802 ratio, but the old and new comparisons use different base/head revisions and separate runs; this is not a quantified attribution of the improvement to a particular design change.

| Focused scenario | Base op/s | Head op/s | Throughput head/base | P99 head/base | P99.9 head/base |
|---|---:|---:|---:|---:|---:|
| TCP Unary | 17,399.2 | 15,940.1 | 0.9161 | 1.0909 | 0.8529 |
| TCP OneWay sender | 338,181.1 | 279,445.8 | 0.8263 | 1.0781 | 1.1728 |
| TCP OneWay receiver | 5,382.4 | 5,544.9 | 1.0302 | 1.0012 | 0.9581 |
| TCP client streaming | 3,288.2 | 3,049.4 | 0.9274 | 0.6031 | 0.5635 |
| TCP server streaming | 5,843.5 | 5,640.8 | 0.9653 | 0.9404 | 1.0114 |
| TCP duplex | 2,578.0 | 2,027.1 | 0.7863 | 1.4479 | 1.0529 |
| SharedMemory Unary | 14,052.8 | 13,066.8 | 0.9298 | 1.0397 | 1.2953 |
| SharedMemory OneWay sender | 279,863.1 | 263,531.5 | 0.9416 | 1.0304 | 0.9857 |
| SharedMemory OneWay receiver | 3,596.0 | 2,873.8 | 0.7992 | 1.0052 | 1.0199 |
| SharedMemory client streaming | 3,697.5 | 2,872.9 | 0.7770 | 1.1380 | 0.5928 |
| SharedMemory server streaming | 5,780.7 | 5,643.4 | 0.9762 | 1.0145 | 1.1700 |
| SharedMemory duplex | 2,430.1 | 1,959.5 | 0.8063 | 1.5555 | 1.2270 |

The final receiver barriers also show slower completion of the queued OneWay batches: TCP goes from 223,645.9 to 172,418.4 completed operations/s (`0.77094×`), and SharedMemory from 207,266.9 to 152,928.6 (`0.73783×`). The TCP per-request receiver-barrier scenario's near-flat result does not erase these batch throughput regressions: it applies a different pacing boundary. Duplex P99 and the short-stream throughput changes remain visible; stream acceptance ordering is a candidate contributor, not yet a measured isolated cause.

The job correctly failed its allocation checks. All **563,840 allocation operations** completed, but the timing configuration's two-CPU override had also been applied to these checks. The original `rpc-add-sharedmemory-c1` spread ceiling failed in every base and head slot, with spreads from 51.402 to 166.708 B/op; some medians also exceeded 1,450 B/op. The c8 spread ceiling failed in three slots. OneWay and idle-wake passed their unchanged limits in all slots. Across arm medians, OneWay allocation was 800.036 to 800.078 B/completed operation.

This is preserved as failed evidence, not reclassified as a passing canonical run. On the same source SHA, the independent [PR Fast canonical allocation job 115076374538](https://github.com/SunSi12138/SharpLink/actions/runs/38329693623/job/115076374538) passed all four original cases: c1 1,350.986, c8 577.922, OneWay 800.408 and idle-wake 616.444 B/op, with all original spread limits satisfied. Its 512-byte negative control failed at 1,883.992 B/op against the unchanged 1,450 B/op ceiling. Different execution environments preclude attributing the discrepancy solely to CPU affinity; subsequent matched allocation runs restore the exact canonical entry point and inherited environment without changing any budget or timing workload.

An independent CPU diagnostic workflow, `rpc-contract-routing-cpu.yml`, freezes the observed `dd0881ae` head and the same `3332559` base. It captures four ABBA EventPipe profile slots and separate JIT assembly processes, with the same diagnostic invocation settings as the historical profiles. The source and driver SHA are recorded separately. Client prefix construction/copies, binding/channel dispatch, server admission and enqueue contention are investigated as candidate costs. All diagnostic timings are excluded from the unprofiled acceptance ratios. No allocation-inducing receipt representation change is justified by the timing comparison alone.

### Canonical allocation configuration and repeat signal: `2948fd62`

[Run 38330877073](https://github.com/SunSi12138/SharpLink/actions/runs/38330877073), job `115080400198`, compared the same frozen base with head `2948fd6259066a49c5b5a5f178d2d7de5c7ec303`. GitHub marks the job cancelled after a subsequent push; its retained artifact had already completed all eight slots, the negative control and its summary. Artifact `11782528653` has verified ZIP SHA-256 `63aead98a626fd580c647bba7136c1659524849e54bca899349147e98a542f7a`. Independent parsing verifies all **1,261,568 focused RPC**, **2,949,120 BDN** and **563,840 allocation** operations, all process exits and the explicit negative-control failure. This is complete retained measurement evidence; it is not a successful GitHub check on the final PR head.

The allocation commands now use the original entry point and inherited environment, reporting four processors, while the timing workloads remain fixed to two processors. All 32 allocation cases across eight slots passed their original median and spread ceilings. The injected control correctly failed at 1,871.370 B/op against 1,450 B/op.

| Canonical allocation case | Median of base slot medians, B/op | Median of head slot medians, B/op | Delta B/op |
|---|---:|---:|---:|
| Unary c1 | 1,324.850 | 1,330.447 | +5.597 |
| Unary c8 | 549.475 | 567.734 | +18.260 |
| OneWay c1 | 800.240 | 800.246 | +0.006 |
| Idle-wake | 616.156 | 616.538 | +0.382 |

The unchanged timing workload independently reproduced the sender regression: `PayloadSize=16` was 1,767.572 to 2,260.985 ns/queued call (**1.27915×**), and `PayloadSize=256` was 1,774.096 to 2,260.847 (**1.27437×**). Every head slot again exceeded every base slot for each label. Final receiver-completed batch throughput was `0.75534×` for TCP and `0.81375×` for SharedMemory. These repeated signals remain open for attribution; restoring canonical allocation execution does not resolve the timing regression. Several short-stream tail ratios vary substantially between runs, so the complete per-slot samples remain necessary for review.

### CPU and emitted assembly for the observed `dd0881ae` pair

[CPU attribution run 38331679091](https://github.com/SunSi12138/SharpLink/actions/runs/38331679091), job `115082575796`, completed successfully. Driver `2b57dc076d2a7ff94371089847cf7173f3b7a5e3` checked out the unchanged observed head `dd0881ae3ec34bce4e8793cc051360dedf0c5a05` and base `3332559cf4db455e0249448cf16ad43f4fc36564`. Artifact `11783081932` has verified ZIP SHA-256 `97f49f7a58f8cbe6746a494b0e22224f2dcc8ccd5bf99d718d4a73f72d882911`.

The four ABBA EventPipe processes each use one launch, six warmups, fifteen actual iterations and 4,096 invocations for both inherited OneWay labels. Eight original `.nettrace` files and their speedscope exports are retained. Separate unprofiled diagnostic processes retain JIT assembly for each arm. Their timings are excluded from the unprofiled three-launch acceptance comparison above. `profile_summary.py` independently reproduces the category weights from the raw exports.

| Category and explicit sample scope | Base share across slots and labels | Head share across slots and labels |
|---|---:|---:|
| Send-queue `Monitor.Enter_Slowpath`, actual-tagged synchronous sender | 52.54–58.66% | 45.69–62.31% |
| Route/call-control subtree, actual-tagged synchronous sender | 1.14–10.98% | 0.43–14.25% |
| Identity write/validation subtree, actual-tagged synchronous sender | 0% | 0–0.99% |
| Identity read subtree, whole-capture server-frame stacks | 0% | 3.71–7.38% |
| Contract admission subtree, whole-capture server-frame stacks | 0% | 0.42–2.79% |

The sender queue remains a large sampled subtree in both arms, but its relative shares overlap. Route/call-control shares vary substantially between slots. These observations do not isolate either category as the cause of the approximately 27–30% timing signal. In particular, the generated proxy already holds a local binding; this workload does not perform `ResolveLocalBinding` or scan the remote Manifest on each call. A low or zero directly named helper share also does not imply zero cost: inlining, caller attribution and unresolved frames affect sampling.

The server scope includes warmup and dispatch-drain samples that carry server frames. It is not aligned to the sender's actual-iteration activity tags and does not include every server-related worker stack. Its percentages therefore identify work to inspect, not server CPU per measured request, and cannot be added to sender percentages. Inclusive categories can overlap. Sample weights are CPU projections, not lock-wait durations or causal estimates.

The retained `disassembly/base/process.log` and `disassembly/head/process.log` provide more direct evidence of representation cost. All matching listings in these diagnostic processes are **Tier0**, with `ClientContractRoute.SelectConnection` marked instrumented Tier0; they are not Tier1 assembly. For the concrete generated publication request, the following emitted stack reservations are observable:

| Emitted method | Base stack reservation | Head stack reservation | Observed head work |
|---|---:|---:|---|
| `SharpLinkClient.SendRpcCall` | 528 bytes | 816 bytes | Constructs a full identity, including a nullable receipt, before writing an ordinary request |
| `ProtocolV2RequestIdentityCodec.Write` | Not present | 672 bytes | Calls identity validation and contains ordinary and receipt serialization paths |
| `ProtocolV2RequestIdentityCodec.Read` | Not present | 608 bytes | Copies the 88-byte nullable receipt into the identity constructor and returns a 136-byte identity through `CORINFO_HELP_MEMCPY`, including ordinary mode |
| `ServerRequestEnvelopeReader.Read` | 560 bytes | 1,712 bytes | Calls the identity reader, copies the 136-byte identity between locals and into the envelope |

For the no-deadline, no-metadata envelope path, the reader contains two unconditional 136-byte `CORINFO_HELP_MEMCPY` calls after the codec returns and a 136-byte `rep movsb` into the envelope constructor arguments. Its additional copies behind optional deadline/metadata branches are not counted as ordinary-path work. The codec's own 136-byte return copy is separate. The retained source also parses request identity before admission and again when decoding the admitted envelope, and validates/looks up the admitted method again during dispatch. These are concrete candidates for passing already validated values forward or specializing ordinary serialization without allocating a receipt object. Stack reservations and instruction listings demonstrate emitted work; they do not measure its end-to-end cost or prove that removing it resolves the observed throughput loss. Every candidate still requires the same unprofiled matched comparison and unchanged allocation policy.

### Subsequent canonical stability failure: `2b57dc07`

The [PR Fast allocation job 115082590251](https://github.com/SunSi12138/SharpLink/actions/runs/38331683873/job/115082590251) for PR head `2b57dc076d2a7ff94371089847cf7173f3b7a5e3` failed the original c1 stability limit in the canonical environment. Its c1 median was **1,339.478 B/op**, below 1,450 B/op, but its range was **1,326.400–1,378.998 B/op**, producing **52.598 B/op spread against the unchanged 50 B/op ceiling**. The c8 case passed at 566.512 B/op with 22.070 B/op spread; OneWay passed at 800.288 with 0.298 spread; idle-wake passed at 616.448 with 0.792 spread. The separate fast test job succeeded. The allocation action skipped its negative-control step after the gate failure.

This remains a failed canonical allocation check. It is separate from the preceding complete `2948fd62` comparison's 32 passing cases and completed negative control. Neither that earlier success nor the small size of this spread overrun converts this result to a pass; the original policy is retained.

### Complete third comparison: `2b57dc07` on AMD

The separate [matched-performance job 115082947047](https://github.com/SunSi12138/SharpLink/actions/runs/38331678983/job/115082947047) for the same `2b57dc07` head completed successfully. Artifact `11782819682` has verified ZIP SHA-256 `b5ad6ee1e56280e384115ddeb09a0073b775bfb3486c939460a4edb0f486de60`. Independent parsing again verifies all eight slots, **1,261,568 focused RPC operations**, **2,949,120 BDN sender calls**, **563,840 allocation operations**, and all original allocation policies. All 32 allocation cases passed; the injected negative control failed correctly at **1,889.950 B/op against 1,450 B/op**. This successful matched allocation result is retained alongside the independent PR Fast stability failure above.

This job ran on an AMD EPYC 7763 host with four available logical CPUs, while the earlier two comparisons used Intel Xeon Platinum 8573C hosts. Both arms within this job retain the same fixed two-CPU timing configuration. Absolute timing values across these jobs cannot measure the effect of the intervening source changes. Within this job, `PayloadSize=16` costs **2,704.965 to 3,399.178 ns/queued call (1.25664×)**, and `PayloadSize=256` costs **2,723.612 to 3,364.194 ns/queued call (1.23520×)**. Every head slot again exceeds every base slot for each label. The repeat signal is present on this different host family.

Final receiver-completed OneWay batch throughput is **182,727.8 to 142,803.9 op/s on TCP (0.7815×)** and **166,424.6 to 133,804.2 op/s on SharedMemory (0.8040×)**. Short client-stream and duplex workloads also remain slower: throughput ratios are 0.7383 and 0.7661 on TCP, and 0.6619 and 0.6488 on SharedMemory. SharedMemory client-stream P99 is 421.960 to 1,109.441 microseconds, and duplex P99 is 1,016.341 to 1,826.042 microseconds. All original per-slot P50/P99/P99.9 samples remain in the artifact, including the highly variable extreme tails. Successful execution and allocation gates do not accept these timing regressions.

The subsequent `b806d892` source candidate specializes ordinary identity writing, isolates receipt-only work, publishes client routes atomically, and reuses already parsed identity on immediate admitted dispatch. These changes are motivated by the emitted work and a separately reproduced publication race. The diagnostic and unprofiled candidate results below evaluate them separately. The CPU workflow defaults to the exact workflow SHA, with an optional exact SHA override. Profiled timings remain diagnostic only.

### Candidate `b806d892`: verified removal of redundant ordinary-path work

The candidate production SHA is `b806d892ef9d204f823e7bdafad23ef835859afd`. [CPU attribution run 38332967392](https://github.com/SunSi12138/SharpLink/actions/runs/38332967392), job `115086643732`, compared this exact source against the same frozen `3332559` base. Artifact `11783637137` has verified ZIP SHA-256 `90889f8fa48ba085bb68c609f5f7b3ff79cb398947fefb38fedc3a4ea4a72a53`. It retains all eight EventPipe traces, their stack projections and independent JIT processes. Both this and the prior CPU capture report AMD EPYC 7763 hosts; this does not establish that separate CI jobs occupied the same physical machine.

The emitted methods are again Tier0. The concrete publication `SendRpcCall` now reserves **512 bytes**, compared with **816 bytes** in the previous head and **528 bytes** in the frozen base. Its emitted code is **2,007 bytes**, compared with 2,242 and 1,922 bytes respectively. The ordinary branch tests `HasReceipt`, calls the scalar `WriteOrdinary`, and no longer calls the nullable receipt getter or constructs a full `ProtocolV2RequestIdentity`. The previous 88-byte nullable constructor-argument copy has disappeared. The receipt-specific construction remains in a separate non-inlined helper for explicit receipt calls. `WriteOrdinary` reserves 96 bytes and emits 177 bytes of code; its shape validator and scalar prefix writer remain separate visible methods.

The route check also tests `HasReceipt` before entering the receipt-specific branch. This avoids the ordinary route's nullable receipt getter/value copy. It does not imply that every route stack frame has lost all space used by receipt-only locals: the shared route helper still includes both branches.

The immediate admitted server path now emits `ReadAfterIdentity` followed by `ReadRemainder`. Neither method calls the identity codec; the original outer parse remains. Their stack reservations are 320 and 816 bytes respectively, versus the previous combined reader's 1,712-byte frame. Queue-resume and decompression paths still use their full parse wrappers against their retained owners. This is direct evidence that the additional immediate-path identity parse was removed, while source and differential tests preserve those ownership boundaries. It is not a measurement of the total performance benefit.

The first published candidate CPU summary's identity-write category matched only the old `Write` and `Validate` method names, so its zero for that column omitted the new scalar helpers. The corrected parser explicitly includes `WriteOrdinary`, `WritePrefix`, and `ValidateShape`, and reprocesses the same raw exports without resampling. The corrected candidate sender identity-helper shares are **1.09–2.76%**. All prior `dd0881ae` profile row values are unchanged by this mapping correction. Candidate sender queue-slowpath shares are 41.05–61.85%, route/call-control shares 4.01–11.04%, and whole-capture server identity-read shares 3.11–4.89%. These category shares retain the same overlapping-subtree, capture-phase, sampling and Tier0 limitations; none is substituted for the unprofiled timing result.

The existing [RPC Session Flush Performance Evidence run 38332972058](https://github.com/SunSi12138/SharpLink/actions/runs/38332972058), job `115086657557`, now completes all static-base, runtime-no-update and post-update scenarios without the preceding head's warmup `No eligible connection` failure. The benchmark's connection setup, timed payloads, flush policies, operation counts and waits were unchanged. This supplies additional real-TCP coverage for the atomic route-publication fix. Its separate baseline is current dev `1a54993d3550911f5979a7b4d3f9f5f63ee86a64`, so its timing rows are not mixed into the frozen-`3332559` comparison.

The existing [Producer Progress RPC Evidence run 38332971198](https://github.com/SunSi12138/SharpLink/actions/runs/38332971198), job `115086654879`, also completed its separate current-dev-to-`b806d892` stream workload. Artifact `11783363935` retains 168 result documents: six AB/BA pairs, seven generated-stream scenarios, two transports, 50 warmup operations and two-second measurement windows. Completion and correctness checks pass, but this workflow has no numerical throughput acceptance threshold. Thirteen of its fourteen transport/scenario aggregate throughput comparisons are negative. For `Client100x16`, TCP throughput is down 12.69% and SharedMemory throughput is down 23.62%, with CPU/op up 19.95% and 29.22% respectively. The SharedMemory gated scenario is up 6.08%. Process-wide allocation remains a separate diagnostic quantity. This corroborating stream evidence does not cover OneWay or Unary, does not replace the fixed-baseline matrix, and is not presented as a claim of regression-free performance.

### Final candidate matched measurement: `b806d892`

[Run 38332967385](https://github.com/SunSi12138/SharpLink/actions/runs/38332967385), matched-performance job `115087019017`, completed successfully for exact head `b806d892ef9d204f823e7bdafad23ef835859afd` against frozen base `3332559cf4db455e0249448cf16ad43f4fc36564`. Artifact `11784251516` has verified ZIP SHA-256 `03500cb9fb53ddc02c4956d0138b13c8f4bc611d580f8ff92d44d7a2f5c76e62`. The host is AMD EPYC 7763, with the same two-CPU timing affinity and runtime settings for both arms. Independent parsing validates all eight slots, **1,261,568 focused RPC operations**, **2,949,120 BDN sender calls**, and **563,840 canonical allocation operations**, including every expected process exit and completion boundary.

| BDN inherited label | Base ns/queued call | Candidate ns/queued call | Candidate/base |
|---|---:|---:|---:|
| `PayloadSize=16` | 2,718.412 | 3,214.402 | **1.18246** |
| `PayloadSize=256` | 2,718.427 | 3,205.841 | **1.17930** |

Every candidate slot median still exceeds every base slot median for both labels. The approximately **18% sender regression remains measured**. Its ratio is smaller than the earlier candidates' ratios, but the candidate contains several changes and runs in a separate job; this comparison does not isolate the timing benefit of one optimization. The assembly evidence establishes removed work, while the matched timing establishes the remaining end-to-end cost.

| Transport/scenario | Base op/s | Candidate op/s | Candidate/base | Base P99 us | Candidate P99 us | Base P99.9 us | Candidate P99.9 us |
|---|---:|---:|---:|---:|---:|---:|---:|
| TCP Unary | 8,393.1 | 7,867.1 | 0.9373 | 199.608 | 207.002 | 252.677 | 288.694 |
| TCP OneWay sender | 252,779.1 | 226,720.3 | 0.8969 | 11.757 | 16.181 | 31.029 | 32.385 |
| TCP OneWay receiver | 3,532.2 | 3,437.8 | 0.9733 | 1,111.057 | 1,111.337 | 1,176.519 | 1,171.879 |
| TCP client streaming | 2,602.1 | 1,821.7 | 0.7001 | 467.511 | 702.748 | 516.431 | 966.240 |
| TCP server streaming | 2,717.5 | 2,959.3 | 1.0890 | 616.391 | 442.067 | 1,218.492 | 514.024 |
| TCP duplex | 1,976.4 | 1,200.8 | 0.6076 | 731.778 | 1,627.792 | 1,059.625 | 3,491.256 |
| SharedMemory Unary | 6,798.3 | 6,666.3 | 0.9806 | 306.518 | 311.022 | 423.587 | 464.368 |
| SharedMemory OneWay sender | 219,166.2 | 227,433.3 | 1.0377 | 14.843 | 16.616 | 30.733 | 32.090 |
| SharedMemory OneWay receiver | 2,375.4 | 2,507.2 | 1.0555 | 1,115.495 | 1,115.850 | 1,192.088 | 1,171.874 |
| SharedMemory client streaming | 2,921.2 | 1,959.4 | 0.6708 | 407.797 | 927.004 | 457.722 | 1,446.248 |
| SharedMemory server streaming | 3,219.5 | 2,851.5 | 0.8857 | 576.582 | 640.390 | 1,578.860 | 1,407.434 |
| SharedMemory duplex | 1,714.3 | 1,260.7 | 0.7354 | 1,424.878 | 1,824.561 | 2,735.888 | 3,122.801 |

These tails are per-request observations, not BDN iteration tails. Four samples per arm remain available for each metric; 1,024-operation stream samples provide limited extreme-tail resolution. Client-stream and duplex throughput and tails remain material review items. The server-stream results vary by transport, so a single favorable row cannot represent all stream shapes.

| OneWay sender workload through final business completion | Base op/s | Candidate op/s | Candidate/base | Base final drain ms | Candidate final drain ms |
|---|---:|---:|---:|---:|---:|
| TCP | 183,494.6 | 158,749.1 | 0.8651 | 95.787 | 123.626 |
| SharedMemory | 168,902.5 | 148,876.2 | 0.8814 | 93.426 | 149.213 |

The favorable SharedMemory sender-only row does not erase slower final business completion. TCP and SharedMemory batch receiver throughput remain approximately 13.5% and 11.9% lower respectively. The per-request receiver-barrier scenario has different pacing and likewise cannot replace this batch observation.

All **32 unchanged canonical allocation cases pass** their five-sample median and spread limits. The 512-byte injected negative control fails as required at **1,876.308 B/op against 1,450 B/op**. The median of each arm's four slot medians is:

| Canonical allocation case | Base B/completed op | Candidate B/completed op | Delta B/op |
|---|---:|---:|---:|
| `rpc-add-sharedmemory-c1` | 1,336.837 | 1,350.594 | +13.757 |
| `rpc-add-sharedmemory-c8` | 549.355 | 556.275 | +6.920 |
| `rpc-oneway-sharedmemory-c1` | 800.444 | 800.444 | +0.000 |
| `send-pump-idle-wake-balanced` | 616.640 | 616.572 | -0.068 |

The focused workloads additionally retain process-wide CPU and allocation through their documented business-completion boundary. These measurements include both endpoints and their in-process harness. They are diagnostic, not replacements for the canonical policy:

| Transport/scenario | Base CPU us/completed op | Candidate CPU us/completed op | Base diagnostic B/completed op | Candidate diagnostic B/completed op |
|---|---:|---:|---:|---:|
| TCP Unary | 237.657 | 253.671 | 489.111 | 489.122 |
| TCP OneWay sender | 9.415 | 10.657 | 518.598 | 890.526 |
| TCP OneWay receiver | 230.191 | 238.989 | 168.633 | 168.316 |
| TCP client streaming | 768.708 | 1,091.345 | 7,523.496 | 9,011.887 |
| TCP server streaming | 726.434 | 664.408 | 1,552.145 | 1,745.301 |
| TCP duplex | 1,010.178 | 1,656.307 | 9,566.480 | 10,999.977 |
| SharedMemory Unary | 293.613 | 298.985 | 1,518.569 | 1,484.229 |
| SharedMemory OneWay sender | 10.352 | 11.542 | 700.145 | 985.252 |
| SharedMemory OneWay receiver | 327.183 | 315.323 | 776.016 | 776.740 |
| SharedMemory client streaming | 684.496 | 1,017.926 | 9,206.023 | 11,262.027 |
| SharedMemory server streaming | 623.727 | 697.102 | 3,088.875 | 3,326.586 |
| SharedMemory duplex | 1,160.514 | 1,582.671 | 11,906.141 | 14,434.246 |

The long 65,536-call OneWay sender workload and the 32-item stream workloads show diagnostic allocation increases even though the canonical 4,000-operation OneWay case is unchanged. These are different workloads and completion windows. The canonical pass must not be used to erase the burst/stream observations; the diagnostic differences must not be attributed to a new allocation in a particular client or server method without allocation-stack evidence.

### Current evidence and review decision

**Disposition: ready for review with explicit remaining performance costs.** Historical raw evidence has been independently reproduced; the suspected Manifest-only explanation was not adopted; current sender, receiver, all five RPC shapes, tails and allocation are measured separately; and the candidate's targeted removal of redundant work is confirmed in emitted Tier0 assembly. No original queue, allocation or regression limit was weakened. The complete candidate evidence run also passes its compilation, regression, public API, NativeAOT, Trim, package, generated ABI and protocol compatibility jobs. All of those results identify exact production head `b806d892ef9d204f823e7bdafad23ef835859afd`.

Performance equivalence to baseline is **not** established. The merge review must explicitly consider the approximately 18% OneWay sender cost, lower receiver-completed batch throughput, short client-stream/duplex throughput and tail changes, and the diagnostic burst/stream allocation increases above. Earlier failures, different execution environments and slower rows remain in this document and their artifacts. Passing execution and allocation gates establishes those defined checks; it does not silently accept every throughput or tail result.

Client-stream and duplex producers now wait for service acceptance before advancing. That ordering implements the issue's required failure semantics and adds a synchronization boundary compared with the baseline. The whole-implementation comparisons do not isolate how much of each stream ratio comes from that boundary versus other admission, serialization or scheduling work. Removing the required acceptance ordering is not proposed as a performance fix. Further optimizations must preserve the service's pre-business checks and the stream/session ownership boundaries.

The earlier [run 38329394037](https://github.com/SunSi12138/SharpLink/actions/runs/38329394037) for head `72c30a8b5399a43eb2ea6c6ce7aeb0f096ef26a4` stopped at two test-fixture constructor migrations and produced no timing evidence. The later complete runs above supersede that execution failure without concealing it.

The workflow uploads `issue775-performance-<sha>-<attempt>` with raw BenchmarkDotNet JSON, per-operation ticks, separate sender/receiver counts, allocation samples, build logs and provenance. `summary.json` retains all base/head samples and ratios; `summary.md` presents the main comparisons. To independently validate a downloaded artifact:

```bash
python3 eng/rpc-contract-routing-performance/summarize.py /path/to/performance-artifact
```

To reproduce CPU category weights, including the corrected scalar identity-helper mapping, use the retained raw CPU artifact:

```bash
python3 eng/rpc-contract-routing-performance/profile_summary.py /path/to/cpu-artifact
```


## Follow-up: reusable admission signals and deferred stream contexts (2026-10-13)

This section supersedes the **current candidate** designation above, not the historical measurements. Production source is `7d3447d233993fd1c9dc6e0f426f3b4a2c3e963b`; subsequent commits fix test isolation/reporting only. The previous production candidate is frozen at `2149ca799dca38bcb7f97583572406b0d0b41890`. The original pre-feature performance baseline remains `3332559cf4db455e0249448cf16ad43f4fc36564`. Do not compare timings taken on different runner hosts as a new paired experiment.

### Changes and retained invariants

- Ordinary static Unary/OneWay dispatch uses the already validated request shape to bypass stream establishment. When server call telemetry has no observers, dispatch enters the existing invocation core before resolving otherwise unused telemetry descriptors/details. No Manifest rescan, selection change or admission bypass is introduced. Authentication/interceptors still run through the same core.
- ClientStreaming/Duplex admission replaces the per-call `TaskCompletionSource<bool>`/Task pair with a bounded pooled `IValueTaskSource<bool>`. Completion and result consumption own separate references; reset/reuse waits for both. The signal is independent of the reusable pending-call slot, so a delayed consumer cannot observe a replacement call's admission. Three tests cover delayed consumption, slot reuse and 8 × 1,024 concurrent completion/consumption races.
- **Producer admission ordering is unchanged.** `RunContinuationsAsynchronously` remains enabled: this change removes the TCS/Task allocation pair but does not claim to remove the required scheduling/acceptance boundary or start user producer code inline on the receive loop. Completion before waiting retains the synchronous accepted fast path.
- Unintercepted streams retain a smaller call-context snapshot containing the admitted descriptor, request ID, authentication, deadline, metadata and cancellation token. A complete invocation context is constructed only for interception or exception mapping. Error mapping uses the captured descriptor, including after retirement, rather than consulting the current registry.
- Atomic route snapshots, the Picker-selected Session, server predecode identity checks, registration leases, failure handling and the original allocation policy are preserved. This round does not change their implementation files or their budgets.

The first `ce4f8ef` experiment revealed that bypassing stream setup merely moved descriptor resolution into disabled telemetry. The `7d3447d` guard removes that remaining resolution. The first experiment is retained in [run 38408537599](https://github.com/SunSi12138/SharpLink/actions/runs/38408537599), rather than being used as evidence for the final implementation.

### Controlled removal experiments: mixed results, not a blanket performance win

[Run 38410063138](https://github.com/SunSi12138/SharpLink/actions/runs/38410063138) compares the unmodified production candidate against three separate one-factor removal variants. Each variant restores only its own change group from `2149ca7`; shared-file changes use exact non-overlapping replacements. The telemetry/static group is a combined factor, so this experiment does **not** distinguish the effects of its two internal changes. Diagnostic overlays are not shipping commits.

All arms use the same runner, two pinned CPUs, .NET 10.0.112, tiered PGO, unchanged workload and `old,new,new,old,new,old,old,new` ordering. Each group executes both TCP and SharedMemory and all six focused scenarios. All **288 case processes** completed; independently checking completion counts and every raw P50/P99/P99.9 vector accounts for **3,784,704 RPCs**. Raw patches, all slot values, process exits and assembly hashes remain in artifact `11821112072` (ZIP SHA-256 `18d8ab278106e11771a4399b2f9eb1a81fa8d9d0e04a5b12a4000d56acc3737c`).

Ratios below are **candidate / corresponding factor-restored arm**. Higher throughput is faster; CPU/allocation ratios below 1 are smaller. ABBA and BAAB are separately calculated from their two observations per arm; the combined ratio uses all four observations per arm.

| Removed factor / scenario | Throughput ratio | ABBA | BAAB | CPU/op ratio | Diagnostic allocation/op ratio |
|---|---:|---:|---:|---:|---:|
| tcs / tcp/clientstream | 1.0028 | 0.9352 | 1.0673 | 1.0019 | 0.9874 |
| tcs / tcp/duplex | 1.0537 | 1.0545 | 1.0530 | 0.9502 | 0.9895 |
| tcs / sharedmemory/clientstream | 1.0375 | 0.9736 | 1.1065 | 0.9793 | 0.9992 |
| tcs / sharedmemory/duplex | 1.0661 | 1.0486 | 1.0853 | 0.9385 | 0.9997 |
| full-context / tcp/clientstream | 0.8932 | 0.8738 | 0.9121 | 1.1210 | 0.9382 |
| full-context / tcp/serverstream | 1.0404 | 1.0207 | 1.0591 | 0.9653 | 0.9182 |
| full-context / tcp/duplex | 1.0316 | 0.9773 | 1.0895 | 0.9643 | 1.0205 |
| full-context / sharedmemory/clientstream | 1.0903 | 1.1506 | 1.0352 | 0.9139 | 0.9940 |
| full-context / sharedmemory/serverstream | 1.0738 | 1.1003 | 1.0496 | 0.9317 | 0.9913 |
| full-context / sharedmemory/duplex | 1.0642 | 1.0400 | 1.0898 | 0.9440 | 0.9623 |
| telemetry-and-static / tcp/unary | 0.9973 | 1.0287 | 0.9671 | 1.0027 | 1.0000 |
| telemetry-and-static / tcp/oneway-sender | 0.9359 | 0.9250 | 0.9455 | 1.0205 | 0.8324 |
| telemetry-and-static / sharedmemory/unary | 1.0145 | 0.9925 | 1.0371 | 0.9870 | 0.9955 |
| telemetry-and-static / sharedmemory/oneway-sender | 0.9639 | 1.0792 | 0.8590 | 0.9412 | 0.8242 |

The reusable signal has repeatable positive Duplex ratios in this run, but ClientStreaming block directions disagree. Lightweight contexts reduce TCP ClientStreaming diagnostic allocation while **throughput falls 10.68% and CPU/op rises 12.10%**; both ordering blocks are slower. The ordinary-path factor also has a **6.41% TCP sender-only decline** despite reduced diagnostic allocation. Those adverse rows remain review risks, not discarded outliers. Conversely, unchanged controls move substantially: SharedMemory OneWay sender is 0.8735 in the TCS factor even though it does not execute the changed admission waiter. This dispersion limits causal claims from a single hosted-runner experiment. The data does not establish a uniform speedup or identify all remaining stream cost as TCS overhead.

### CPU and emitted-code attribution

[Run 38410062652](https://github.com/SunSi12138/SharpLink/actions/runs/38410062652) captures a separate same-host ABBA of `2149ca7` versus `7d3447d`: eight OneWay CPU profiles and sixteen stream CPU profiles, plus independent JIT runs. Original `.nettrace`, speedscope, process logs, assembly hashes and provenance are in artifact `11821302459` (ZIP SHA-256 `eb28dea8bb1ba863e4307477d0b76be00c8f84490f1360af6874f800d5192374`). Profiled/disassembly timings do not enter unprofiled throughput acceptance.

Emitted Tier0/Instrumented Tier0 code confirms that `PendingCall.WaitForRequestAdmissionAsync` changes from TCS construction/Task retrieval to `RequestAdmissionWaiter.Rent/WaitAsync`, and `CreateCallContext` has a lightweight snapshot branch while retaining the intercepted full-context branch. `InvokeServiceTrackedAsync` branches directly to the existing invocation core before `GetMethodDescriptor` when telemetry is disabled. Tier1 output is also retained; inlining means a missing named helper alone is not proof of removed work.

The existing actual-tagged synchronous OneWay sender classifier reports queue monitor slowpath weights of 30.71–51.91% in base slots and 44.36–61.61% in candidate slots. These are overlapping CPU-stack projections, not lock-wait durations or causal throughput percentages. Whole-capture server identity/admission stacks are reported separately, never divided by synchronous sender samples.

For asynchronous streams, `stream_profile_summary.py` intentionally uses the **whole capture**, including warmup/runtime threads; it does not assume the synchronous benchmark frame covers resumed work. Average scheduling-category percentages across the two slots are:

| Stream shape / items | Previous % | Candidate % |
|---|---:|---:|
| ClientStreaming / 32 | 10.822 | 8.835 |
| ClientStreaming / 256 | 11.153 | 10.080 |
| Duplex / 32 | 9.062 | 9.965 |
| Duplex / 256 | 12.676 | 13.115 |

Named admission helpers account for less than 0.03%, and context helpers less than 0.004% of these whole-capture weights. They are too sparsely sampled, and too susceptible to inlining, to quantify a per-call TCS/context CPU saving. Scheduling projections do not consistently decrease for Duplex. No allocation-stack capture was taken, so process-wide allocation differences are not assigned to a particular constructor. Reproduce both classifications with:

```bash
python3 eng/rpc-contract-routing-performance/profile_summary.py /path/to/cpu-artifact
python3 eng/rpc-contract-routing-performance/stream_profile_summary.py /path/to/cpu-artifact
```

### Validation and original-baseline comparison

[Run 38412340340](https://github.com/SunSi12138/SharpLink/actions/runs/38412340340) compares original baseline `3332559` with `6c06c7d` (production source identical to `7d3447d`). All eight unprofiled ABBA/BAAB slots completed on one host. Independent re-parsing verifies **1,261,568 focused RPCs**, **2,949,120 BDN sender-queued calls** and **563,840 allocation operations**. Artifact `11823253197`, ZIP SHA-256 `fe037524084fe93e2b7e97b7596b7c0bc015c08cd82b8a102db93202ea6c611e`, retains all raw samples.

**This evidence run failed its original allocation gate:** baseline slot `07-base`, `rpc-add-sharedmemory-c8`, spread **108.016 B/op > 90 B/op** (samples 538.152, 570.355, 539.584, 646.168, 551.168; median 551.168). All 16 candidate allocation cases and 15/16 baseline cases pass. The injected negative control fails as required at **1,863.692 B/op > 1,450**. No sample is dropped and no budget is changed. Timing rows remain observations, not an accepted all-green experiment. A subsequent docs/test-only head reruns the same unchanged gate; its artifact and exact-head status are separately linked in the PR review summary.

Historical BDN labels yield **1,777.262 → 2,160.206 ns (1.2155×)** and **1,782.172 → 2,152.620 ns (1.2079×)**. Both labels execute the same expression; neither is a receiver-completion measurement.

| Transport/scenario | Throughput candidate/base | Base P99 us | Candidate P99 us | Base P99.9 us | Candidate P99.9 us |
|---|---:|---:|---:|---:|---:|
| sharedmemory-clientstream | 0.7205 | 290.620 | 568.006 | 377.471 | 958.472 |
| sharedmemory-duplex | 0.7883 | 747.108 | 1209.508 | 951.733 | 1792.713 |
| sharedmemory-oneway-receiver | 0.7384 | 1105.652 | 1109.788 | 1150.003 | 1158.167 |
| sharedmemory-oneway-sender | 0.8281 | 10.120 | 12.099 | 17.642 | 21.342 |
| sharedmemory-serverstream | 0.9338 | 239.296 | 376.899 | 331.261 | 509.060 |
| sharedmemory-unary | 0.9754 | 194.248 | 198.909 | 303.122 | 296.212 |
| tcp-clientstream | 0.8027 | 454.382 | 550.102 | 1491.495 | 1083.621 |
| tcp-duplex | 0.6620 | 860.177 | 1140.474 | 1061.105 | 1708.110 |
| tcp-oneway-receiver | 1.2568 | 1099.838 | 1104.936 | 1164.581 | 1181.972 |
| tcp-oneway-sender | 0.8671 | 8.463 | 9.254 | 17.476 | 19.259 |
| tcp-serverstream | 0.9174 | 410.529 | 326.582 | 960.298 | 702.011 |
| tcp-unary | 0.9172 | 113.922 | 136.976 | 203.632 | 204.213 |

The batch OneWay workload through final receiver business completion is **264,692.5 → 233,605.5 op/s (0.8826×) TCP**, and **259,147.3 → 228,717.4 op/s (0.8826×) SharedMemory**. Sender-only completion and per-request receiver barriers are distinct boundaries and cannot replace these results. ClientStreaming remains **19.73% / 27.95%** slower and Duplex **33.80% / 21.17%** slower for TCP / SharedMemory against the original baseline. Different runner hosts prevent treating these ratios versus historical ratios above as a paired measurement of this round's gain.

The median-of-slot canonical OneWay allocation is **800.156 → 800.120 B/completed op**. The independently passing [PR Fast allocation job](https://github.com/SunSi12138/SharpLink/actions/runs/38412357710/job/115347685458) does not override the baseline spread failure in this separate experiment.

| Validation on `6c06c7d` | Observed result |
|---|---|
| [Compilation](https://github.com/SunSi12138/SharpLink/actions/runs/38412340340/job/115347852430) and [complete regression](https://github.com/SunSi12138/SharpLink/actions/runs/38412340340/job/115348298095) | Release/Debug builds: zero warnings/errors. Unit 2,174; Generator 271; Load 77; StreamLoad 21; real-transport Integration 469; Debug admission rollback 1. All zero failures/skips. Formatting and architecture guards pass. |
| [NativeAOT, Trim, packages](https://github.com/SunSi12138/SharpLink/actions/runs/38412340340/job/115348297771) | Project/package consumers, TCP/SharedMemory AOT, SharpPack/pre-credit, rooting, partial/late Trim, eight package/symbol pairs, ABI-mixing rejection, wire-process gate and quickstart pass. |
| [Packaged public API](https://github.com/SunSi12138/SharpLink/actions/runs/38412340340/job/115348297697) | Complete public/protected surface and SDK forwards match the reviewed baseline of all eight packages. |
| Repeated parallel suites | [Hosting](https://github.com/SunSi12138/SharpLink/actions/runs/38412358258), [Snapshot](https://github.com/SunSi12138/SharpLink/actions/runs/38412357597), [Terminal](https://github.com/SunSi12138/SharpLink/actions/runs/38412357379) pass their unchanged full-suite/old-negative checks. [Lease](https://github.com/SunSi12138/SharpLink/actions/runs/38412357779) fails the fourth full run at the refresh fixture described below; this row is not called green. |

Failure history is part of this round's evidence. The new disabled-telemetry test initially failed because a fresh filtered TUnit process still installs the default HTML reporter's external Activity collector. Only its child now sets `TUNIT_DISABLE_HTML_REPORTER=true`; the assertions that no observers exist and no descriptor is resolved remain mandatory. Parent reporting stays enabled. An earlier refresh interaction test, and the fourth Lease full run's `FixedReplacementDisconnectShouldCleanupAndReconnect`, treated the pre-publication fixed-pool eligibility hook as completed route publication. The test corrections join the existing owning publication gate before the next RPC. No retry, longer timeout, skipped test or production routing change is used.

The .NET 10.0.112 SDK and matching NuGet cache were extracted and checksum-verified locally, with a workspace activation script. This sandbox cannot initialize CoreCLR (`0x8007000E`), so it is not represented as a successfully runnable local .NET host. All actual C# execution reported here is from the linked Actions jobs.

**Review boundary:** the implementation preserves acceptance-before-producer and reduces targeted construction work, but baseline performance equivalence is not established. The OneWay sender/receiver and short-stream regressions remain material; the adverse single-factor rows and failed baseline allocation spread are explicitly retained. PR #861 stays Ready for review. Exact final-head CI and any subsequent unchanged-budget rerun are recorded in the PR summary, separately from this immutable-source experiment.
