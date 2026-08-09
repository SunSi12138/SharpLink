# P2-00P client/server performance and layout baseline

This is an advisory baseline for the P2 mechanical refactors and the later P4
performance investigations. P2-00P changes benchmark and reporting code only;
the production `src/**` diff is empty.

## Reproducibility envelope

- Benchmarked commit: `570c3a2dd284d562dcb2b8e7e2bc8fd1b78024da`
- Integration base: `f6b7f1bc549d3f6d9e0e0d514a8bbda7167d0937`
- Host: `SunSiUbuntu`, AMD Ryzen 9 7950X, 16 cores / 32 logical CPUs, 60 GiB RAM
- OS/runtime: Ubuntu 26.04, Linux 7.0.0-28, .NET runtime 10.0.10, SDK 10.0.110
- CPU governor: `performance`; frequency boost enabled
- Runtime mode: tiered compilation enabled, dynamic PGO enabled, workstation GC
- Macro evidence: five fresh-process repetitions per scenario, 2,000 warmup calls,
  three measured seconds, forward/reverse scenario order on alternating runs
- BenchmarkDotNet feature evidence: `Short` job with five process launches per
  scenario, `MemoryDiagnoser`, `ThreadingDiagnoser`, and full statistics
- Validation: 100/100 macro runs returned the expected result; no operation limit
  was hit; all 56 BenchmarkDotNet cases completed without errors or reported issues

The complete 264-file raw evidence set is retained in the isolated task checkout
at `artifacts/p2-performance-baseline/formal-570c3a2-20260809`. It contains the
100-line JSONL input, individual JSON runs, BenchmarkDotNet logs/CSV/HTML, the
environment fingerprint, IL layout, JIT timing, Tier0/FullOpts disassembly, and
hardware-counter output.

Re-run the same matrix in a fresh output directory with:

```bash
SHARPLINK_BENCHMARK_SHA=570c3a2dd284d562dcb2b8e7e2bc8fd1b78024da \
  ./eng/run-p2-performance-baseline.sh \
  artifacts/p2-performance-baseline/<fresh-run-name>
```

Hardware counters are collected only when the host policy permits them. The
formal run temporarily permitted unprivileged collection and restored
`kernel.perf_event_paranoid` to `4` after collection.

## Fresh-process feature matrix

These are medians of five independent macro runs. QPS deltas are relative to
`StaticDefault` for server scenarios and `FixedDefault` for client scenarios.
CPU/op is aggregate process CPU for the in-process client and server, so it must
not be interpreted as one side's exclusive CPU cost.

### Server

| Scenario | QPS | vs baseline | P50 us | P99 us | P99.9 us | CPU us/op | Alloc B/op | First call us |
|---|---:|---:|---:|---:|---:|---:|---:|---:|
| StaticDefault | 60,591 | 0.0% | 14.48 | 25.37 | 33.29 | 74.08 | 952 | 10,569.00 |
| AdmissionImmediate | 57,415 | -5.2% | 14.73 | 28.44 | 36.85 | 80.31 | 1,408 | 13,001.30 |
| ServerInterceptor | 59,701 | -1.5% | 14.62 | 27.23 | 34.83 | 74.75 | 1,264 | 12,489.90 |
| MetricsClientAndServer | 57,612 | -4.9% | 14.73 | 29.54 | 36.86 | 74.54 | 1,632 | 11,868.30 |
| ServerTraceOnePercent | 57,578 | -5.0% | 14.81 | 27.73 | 35.78 | 78.57 | 1,800 | 12,700.40 |
| ServerTraceAll | 57,363 | -5.3% | 14.88 | 27.46 | 35.54 | 80.21 | 1,800 | 12,496.90 |
| DynamicRegisteredStaticHit | 60,180 | -0.7% | 14.36 | 25.55 | 32.54 | 73.97 | 952 | 10,597.20 |
| DynamicServiceActual | 57,467 | -5.2% | 14.56 | 26.91 | 33.38 | 78.90 | 1,032 | 12,563.20 |

### Client

