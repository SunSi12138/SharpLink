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
equivalence. These are successful scalar-stream paths: bounded two-core loopback
TCP and short-circuit allocation/JIT diagnostics. They do not cover all RPC
shapes, AOT, or the final 3.0 release matrix.

For standalone usage see [TCP](tcp/README.md) and [scalar](scalar/README.md).
