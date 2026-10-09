# Read-ownership dispatch allocation micro harness

This is a controlled dispatch/allocation harness for comparing the same read-consumer usage against an instrumented framework-controlled `PipeReader`, both raw and wrapped by `ReadOwnershipPipeReader`. It is not an RPC throughput benchmark, a dispatch-exception test, or a buffer/exception lifetime proof.

## Invocation

Build in Release using the repository SDK, then execute the output DLL in a fresh process:

```sh
DOTNET_TieredCompilation=0 SHARPLINK_COMMIT=<exact-source-commit> \
  dotnet <output>/SharpLink.Benchmarks.dll <arm> <output.json> 10000 main
DOTNET_TieredCompilation=0 SHARPLINK_COMMIT=<exact-source-commit> \
  dotnet <output>/SharpLink.Benchmarks.dll <arm> <backlog-output.json> 10000 backlog
```

The first three arguments are required. Iterations must be at least 1000. The fourth argument defaults to `main`; `backlog` must run in its own fresh process, after other measured work. The outer runner owns balanced arm order, repeat counts, exact source/binary identities, process isolation, and performance acceptance. Every cell warms up for 5000 operations. Construction has its own 10000-reader sample.

The project deliberately uses friend assembly name `SharpLink.Benchmarks` and a fixed repository-relative Runtime project reference. The identical harness builds with the baseline, prior optimized, and safe-dispatch implementations; it never sets a safe-only wrapper switch.

## Fixed cells

Each cell has a raw control followed by the wrapped usage. `Cases.All` defines the immutable names, consumer labels, categories, and backends used by schema version 1.

Main suite:

1. `ordinary-await-before-completion`: an actual async method registers its await before inline inner completion.
2. `completion-before-ordinary-await`: the inner read and live outer operation complete before the actual async consumer awaits. The outer token is still unconsumed when the consumer starts.
3. `forced-async-inner-await`: the same actual async consumer, with the inner framework core forcing asynchronous continuation dispatch.
4. `custom-synchronization-context-await`: actual async consumption captures and verifies a dedicated custom context.
5. `custom-task-scheduler-await`: actual async consumption captures and verifies a dedicated custom scheduler.
6. `publisher-synchronization-context-await`: default-context registration followed by completion under a custom publisher context.
7. `publisher-task-await`: default-context registration followed by completion inside a normal Task.
8. `explicit-late-unsafe-continuation`: a cached direct continuation is explicitly registered on a completed but unconsumed read, then consumes the result once.
9. `explicit-flow-execution-context`: a cached direct flowing continuation verifies an AsyncLocal marker distinct from the publisher's marker.
10. `reentrant-ordinary-await`: one actual async loop consumes, advances and registers the next read inside the preceding inline completion.

Backlog suite:

11. `late-continuation-backlog`: a cached notification-only continuation is registered after completion; the main thread deliberately polls, consumes and rearms while one blocked ThreadPool worker prevents notifications from running. Every notification is released and drained afterward. This reproduces overlapping late notifications without inspecting a consumed token or relying on a racing timing assumption.

The ordinary category includes before-completion, completion-before-await, forced-async inner, and Task-publisher cells. The remaining paths are separately labeled special/diagnostic. A path's actual synchronization context, scheduler, Task identity presence, and ThreadPool execution are counted at consumption in `contextObservation`; raw and wrapped dispatch can intentionally differ.

## Measurement and interpretation

- Every reader, event, cached delegate, dedicated thread, queue, context and scheduler is created outside steady-state counters. The same fixture is warmed and reused.
- Every read carries one byte with a sequence marker. Each result's payload and status, source consumption, `AdvanceTo`, consumer count, callback registration, and callback completion are checked. The harness never queries a read token after consuming it.
- Actual per-read async-method allocations are included. A total wrapped async-consumer allocation is not a wrapper allocation. The meaningful incremental allocation is wrapped minus raw within the identical usage cell.
- `GC.GetTotalAllocatedBytes(precise: true)` includes worker-thread managed allocation. CPU is process-wide `TotalProcessorTime`; wall time includes validation and synchronization. GC generation counts are reported. Exact consumer, source-callback and custom-worker counters drain first. Two stable samples of an empty/inactive ThreadPool, separated by `Thread.Yield`, fence both the start and end. The ending fence is inside elapsed/allocation measurement and is separately timed as `quiescenceSeconds`; there is no fixed millisecond stability delay. This is sampled quiescence, not a claim about every runtime housekeeping instruction.
- Task registration/publisher cells include one harness Task per read in both controls. Explicit execution-context flow includes the same AsyncLocal changes in both controls. Custom context/scheduler queue storage is reused.
- No raw latency subtraction or RPC speedup claim is justified. Across-arm comparisons use the same wrapped cell and repeated isolated processes. Scheduling transitions are visible in the context counters.
- The backlog workload is a bounded burst (5000 warmup, 10000 recommended measured reads), and framework queue growth remains included in both controls; it is not a pure ordinary-await steady-state claim. The backlog process requires successful ThreadPool minimum/maximum changes, confirmed blocker occupancy, zero notification execution before release, complete draining, and restored original limits. A dedicated non-ThreadPool watchdog fails a stuck process. It fails evidence instead of falling back to a weaker workload.
- Reader `CompleteAsync` is fully awaited outside measurement before moving to another fixture. Construction allocation includes the same fixed instrumented inner reader in both arms and excludes the retention array; it does not measure retained graph size.
- Acceptance requires the outer runner’s pinned-source, same-machine, balanced-series validation; a standalone smoke run establishes functionality only.

## Output contract

`schemaVersion=1`, `benchmark="read-ownership-dispatch"`, `suite`, source/runtime metadata, `allocationScope="process-wide-precise"`, iteration counts, fixed `rows`, separate `construction`, and explanatory `notes` are always emitted. Main contains 20 rows and backlog contains 2. Invalid/missing results, context mismatches, failed handshakes, invalid counters, nonfinite metrics, or timeouts fail the process without publishing a successful JSON report.

Each row includes total and per-operation allocated bytes, elapsed seconds, operations/second, wall and CPU nanoseconds/operation, GC counts, exact correctness counts, observed context partitions, and explicit drain/backlog verification flags. Captured context and scheduler cells must observe their selected target on every operation. The parent validator checks fixed case identities and metric arithmetic.