| Scenario | QPS | vs baseline | P50 us | P99 us | P99.9 us | CPU us/op | Alloc B/op | First call us |
|---|---:|---:|---:|---:|---:|---:|---:|---:|
| FixedDefault | 60,920 | 0.0% | 14.31 | 24.37 | 30.01 | 74.05 | 952 | 10,583.50 |
| StaticTwoEndpoints | 60,024 | -1.5% | 14.55 | 25.68 | 32.39 | 75.49 | 952 | 11,469.90 |
| StaticFourEndpoints | 60,360 | -0.9% | 14.41 | 25.90 | 36.25 | 76.68 | 952 | 11,705.80 |
| StaticSixteenEndpoints | 59,550 | -2.2% | 14.53 | 29.30 | 38.91 | 76.11 | 952 | 11,393.00 |
| DynamicFourEndpoints | 61,896 | +1.6% | 14.42 | 24.66 | 35.43 | 76.50 | 952 | 11,175.50 |
| RetryFirstSuccess | 58,692 | -3.7% | 14.71 | 26.77 | 38.10 | 77.32 | 1,512 | 13,260.60 |
| AlwaysAcceptAdmission | 59,935 | -1.6% | 14.78 | 25.13 | 32.96 | 74.89 | 1,176 | 12,262.70 |
| ClosedCircuitBreaker | 56,248 | -7.7% | 15.19 | 30.91 | 41.38 | 78.42 | 1,176 | 14,581.10 |
| ClientInterceptor | 58,663 | -3.7% | 14.60 | 26.58 | 33.77 | 71.56 | 1,880 | 13,333.30 |
| MetricsClientAndServer | 58,724 | -3.6% | 14.77 | 25.57 | 32.59 | 72.07 | 1,632 | 11,923.70 |
| ClientTraceOnePercent | 6,645 | -89.1% | 146.39 | 246.14 | 277.75 | 263.79 | 1,826 | 13,061.00 |
| ClientTraceAll | 6,638 | -89.1% | 149.79 | 248.47 | 286.29 | 265.12 | 1,826 | 13,475.10 |

The trace result is not a claim that exporting one percent of spans costs the
same as exporting all spans. The listener returns `PropagationData` for the
non-recorded calls, so both cases intentionally retain per-call activity
propagation. The result shows that sampling after activity creation does not
remove the dominant client-side cost in this setup.

Process resource medians remained bounded. The default client used 19 threads
and 78.3 MiB working set; static four and dynamic four endpoints both used 26
threads and 80.9 MiB; static sixteen endpoints used 26 threads and 82.4 MiB.
These are whole-process snapshots, not retained-object measurements. Median
Gen1/Gen2 collections were at most one in every scenario, and no run hit the
two-million-operation cap.

## BenchmarkDotNet steady-state evidence

The five-launch feature job confirms allocation differences more reliably than
the macro QPS deltas. Several latency distributions overlap, so small mean
differences remain advisory.

| Scenario | Mean | StdDev | Allocated | Stable interpretation |
|---|---:|---:|---:|---|
| Server static default | 16.61 us | 1.250 us | 952 B | baseline |
| Server admission immediate | 17.91 us | 0.967 us | 1,408 B | fixed +456 B; dedicated admission A/B confirms cost |
| Server interceptor | 17.25 us | 0.951 us | 1,264 B | fixed +312 B; time overlaps baseline |
| Server metrics | 16.75 us | 1.315 us | 1,632 B | fixed +680 B; time overlaps baseline |
| Server trace 1% | 17.79 us | 0.921 us | 1,792 B | fixed +840 B |
| Server trace 100% | 17.24 us | 1.284 us | 1,792 B | fixed +840 B; time overlaps 1% |
| Dynamic registered, static hit | 16.82 us | 1.142 us | 952 B | no stable static-hit penalty detected |
| Dynamic service actual | 16.61 us | 1.302 us | 1,032 B | +80 B; time overlaps baseline |
| Client fixed default | 17.01 us | 1.080 us | 952 B | baseline |
| Client static 2/4/16 | 17.03 / 16.53 / 17.01 us | 1.155 / 1.229 / 1.071 us | 952 B | topology costs overlap baseline |
| Client dynamic four | 17.64 us | 0.093 us | 952 B | macro result moves the other direction; no stable penalty claim |
| Client retry, first success | 17.49 us | 0.788 us | 1,512 B | fixed +560 B |
| Client admission, always accept | 17.48 us | 0.983 us | 1,176 B | fixed +224 B |
| Client breaker, closed | 16.86 us | 1.268 us | 1,176 B | fixed +224 B; time overlaps baseline |
| Client interceptor | 17.21 us | 1.008 us | 1,880 B | fixed +928 B |
| Client metrics | 17.94 us | 0.223 us | 1,632 B | fixed +680 B |
| Client trace 1% / 100% | 251.45 / 250.40 us | 19.658 / 19.635 us | 1,824 / 1,825 B | dominant and reproducible listener cost |

The dedicated admission benchmark measured `Disabled` at 15.04 us / 952 B and
`ImmediatePermit` at 16.60 us / 1,408 B, a ratio of `1.10 +/- 0.05`. Admission
remains directional evidence until P4 adds a lower-variance dispatch-core
benchmark.

The pure parser frame mix uses 100 frames per invocation. Continuous unary
request parsing measured 32.20 ns/frame; the 90% unary plus OneWay, StreamData,
StreamComplete, Cancel, Ping, Pong, error response, and WindowUpdate mix measured
30.44 ns/frame (`0.95 +/- 0.05`). The mixed frames are not payload-size matched,
so this is evidence that rare frame parsing is not obviously pathological, not
evidence that mixed traffic is intrinsically faster.

