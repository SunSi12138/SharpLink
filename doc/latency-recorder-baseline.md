# Latency recorder baseline (schema 2)

This baseline replaces shared per-request latency histograms for formal load-test
evidence. Reports using schema 1 or another recorder version are not directly
comparable with schema 2 reports. `PerformanceReportCompatibility` fails fast
when either semantic version differs.

## Recorder contract

- `formal`: exact, bounded worker-local raw `Stopwatch` ticks; no realtime
  reporter; `formalComparable=true`.
- `diagnostic`: legacy aggregate and realtime percentiles;
  `formalComparable=false`.
- `off`: no latency buffer and no per-operation timestamp; percentile fields are
  omitted; `formalComparable=false`.
- `validation-dual`: exact and legacy recorders are compared within the stated
  1 microsecond or 0.5% tolerance; it is not formal evidence.

All sample buffers are allocated before the synchronized start gate. Capacity
exhaustion or drain timeout fails the run; samples are never clamped or silently
dropped. Throughput uses only `measurementDuration`, while in-flight operations
complete during the separately reported bounded `drainDuration`.

The default formal hard bound is 30,000,000 samples for both runners. Worker
recorders own disjoint slices of one preallocated backing array, so post-drain
compaction and sorting do not allocate a second maximum-sized sample buffer.

Schema 2 records `sourceCommit`, `recorderMode`, `recorderVersion`,
`stopwatchFrequency`, `warmupDuration`, `measurementDuration`, `drainDuration`,
`workerCount`, `sampleCount`, `maximumSampleCapacity`, and `formalComparable`.
Recorder-interference runs additionally expose an opt-in tail observer. It uses
a dedicated client connection and raw-sample worker, starts at the same gate,
and runs identically beside recording-off and formal workloads. Its sample
count, failures, P99, and P99.9 are separate from workload latency fields, so
recording-off still omits unavailable workload percentiles.

## Current dev evidence

- Integration base: `5683c90ee501a5afa56043802309de7c0155b7ee`
- Host: Apple arm64, 10 logical CPUs
- OS/runtime: macOS 26.6, .NET SDK 10.0.102, runtime 10.0.2
- Macro protocol: local TCP Add, alternating formal/off order, five fresh
  processes per mode and concurrency, 2 second warmup, 3 second measurement,
  zero failures
- Raw evidence: isolated task checkout
  `artifacts/issue-122/latency-recorder-evidence.json` and
  `artifacts/issue-122/macro-postopt-*.json`

### Recorder interference microbenchmark

Each scenario records approximately one million precomputed latencies per
repetition. Threads and recorder storage are created before timing. Values are
five-run medians; all formal steady-state runs reported 0 B/record.

| Concurrency | Control ns/record | Legacy one | Legacy double | Formal worker-local |
|---:|---:|---:|---:|---:|
| 1 | 1.07 | 35.32 | 43.18 | 4.90 |
| 8 | 0.28 | 103.58 | 136.60 | 1.20 |
| 32 | 0.50 | 105.05 | 200.92 | 1.14 |
| 128 | 1.29 | 99.71 | 228.98 | 1.59 |
| 512 | 7.86 | 109.18 | 235.58 | 8.04 |

### Formal versus recording-off macro gate

Positive delta means the formal run was faster; it is retained as environmental
variance rather than claimed as a product improvement.

| Concurrency | Off median QPS | Formal median QPS | Delta | Gate |
|---:|---:|---:|---:|---:|
| 128 | 1,138,407 | 1,142,673 | +0.37% | pass |
| 512 | 1,248,675 | 1,279,889 | +2.50% | pass |

The baseline is host-specific. Run the full transport/profile/operation and
streaming matrix on the machine used for a performance decision; do not compare
these macOS numbers with historical Linux evidence by percentage.

```bash
SHARPLINK_COMMIT=<exact-sha> \
  ./eng/run-latency-recorder-baseline.sh \
  artifacts/latency-recorder-baseline/<fresh-name>
```

The script captures an environment fingerprint; validates the unit, load, and
stream-load suites; runs the five-repeat interference micro/macro gates; and
executes validation-dual accuracy controls. Its analyzer verifies
schema/commit/recorder compatibility, zero failures, complete drain, exact
formal sample counts, steady-state zero allocation, CPU/op deltas, the absolute
3% throughput threshold, and the independent observer's absolute 3% P99 and
P99.9 thresholds. A failed gate returns a non-zero exit code before the wider
matrix runs. The remaining matrix records TCP/shared-memory,
LowLatency/Balanced/Throughput, unary/echo, streaming, metrics/tracing controls,
and static/dynamic endpoint representatives.
