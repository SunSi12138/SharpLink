# OneWay shape investigation (issue #729)

This is an investigation harness, not a production change or an acceptance gate.
All arms start at exact dev commit `553854f56c33a6e02ef225c24f558e5e92037aa1`:

- A: unchanged baseline.
- C: `eng/perf/oneway-shapes/internal-split.patch`, internal specialization only.
- B: `eng/perf/oneway-shapes/static-entries.patch`, generated static entry experiment, including C's specialization.

Patches apply independently to A. Do not apply B on top of C. The orchestrator
clones isolated checkouts without hardlinks, copies the identical standalone
full-RPC harness into each, checks source hashes, and records patches, hashes,
SDK/OS/CPU details, complete command lines, build times, logs, and results.
It does not modify the checked-out production sources, push, merge, or publish.

## Run

Requires the pinned .NET SDK, Python 3, and (for NativeAOT) the platform's usual
NativeAOT compiler prerequisites. The included workflow uses Ubuntu 24.04.

```sh
python3 eng/perf/oneway-shapes/run.py \
  --work-root /tmp/oneway-arms-new \
  --output /tmp/oneway-results-new \
  --rounds 1 --seconds 1 --warmup 0.5 --features --aot --aot-rounds 1
```

Both destinations must be new, so evidence cannot be silently overwritten.
`DOTNET` can name a dotnet executable. `--offline` is explicitly a local smoke
option using cached packages and disabling NuGet audit; it is recorded in
provenance and never used by CI. Normal builds retain repository warning and
audit policies. A short local smoke can use `--rounds 1 --seconds 0.1 --warmup
0.1`, but cannot establish CPU or performance conclusions.

For an individual already-built scenario:

```sh
timeout 90s dotnet test/SharpLink.OneWayEvidence/bin/Release/net10.0/SharpLink.OneWayEvidence.dll \
  tcp 2 8 2 1 4 none /tmp/tcp-two-streams.json
```

Arguments: transport (`tcp`/`shm`), input stream count, maximum offered batch,
measurement seconds, warmup seconds, items per stream, features, JSON output.
The orchestrator enforces an external timeout and retains timed-out samples.

## Workload and measurement boundary

- Real generated proxy, serialization, TCP loopback or SharedMemory transport,
  server dispatch, and generated stream writers/readers run in one process.
- Tiny payload: one 64-bit call ID and either zero, one, or two input streams.
  Each stream contains four integers whose values depend on call ID, stream,
  and position. The server validates every value, exact count, and unique call.
- `batch=1` is the idle/wake completion-observed case. `batch=8` submits up to
  eight calls with one synchronization after the entire batch. It is bounded
  offered load, not eight simultaneous client producer threads. OneWay enqueue
  completions are awaited but never counted as server completions.
- The server signals an investigation-only reusable in-process completion
  barrier after its service body consumes and validates all inputs. The next
  batch cannot begin before all server calls in the current batch complete.
  This boundary is service-body completion, not completion of the subsequent
  dispatcher cleanup. No production telemetry or shared-path code is added.
- Completion-barrier atomics, continuation scheduling, stream iterator objects,
  server execution, validation, and transport overhead are included equally in
  all arms. Absolute numbers are full-process harness cost, not isolated client
  invocation latency. OneWay wire protocol does not gain a response message.
- Each fresh process warms up, completely drains, settles for 200 ms, runs GC,
  measures at least the requested duration, then drains/settles identically.
  Wall ns/call, process CPU ns/call, total process allocation bytes/call, and
  completed-call throughput all use validated server-completed calls.
- CPU includes client and server on all process threads. Use >=1 second samples
  for CPU analysis. Very short smoke samples are dominated by quantization and
  fixed measurement cost. Allocation is process-wide, not current-thread-only.
- All timed methods have a cancellation parameter. In the base case it is
  `CancellationToken.None`; these are not `[NonCancellable]` contracts.
- Extra empty, reference, and wide request methods exist only as layout/codegen
  fixtures and are not included in the timed throughput workload.

## Staged matrix

Initial screen is 0/1/2 streams x TCP/SharedMemory x batch 1/8. Each JIT scenario
uses one fresh-process round by default for correctness/regression sanity; use
`--rounds 5` for five independent rounds if a signal needs investigation. Seeded arm permutations and scenario
order are recorded in `schedule.json`; six rounds would cover all arm positions
exactly, while five rounds is near-balanced and one round cannot establish performance significance. Runs are serial on one runner.
JIT measurements use tiered compilation and ReadyToRun disabled in every arm.

NativeAOT repeats the same workload once by default for correctness and reports
image/section/symbol size and publish time. This one-round smoke does not supply
a confidence interval; use `--aot-rounds 5` for a paired NativeAOT performance
screen. Build time is elapsed publish time including restore/incremental compiler
work, not a claim of isolated compiler CPU; build order is A/C/B and can be
affected by package caches.

`--features` adds independently enabled deadline, live never-cancelled token,
pass-through client/server interceptors, fully sampled activities plus metrics,
single-endpoint topology, endpoint admission, and all-enabled cases for each
transport/stream count at batch 1. It is a staged sensitivity matrix, not a full
factorial. The timed methods carry parameterless `[Timeout]`: the base explicitly disables
the fallback, and `deadline` explicitly enables a two-minute fallback. Ordinary
OneWay methods without `[Timeout]` ignore the client-wide default. An untimed initial probe enables detailed client activity sampling, verifies the
selected lifetime source is `client_custom_timeout` only for deadline cases,
and removes that probe before warmup. Server cancellation tokens also include
shutdown, so their `CanBeCanceled` flag alone cannot prove deadline activation.
Warmup verifies enabled interceptor, activity, metric, and admission callbacks. Deadline uses a comfortably non-expiring two-minute budget and every
case must validate the same completed workload. Admission is compared against
`endpoint` (same topology), rather than claiming an isolated comparison to the
direct fixed-endpoint `none` case. Cancellation/deadline failure semantics remain
the responsibility of focused correctness tests, not this never-cancelled screen.

## Interpretation and retained evidence

`records.json` retains every planned measurement, including failed builds,
exceptions, nonzero exits, and timeouts; per-command status files and complete
stdout/stderr are never dropped. `summary.json` reports only valid paired ratios,
their min/max spread, each independent round, and approximate Student-t 95%
intervals on paired log ratios. One-round or noisy evidence is inconclusive,
not “no gain.” Investigate failed samples before comparing surviving samples.

Full-RPC gain is not required: a clear repeatable representative local invocation
gain with modest complexity can justify an internal optimization. This harness
provides correctness and regression sanity, and helps identify gains hidden by
transport/server cost. Compare B against C before accepting an expanded generated
ABI; compare both against A. A small noisy end-to-end difference alone is not
proof of a regression or improvement.

`images/` retains JIT and native executable images and debug symbols. `codegen/`
retains generated source, raw JIT disassembly (separate untimed processes), native
section output and symbol output. Whole-image `.text` and symbol sizes are
different quantities; do not call whole-image size one method's native size.
`provenance.json` records elapsed publish time, native executable bytes, all published file sizes, and
aggregate image bytes excluding `.pdb`/`.dbg` separately from debug-file sizes.

The workflow uses exact PR head, immutable action SHAs, read-only permissions,
no stored checkout credential, and uploads complete evidence even on failure.
