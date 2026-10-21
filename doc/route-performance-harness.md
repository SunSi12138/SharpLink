# Bounded immutable-route transport evidence harness

`eng/immutable-route-performance` runs real loopback TCP using the production client,
server, routing, module lifetime and serializers. Its tiny fixture uses handwritten
descriptors, an emitted manifest and a stub shared with the correctness tests. It
does not measure a source-generated prepared proxy or a production preparation API.

The harness deliberately uses assembly name `SharpLink.UnitTests` to reuse the
existing friend access intended for experiments/tests. It is not packaged, included
in the solution, or linked into any production project. Candidate-specific cold
configuration uses reflection so the exact same harness and linked fixture compile
against the unchanged dev baseline. There is no reflection in measured loops.

## Build and run

Use the repository-pinned SDK in both independent worktrees. Copy the harness and
`test/SharpLink.UnitTests/Runtime/ImmutableRouteTransportSupport.cs` unchanged to
the baseline worktree; do not copy candidate production files.

```sh
dotnet build eng/immutable-route-performance/ImmutableRoutePerformance.csproj -c Release \
  -m:1 -p:BuildInParallel=false -p:UseSharedCompilation=false

dotnet eng/immutable-route-performance/bin/Release/net10.0/SharpLink.UnitTests.dll \
  --mode ordinary --label baseline --warmup 10000 --unary 10000 --oneway 100000 \
  --timeout-seconds 120 > baseline.json

dotnet eng/immutable-route-performance/bin/Release/net10.0/SharpLink.UnitTests.dll \
  --mode prepared --label candidate --warmup 10000 --unary 10000 --oneway 100000 \
  --timeout-seconds 120 > candidate.json
```

Use the first invocation against baseline binaries and the second against candidate
binaries, not merely different mode flags on the same build. Candidate ordinary
mode is an optional attribution control, not the original baseline. Pin runtime,
environment, operation counts and fixture source. Record exact production SHAs and
SDK version beside the JSON. Run interleaved repeated blocks in separate processes
with no concurrent builds/tests; a single launch is a smoke test, not acceptance.

## Measurements and limits

- Unary is serial empty-request/int32-response RPC. It reports end-to-end throughput
  and per-call p50/p95/p99/max latency, including timestamp sampling overhead in both
  builds. It is not saturated concurrent throughput.
- OneWay sender acceptance time is separate from the time all expected service
  execution counters become observable. A Ping or later Unary is not used as a
  completion fence. The counter increments in the tiny service body; it is **not**
  proof every server lease/transport cleanup finished. Polling every 1 ms adds
  observation granularity to that second metric.
- Allocation is `GC.GetTotalAllocatedBytes(precise: true)` for the entire process,
  including client, server and background activity. It is neither caller-thread
  allocation nor a pure per-RPC steady-state allocation guarantee. The latency
  sample array is allocated before sampling. Sorting is outside timed sampling.
- Both modes use the same direct-channel helper and per-call descriptor passing
  pattern. This isolates the candidate footprint and routing path, not differences
  between a generated static descriptor and a hypothetical generated instance
  descriptor. No interceptor/policy is configured in this performance case.
- Dynamic singleton registration, request timeout, call shape and payload are the
  same. Cold registration, handshake, reflection and preparation are outside loops.
- An overall budget bounds the run. Timeout causes client/server shutdown and a
  nonzero exit, not a successful partial result. Do not count failed launches.
- Shared-cloud scheduling, tiered compilation, JIT, GC and sampling noise remain.
  “No statistically significant difference” is not evidence of equivalence or zero
  overhead. Formal acceptance requires a predefined budget and adequate matched
  repeated samples; a tiny loopback result does not replace the broader matrix.

The JSON records build SDK, runtime, architecture, processor count, descriptor size, GC mode,
explicit tiering environment overrides, counts and operation results. Build SDK is
embedded from MSBuild's `NETCoreSdkVersion`, not inferred from the runtime. Also
capture `dotnet --version` and the production and harness commit hashes externally.

## Bounded interleaved comparison

Only start after correctness passes, builds finish, and no other test/build/benchmark
jobs are running. The runner does not build or copy sources. It requires the exact
baseline HEAD, no baseline tracked modifications, and matching harness/helper hashes.
Use the same SDK executable for both binaries:

```sh
python3 eng/immutable-route-performance/run_abba.py run \
  --baseline /workspace/scratch/9e5903e2fbfb/sharplink-immutable-baseline \
  --candidate /workspace/scratch/9e5903e2fbfb/sharplink-immutable-route \
  --dotnet /workspace/scratch/9e5903e2fbfb/.sharplink-dotnet/dotnet \
  --output /workspace/scratch/9e5903e2fbfb/immutable-route-abba-evidence \
  --candidate-ordinary
```

The output directory must be new and outside both source trees. Four ABBA blocks
produce eight baseline-ordinary and eight candidate-prepared process runs.
The optional switch adds two candidate-ordinary diagnostic runs afterward, not
included in the primary comparison. Every run uses 10,000 warmup iterations,
10,000 measured Unary calls, 100,000 OneWay calls, a 120-second harness budget,
and a 180-second process watchdog. Tiered compilation and tiered PGO are explicitly
enabled identically. Do not run JIT diagnostics during this comparison.

Each process JSON and stderr file is preserved. The manifest records SDK/runtime
configuration, HEAD/tree/status, tracked diff hash, untracked file hashes,
harness/helper hashes, and DLL hashes before and after the run set. Changed
sources/binaries, incomplete counts, mismatched runtime settings, invalid JSON,
nonfinite metrics, timeouts, or process failures stop the comparison rather than
silently dropping samples. An incomplete manifest cannot be summarized.

`summary.json` reports each individual process-level metric, median, min/max, and
candidate-to-baseline median ratio. These are descriptive summaries, not pooled
latency percentiles, confidence intervals, equivalence bounds, or a zero-overhead
claim. Regenerate only from a complete evidence directory:

```sh
python3 eng/immutable-route-performance/run_abba.py summary --output PATH
```
