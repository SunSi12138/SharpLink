# PR742 diagnostic-only profiling kit

## Status and files to publish

- `ProfileWindow742.cs`: shared two-boundary EventSource plus c8 admissions-close marker, namespace `SharpLink.Profiling742`.
- `parser/Program.cs` and `parser/ProfileParser.csproj`: parser pinned to TraceEvent 3.2.8.
- `calibration/Program.cs` and `calibration/Calibration.csproj`: independent calibration, no SharpLink runtime changes.
- Do not publish `verification/`, `reflect/`, temporary caches, binaries, or the local collector. These retain local verification and failures.

The final sources compile locally with .NET SDK 10.0.102/runtime 10.0.2 and cached TraceEvent 3.1.21. The deliverable pin remains 3.2.8; pinned online restore timed out. Real EventPipe collection is blocked by the executor's unavailable Unix IPC. No diagnostic/security/perf setting was changed to bypass it. Therefore full trace attribution, actual dropped-event decoding, and System.Threading.Lock event coverage remain CI calibration gates. Untraced calibration and deterministic parser loss/boundary self-tests pass; neither proves trace collection.

## Exact supported collection

Install/use the same tools as existing allocation profiling: dotnet-trace 10.0.745401; TraceEvent 3.2.8. Record `dotnet --info`, `dotnet-trace --version`, `dotnet-trace collect --help`, exact commands/env, SHA-256 of parser/marker/calibration/app assemblies, source trees, and source blob hashes.

Use this explicit provider list to avoid profile-name drift:

```
Microsoft-DotNETCore-SampleProfiler:0:4,Microsoft-Windows-DotNETRuntime:0x40034019:5,SharpLink-742-ProfileWindow:0xffff:4
```

The CLR mask combines GC 0x1, Loader 0x8, JIT 0x10, Contention 0x4000, Threading 0x10000, JittedMethodILToNativeMap 0x20000, Stack 0x40000000. Verbose level 5 enables GCAllocationTick. Keep default rundown enabled; do not use `--no-rundown`. The parser does not download symbols and preserves missing stacks/symbols as explicit buckets.

```
dotnet-trace collect --providers "$PROVIDERS" --buffersize 128 --output "$TRACE" -- dotnet exec "$APP_DLL" <normal workload args>
dotnet ProfileParser.dll "$TRACE" "$NORMALIZED_REPORT" "$OUTPUT_JSON"
```

Use `collect`, never privileged `collect-linux`, perf, sysctl changes, capabilities, ptrace, or security workarounds. Dotnet's EventPipe SampleProfiler is managed thread-stack sampling, NOT scheduler on-CPU sampling. Report observed sample hits per completed operation/item alongside independent process CPU us/op. Never distribute total CPU time across samples to invent per-method CPU ns or per-registration costs. External samples remain a separate class.

## Mandatory calibration before accepting profiles

Build the two projects outside the production source/binary folders. Copy/link `ProfileWindow742.cs` into calibration unchanged. Run on every workload job's runner with the same runtime/env/provider list as actual profiling:

```
dotnet ProfileParser.dll --self-test
dotnet-trace collect --providers "$PROVIDERS" --buffersize 128 --output calibration.nettrace -- dotnet exec Calibration.dll calibration.json
dotnet ProfileParser.dll calibration.nettrace calibration.json calibration-parsed.json --calibration
```

The program executes distinct non-inlined busy methods before/inside/after its measurement, distinct allocation classes in all three regions, and deliberately contends both object/Monitor and System.Threading.Lock inside. The parser requires resolved CPU-method samples inside, observed before/after samples excluded from the window, unique allocation types correctly excluded/included, and attributed contention-start stacks for both lock mechanisms. It rejects event loss, incomplete conversion, unexpected markers and unpaired contention intersecting the window. If System.Threading.Lock coverage fails, stop and report unsupported coverage; absence is not zero contention. The self-test explicitly tests the production raw/converted nonzero-event-loss rejection logic with synthetic loss counts. It does not claim to synthesize a genuinely dropped-event nettrace.

Keep every failure. If calibration fails, the job is incomplete: publish its trace/logs and diagnose it before running/accepting SharpLink profiles. Never silently retry until success. Any corrected rerun gets a new directory and retains the original failure.

## Normalized report input contract

The parent harness must validate original workload-specific configuration and report, then write these fields, matching the same process/measurement that produced the trace:

```
{
  "workload": "rpc-Server1x16",
  "transport": "sharedmemory",
  "operations": 1234,
  "items": 1234,
  "operationsStarted": 1234,
  "commit": "<exact production commit or reconstructed tree>",
  "validationFailures": 0,
  "failure": 0,
  "cancelled": 0,
  "processCpuMs": 123.456,
  "allocatedBytes": 1234567,
  "profileWindowSeconds": 10.001
}
```

