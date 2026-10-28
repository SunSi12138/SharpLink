# Frozen PR931 paired evidence

Dispatch `.github/workflows/performance-evidence.yml` on the reviewed **tool
branch**, with `suite=ownership-931` (`repetitions` applies only to the existing
smoke job). The measured product revisions are constants, independent of the
tool branch:

- Baseline: `cc7c458b559295e0fc61ec0062bb220faf880a76`.
- Candidate: `ac17c8d8ea0203edbe7b8cf19ddb95eaa24d4d2c`.
- Tool commit: the dispatched `github.sha`, also recorded in `experiment.json`.

The job uses one Ubuntu 24.04 runner, isolated SDK 10.0.102 and a separate
runtime-only installation containing exactly Microsoft.NETCore.App and
Microsoft.AspNetCore.App 10.0.13. Snapshot runtimeconfigs disable roll-forward;
every process records its actual version and framework paths for validation.
No system runtime is used for measurement.

## Build first; measure frozen binaries

Both clean source checkouts are built and all nine shipping packages are
fresh-packed and verified before either probe executes. TCP and scalar probes
are each built once against the baseline, then copied byte-identically to both
arms. The TCP generated module is built once with the baseline generator.
Unequal non-SharpLink DLLs or probe/module binaries fail preparation. Product
DLLs are the only permitted differences.

After snapshot validation and runtime pinning, the job stops its build servers.
`measurement-start.json` marks the no-more-build boundary. The same two CPUs
selected from `sched_getaffinity(0)` are used for every execution. Affinity does
not reserve CPUs; `/proc/stat` and load observations remain in each TCP sample.

- **TCP:** static/dynamic intercepted × 1/100 items, one worker, one connection
  (pool minimum/maximum 1), six rounds, 3 s warmup + 6 s measurement. AB/BA order
  alternates and scenarios rotate: 48 fresh processes.
- **Scalar:** static/dynamic × 0/1/100 items × no deadline/five-minute deadline.
  Five paired FullOpts processes per arm; separate default-tiered and PGO-off
  diagnostics. Native listings identify actual method/tier, not merely file size.

## Read the retained evidence

Download `physical-ownership-931-<tool-sha>-<attempt>`. Upload runs even after a
failure, preserving build/pack/binlogs, fresh packages, prepared binaries,
installation logs, source/binary identities, environment and assembly versions.

- `experiment.json`: three revisions, SDK, runtimes and allowed/selected CPUs.
- `tcp/runs/`: every sample JSON, log and before/after host observation.
- `tcp/summary-integrity.json`: successful sample count, failed-process statuses
  and incomplete pairs, including when no successful pair remains. Only exit-0
  processes with valid samples enter comparisons; a shutdown failure excludes
  its sample without deleting the raw evidence.
- `tcp/summary.json`: paired ratios, medians and ranges for throughput,
  P50/P99/P99.9, CPU/op and B/op; descriptive bootstrap intervals.
- `scalar/<arm>/<mode>/run-N/`: each allocation/layout JSON and log, plus native
  listing for the first repetition of each mode. No repeated output is overwritten.
- `scalar/allocations.json`, `summary.json`, `native.json`: all rows, paired
  FullOpts summaries and per-method native sizes/tiers.
- `complete.json`: emitted only after 48 TCP and 14 scalar process records pass
  runtime validation. Failures are not replaced with favorable subsets.

There is no new performance pass/fail threshold. Compare all paired samples and
host variation; zero allocation or a noisy null result does not prove performance
equivalence. These are successful scalar-stream paths: bounded two-logical-CPU loopback
TCP and short-circuit allocation/JIT diagnostics. They do not cover all RPC
shapes, AOT, or the final 3.0 release matrix.

For standalone usage see [TCP](tcp/README.md) and [scalar](scalar/README.md).

## Client-only diagnostic for PR931

After review and explicit dispatch approval, choose
`suite=ownership-931-client-ablation` on the reviewed tool branch;
`repetitions=1` is ignored by this suite. This reuses the source build,
fresh-package verification, runtime pinning and artifact upload above.
It tests the incremental effect of the Client change on the candidate runtime,
not the complete product change or the original default-PGO workload.

