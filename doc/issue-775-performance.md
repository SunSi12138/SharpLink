# Issue #775 contract routing performance evidence

Status: the historical #802 regression has been independently reproduced from retained raw evidence. Three complete new comparisons still show approximately 24–30% greater OneWay sender cost and slower receiver-completed throughput. CPU samples and emitted assembly identify concrete serialization and copying work for follow-up; they do not establish the full regression's cause. Attribution and performance acceptance remain open. Successful compilation or successful benchmark execution alone is not a performance acceptance decision.

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

The next source candidate specializes ordinary identity writing, isolates receipt-only work, publishes client routes atomically, and reuses already parsed identity on immediate admitted dispatch. These changes are motivated by the emitted work and a separately reproduced publication race. Their performance effect is not yet measured. The CPU workflow now defaults to the exact workflow SHA, with an optional exact SHA override, so the next explicitly triggered capture can verify the candidate's emitted work against the same frozen base. Profiled timings remain diagnostic only.

### Current evidence and review decision

The earlier [run 38329394037](https://github.com/SunSi12138/SharpLink/actions/runs/38329394037) for head `72c30a8b5399a43eb2ea6c6ce7aeb0f096ef26a4` stopped at two test-fixture constructor migrations and produced no timing evidence. The first complete comparison above identifies remaining regressions. The final reviewed source SHA, completed artifact and explicit disposition of those regressions are still required before making a performance acceptance claim.

The workflow uploads `issue775-performance-<sha>-<attempt>` with raw BenchmarkDotNet JSON, per-operation ticks, separate sender/receiver counts, allocation samples, build logs and provenance. `summary.json` retains base/head samples and ratios for throughput, receiver-completed throughput, tails, CPU, diagnostic allocation and drain time. `summary.md` presents the main comparisons. To independently validate a downloaded artifact:

```bash
python3 eng/rpc-contract-routing-performance/summarize.py /path/to/performance-artifact
```

Review must account for every RPC shape and both transports. In particular, client-stream and duplex calls now wait for stream acceptance before advancing producers; short-stream measurements must retain the actual cost of that ordering. A persistent regression requires investigation against the unchanged control workload, including sender/receiver pacing and queue contention when indicated by the old profiles. Passing the execution and allocation checks establishes evidence integrity; the measured throughput and tail ratios still require an explicit, source-matched performance decision.