- `rpc-Server1x16` requires `sharedmemory`, completed items = operations. Reject the max-operation limit and validate the real generated runner's scenario, 1 item of 16 bytes, runtime flags and requested/measured period before normalization.
- `c8-s2c-10000` allows `sharedmemory` or `tcp`, completed items = operations * 10000. Validate exactly one s2c result, concurrency 8, stream size 10000, stream receive window 8192 and connection window 65536, and completed = started = success with all error/cancel counters zero. CPU and allocations cover the full measurement+drain. The parent must never add the warmup to the denominator.
- `processCpuMs` is the actual whole-process client+server CPU delta, not wall time. `allocatedBytes` is the actual process-wide allocation delta. `profileWindowSeconds` includes drain for c8.
- Calibration uses workload `calibration`, transport `synthetic` and a synthetic source identity, and is accepted only with `--calibration`.

## Marker placement

Fully qualified calls: `SharpLink.Profiling742.ProfileWindow742.Log`.

- Generated RPC: WindowBegin("rpc-Server1x16") before the allocation/CPU snapshots, after setup/warmup/GC. WindowEnd("rpc-Server1x16", completed, completed) after final snapshots and before sorting/report creation.
- c8: only `!isWarmup`. WindowBegin("c8-s2c-10000") immediately before evidenceBefore Capture and StartMeasurement. AdmissionsClosed("c8-s2c-10000") immediately after StopStartingNewOperations. WindowEnd("c8-s2c-10000", success + failure + cancelled, checked((success + failure + cancelled) * (long)options.StreamSize)) immediately after final evidence Capture. This includes all drained completions and excludes later reporting.
- All arms must receive byte-identical diagnostic harness/marker sources. Keep the uninstrumented production timing builds in different directories. Never use traced/instrumented throughput for acceptance.

## Minimal matched experiment

The parent owns source materialization and execution. Three exact arms:

- dev: 0fe26024b114bb6e78411a9b86276086c045d03d
- published: e834d3c28c87ad496989af925515cf21babd308d
- candidate production tree: 65bcb96eb293372cf78feeb238998febec941e96, reconstructed from preflight 40c30552f8a099ecda55525163e0b163adbd2357 using the previously verified recipe; verify tree BEFORE diagnostic edits.

Three independent workload jobs: generated SHM Server1x16, bounded c8 TCP s2c/10000, bounded c8 SHM s2c/10000. Each job compares all three arms on its own same runner, same processor count/affinity, .NET runtime, R2R/tiering/PGO flags and socket defaults. Use a fixed 10-second diagnostic interval after identical warmup and three ordered repeats, Latin rotation: dev/published/candidate; published/candidate/dev; candidate/dev/published. Nine diagnostic processes per job, 27 total, no retries. A generated max-op bound should be generous enough not to stop the measurement early. c8 retains its bounded windows and 30-second drain cap. Unmodified timings remain an independent paired population; one must not substitute a new longer trace window for the established throughput regression evidence.

Normalize each trace independently by its actual successful completed operations/items; compare distributions/medians of normalized samples and waits, not pooled raw counts. Retain full stacks; deduplicate recursive method names per sample for inclusive method counts. Inclusive methods overlap and cannot be summed. Prioritize changed full stacks touching StreamFlowController/StreamManager/registration, PooledAsyncStreamDispatcher, wrappers, ThreadPool/ExecutionContext, Lock/Monitor/spin and SHM notification/control paths. A disappearance due to inlining is not proof of eliminated work; inspect callers and aggregate stable path groups as a secondary manually reviewed view.

GC allocation-tick type and stack weights are sampled estimates. Keep raw sample counts, estimated B/op and B/item, actual process allocation B/item and coverage ratio. Allocation samples near a boundary may include interval bytes allocated before it. Sparse type deltas or missing stacks are inconclusive. Contentions crossing the window are explicitly proportionally clipped; full inside event durations use the runtime's DurationNs. Monitor/Lock events do not capture every interlocked, spin, asynchronous or transport wait. Zero events is only interpreted after calibration establishes event coverage.

## Official sources verified

- dotnet-trace CLI/providers and thread-sampling caveat: https://learn.microsoft.com/en-us/dotnet/core/diagnostics/dotnet-trace
- CLR contention event IDs/DurationNs: https://learn.microsoft.com/en-us/dotnet/fundamentals/diagnostics/runtime-contention-events
- CLR manifest: https://raw.githubusercontent.com/dotnet/runtime/main/src/coreclr/vm/ClrEtwAll.man (GCAllocationTick_V4 is GCKeyword, Verbose)
- TraceEvent 3.2.8 conversion/CallStack APIs and event-loss propagation: https://raw.githubusercontent.com/microsoft/perfview/v3.2.8/src/TraceEvent/TraceLog.cs
- SampleProfiler event types Managed/External/Error: https://raw.githubusercontent.com/microsoft/perfview/main/src/TraceEvent/EventPipe/SampleProfilerTraceEventParser.cs
- .NET10 Lock event emission: https://raw.githubusercontent.com/dotnet/runtime/v10.0.0/src/libraries/System.Private.CoreLib/src/System/Threading/Lock.cs
