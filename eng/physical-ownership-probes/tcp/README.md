# Real TCP physical-ownership stream probe

This isolated probe measures complete scalar server-stream RPCs, not a client-side short circuit or an allocation-to-throughput inference. It compares two **already built** SharpLink runtime library sets using one byte-identical generated contract/service module and one byte-identical probe executable. It does not rebuild either production tree.

## Prerequisites and preparation

Use .NET 10 and built baseline/candidate directories containing the required SharpLink runtime DLLs and their dependencies. Supply the **baseline** `SharpLink.Generator.dll` explicitly. Linux `taskset` is used by default; select CPUs actually available on the host, or use `--affinity none` and record that different configuration.

```bash
python3 eng/physical-ownership-probes/tcp/prepare.py \
  --baseline-libraries /absolute/baseline/test/SharpLink.UnitTests/bin/Release/net10.0 \
  --candidate-libraries /absolute/candidate/test/SharpLink.UnitTests/bin/Release/net10.0 \
  --baseline-generator /absolute/baseline/src/SharpLink.Generator/bin/Release/netstandard2.0/SharpLink.Generator.dll \
  --output /tmp/physical-tcp-prepared

python3 eng/physical-ownership-probes/tcp/run_matrix.py \
  --prepared /tmp/physical-tcp-prepared \
  --output /tmp/physical-tcp-results \
  --baseline-revision BASELINE_FULL_COMMIT \
  --candidate-revision CANDIDATE_FULL_COMMIT \
  --rounds 6 --warmup 3 --seconds 6 --affinity 2,3 \
  --workers 1 --items 1,100

python3 eng/physical-ownership-probes/tcp/summarize.py \
  --input /tmp/physical-tcp-results
```

All tools accept `--help`. Preparation and matrix execution require new output directories, preventing accidental overwrites of existing results. `prepare.py` builds copied probe sources in an isolated output tree, then snapshots the supplied dependency DLLs. It records source hashes, binary hashes, generator identity, exact build commands and source library directories. It never builds production projects. Use `--dotnet /absolute/path/to/dotnet` on preparation and execution when a specific installation is required.

Freeze both snapshots before sampling. Coordinate a quiet window: do not build, test, run other benchmarks, overwrite DLLs, or run CPU-heavy summaries during the matrix. Save every outcome, including unfavorable results and failures. Never pool different worker counts, CPU affinities, runtime modes or runtime binaries as if they were repetitions of one workload.

## What is exercised

- Actual loopback TCP with client and server in one process, one endpoint, explicit MinConnections = MaxConnections = 1. Actual ready connection count must equal one before and after each sample.
- Static intercepted and dynamic-module intercepted streams, selected with `--modes static-intercepted,dynamic-intercepted`. Both have exactly one pass-through client interceptor and no server interceptors. Dynamic registration occurs after client/server construction; static registration occurs beforehand.
- First-call wrapper inspection must find `InterceptedAsyncEnumerable<int>.GetAsyncEnumerator` and records its chain down to `PooledAsyncStreamDispatcher<int>`. Dynamic mode also asserts `SharpLinkModuleRpcChannel.InvokeServerStreamingDeferred`.
- The generated server stub calls `bridge.PumpOutboundStreamAsync` with the scalar int codec. The service yields the integers synchronously. Serialization, transport, server dispatch, stream lifecycle, deserialization and disposal remain in the measured path. All item values, order, count and checksums are validated for every operation.
- One and 100 item streams distinguish tiny full-call cost from moderate streaming work. `--workers 1` is the primary closed-loop workload. Other worker counts require separate result directories and interpretation.
- Request timeout disabled; call cancellation None; dynamic mode retains its actual module cancellation token. Heartbeat interval/timeout are one/two hours. Other runtime/flow-control settings are default. This is happy-path throughput, not exceptional-cancellation correctness testing.

## Measurement and interpretation

Each sample is a fresh process, normally using .NET's default tiered compilation/PGO/ReadyToRun and workstation GC. JIT/tiering environment overrides are removed by the matrix runner. Runtime, GC, processor count, relevant environment and binary hashes are recorded. The runtimeconfig has no forced tiering or GC mode.

Default warmup is three seconds followed by six measured seconds. Baseline/candidate order alternates by round and scenario order rotates. A full completed RPC stream is one operation. Latency arrays are preallocated; report/sort work is excluded. All in-flight operations are drained, and throughput uses actual elapsed time including final drain. Each worker has a fixed one-million-operation sample capacity and fails visibly rather than dropping samples if that limit is reached.

Latency is closed-loop, complete client-observed stream duration. There is no open-loop load model or coordinated-omission correction. Allocation is the process-wide `GC.GetTotalAllocatedBytes(precise: true)` delta, including client and server managed allocation, not a thread-local measurement or native transport allocation. CPU is process `TotalProcessorTime`. First-call introspection is outside measurement, so first-call timing is diagnostic only, not a clean startup benchmark.

`/proc/stat` and `/proc/loadavg` are recorded around each process where available. Affinity does not reserve a CPU; a quiet local build window does not eliminate hypervisor steal or other host activity. The October 22, 2026 matrix had substantial variation and 2.35–4.81% affinity-CPU steal despite local build avoidance.

The summarizer reports all complete paired samples, baseline/candidate medians and ranges, paired ratios and deterministic 20,000-resample descriptive bootstrap intervals. It separately reports incomplete pairs. Six pairs are underpowered for small changes; these intervals are **not** an equivalence test and do not correct host drift/dependence. Do not infer throughput from allocation/JIT size, claim a noisy null result proves no regression, or discard unfavorable samples.

This is bounded two-core loopback evidence, not production certification. It does not cover separate hosts, multiple clients/connections, large/complex payloads, asynchronous/expensive service producers, server GC, other transports or exceptional behavior.

## Measured-source provenance

`probe/Program.cs` and `module/Contract.cs` are byte-identical to the isolated sources used for the 48-process comparison of baseline `13928a35` and candidate `520b544b56af4d4b298446b96c3a39446abedfa4`:

- Program.cs SHA256: `ff8d440346859f07110f7dcf42c5f425bb7c272f7117852b6e95ea4bd126b684`
- Contract.cs SHA256: `6bb3353fa8a211f648eb4c6adfeaac2f910abc80dff18e57b66de17232df9082`

Orchestration paths have been generalized for repository use. Rebuilding in a new path may change binary hashes even when source is identical; each newly prepared matrix records its actual hashes. The first bounded matrix was inconclusive for tiny dynamic streams, not proof of performance equivalence. See the separately retained raw evidence for that run.