Both arms contain candidate `ac17c8d8ea0203edbe7b8cf19ddb95eaa24d4d2c`
Runtime, Server, Abstractions and all other DLLs. Only `SharpLink.Client.dll`
differs: baseline `cc7c458b559295e0fc61ec0062bb220faf880a76` versus candidate
`ac17c8d8ea0203edbe7b8cf19ddb95eaa24d4d2c`. The reverse combination is not used:
the new Client requires a Runtime helper absent from the old Runtime. A separate
probe preserves the original probe and its timed loop; it exports that loop's
existing ordered latency arrays after sampling ends.

The fixed plan is dynamic intercepted TCP, 100 items, one worker and one ready
connection, FullOpts (`DOTNET_TieredCompilation=0`), private .NET 10.0.13.
Every process uses the same two allowed logical CPUs. `cpu-topology.json`
records package/core IDs and thread siblings, explicitly distinguishing
same-core SMT siblings from distinct reported cores. Affinity and guest topology
do not establish dedicated physical CPUs.

Before timing, each arm runs one **non-timed**, fixed 256-operation TCP preflight.
It must prove the frozen DLL inventory, loaded assembly paths/source revisions,
interceptor and pooled dispatcher path, and positive-size FullOpts native code
for these four closed-generic `MoveNext` targets:

- `InterceptedAsyncEnumerable<int>.GetAsyncEnumerator`: common client wrapper.
- `SharpLinkModuleRpcChannel.InvokeServerStreamingDeferred<generated request, int>`:
  actual dynamic TCP request, rather than the scalar short-circuit request.
- `RpcSession.PumpGeneratedOutboundStreamAsync<int>`: outbound protocol pump.
- `ServerGeneratedBridge.PumpOutboundStreamAsync<int>`: server invocation bridge.

These names follow the executed generic types; generated state-machine ordinal
numbers are not fixed. Missing code, another tier, wrong assemblies or any
preflight process failure closes the gate before all timing. Native disassembly
is disabled for the measured processes.

After both preflights pass, six pairs run once in AB/BA/AB/BA/AB/BA order: **12
fresh processes**, each with 3 s warmup and 6 s measurement. The complete declared
schedule is attempted even after a measured process fails or times out; no
sample is retried, replaced or added. Any failure makes the job fail. Successful
partial pairs may be summarized, with the failure inventory retained.

The artifact is `physical-ownership-931-client-ablation-<tool-sha>-<attempt>`:

- `client-ablation-plan.json`: fixed scene, sequence and failure policy.
- `client-prepared/prepared-manifest.json`: original preparation and exact hybrid
  DLL identities and origins; only Client may differ between arms.
- `preflight-gate.json`, `preflight/`: gate result, real-TCP proof, native listings,
  logs and host/exit/timeout records.
- `client-tcp/runs/`: all process samples and host outcomes;
  `matrix-failures.json` records any schedule failure.
- `client-tcp/ticks/`: ordered per-request `Stopwatch` durations and frequency,
  with exactly one duration per completed operation. Export occurs after the
  CPU/allocation sampling window and before shutdown.
- `complete.json`: emitted only when both gates and all 12 sample/host/tick
  inventories and identities validate. It carries no performance acceptance
  threshold.

### Tooling checks without timing

```sh
python3 -m unittest discover -s eng/physical-ownership-probes -p 'test_*.py' -v
python3 -m unittest discover -s eng/physical-ownership-probes/tcp -p 'test_*.py' -v
actionlint .github/workflows/performance-evidence.yml
```

The isolated `tcp/ablation-probe/probe.csproj` can be built against previously
frozen libraries using `LibraryRoot`. Its `preflight` invocation takes the same
first six arguments as the original probe followed by `preflight`; use zero
warmup/measurement seconds. A local smoke on a different OS, runtime version or
CPU arrangement validates only compilation and path/native capture. The hosted
private-runtime identity, Linux affinity and complete gate must still pass on
the actual runner before its timing begins.