Existing reference jobs also completed:

- Unary RPC Add: 23.34 us / 952 B at payload parameter 16; OneWay enqueue:
  0.814 us / 1,248 B.
- Streaming at size 32: upload 34.06 us, download 32.15 us, duplex 48.10 us,
  two-input merge 50.12 us.
- Streaming at size 256: upload 110.05 us, download 109.72 us, duplex 170.69 us,
  two-input merge 199.38 us.
- Runtime core: request-frame write 6.07 ns, pending register/complete 63.29 ns,
  contiguous request parse 29.61 ns, one-byte segmented metadata parse 316.18 ns.

## Code layout and JIT evidence

The release assemblies were 223,744 bytes for `SharpLink.Server.dll` and 391,168
bytes for `SharpLink.Client.dll`. Moving methods between partial files in P2 must
not change these method IL sizes or native-code sizes.

| Target | Method IL | State-machine `MoveNext` IL | Instrumented Tier0 native | FullOpts native |
|---|---:|---:|---:|---:|
| Server `ProcessRequestLoop` wrapper / state machine | 63 B | 2,370 B | 148 / 8,058 B | 130 / 6,822 B |
| Server `DispatchRpcAsync` | 2,320 B | - | 9,344 B | 9,796 B |
| Server `DispatchOneWayRpc` | 1,393 B | - | 5,094 B | 5,326 B |
| Server `InvokeServiceTrackedAsync` | 95 B | - | 525 B | 657 B |
| Client `ProcessRequestLoop` wrapper / state machine | 71 B | 1,565 B | 175 / 5,617 B | 135 / 5,037 B |
| Client `InvokeUnaryAsync` | 175 B | - | 749 B | 1,276 B |
| Client `InvokeUnaryCoreAsync` | 171 B | - | 821 B | 1,506 B |
| Client retry state machine | 115 B | 1,027 B | 325 / 3,538 B | inlined wrapper / 2,207 B |
| Client `InvokeUnaryRetryAttemptAsync` | 140 B | - | 661 B | 1,201 B |
| Static `SelectEndpoint` | 250 B | - | 1,105 B | 495 B |
| Static `SelectConnection` | 128 B | - | 634 B | 263 B |

`InvokeUnaryWithOptionalRetryAsync` has 61 B IL and 334 B Tier0 native code; it
was inlined in the FullOpts probe. Dynamic and static selection IL are nearly
identical: endpoint selection is 255/250 B and connection selection is 128/128 B.

Process-wide JIT timing was captured separately from method code size. The
runtime emitted two internal timing groups per probe; they are retained raw and
are not combined into a per-method claim. The complete disassembly includes the
actual async `MoveNext` bodies, not only their small startup wrappers.

## Hardware counters

`perf stat` collected three repetitions for server `AdmissionImmediate` and
client `StaticFourEndpoints`. Eight counters were multiplexed and ran about 62%
of wall time, so these scaled counts are directional:

| Probe | IPC | Branch miss rate | L1I miss/load rate |
|---|---:|---:|---:|
| Server admission | 0.64 | 8.35% | 0.43% |
| Client static four | 0.63 | 7.97% | 0.53% |

The AMD iTLB aliases reported unusually high miss/load ratios while multiplexed;
those raw values are preserved but are not used for a decision. A future layout
experiment should collect fewer events per pass and compare base/head in an
interleaved run before attributing a change to instruction-cache behavior.

## P4 decisions supported by this baseline

1. **P4-03A should proceed as a measurement task.** Server metrics and tracing
   add stable per-call allocation, while the current loopback variance cannot
   isolate descriptor lookup from ambient call-context push/restore. The next
   task should compare direct core, tracked wrapper, context read/not-read, and
   telemetry listener states before changing production code.
2. **P4-06 is worth a focused layout experiment, not an assumed refactor.** The
   server dispatch bodies and request-loop state machine are large, and the
   dedicated admission A/B shows a real 10% / +456 B cost when admission is
   enabled. However, dynamic registration does not penalize static hits, the
   parser frame mix is not slower, and the multiplexed L1I rate is low. A
   base/head disassembly experiment must demonstrate at least the documented
   3%-5% production-workload gain or clear JIT/native-code improvement.
3. **P4-07 should not begin by splitting fixed/static/dynamic clients.** Endpoint
   counts and stable dynamic snapshots preserve 952 B/op and show no consistent
   steady-state latency direction. Scope the investigation to optional-feature
   costs instead: retry (+560 B), interceptor (+928 B), telemetry (+680 to
   +873 B), and retained topology memory. Client tracing is the strongest
   measured opportunity, but it requires a propagation/export semantics review,
   not a branch-removal guess.

This baseline is advisory. P2 behavior-preserving PRs compare exact IL/native
layout and use same-host interleaved base/head runs; one noisy QPS sample must not
override P99/P99.9, allocation, resource, or lifecycle correctness evidence.
