# Read-ownership GitHub retest

This evidence helper runs only on an isolated `perf/pr753-read-ownership-*` branch. It does not modify the production PR. Pin all three full revisions in `read-ownership-revisions.json` before publishing. The runner rejects pending/moving revisions, unequal build or load-test sources, a baseline that is not an ancestor, and production differences beyond `TransportConnection.cs`.

Arms:

- `baseline`: exact dev revision after #790.
- `candidate`: exact independently reviewed #753 head, including current dev.
- `prior-harmonized`: candidate checkout with only the `ReadOwnershipPipeReader` class replaced by its exact source at prior head `11b6e78`. This synthetic control isolates the new fixes from unrelated dev changes. It is neither the old full commit nor a deployable alternative.

Each of six rounds uses one of all six arm permutations. Every arm runs in every position twice; every pair precedes/follows the other three times. All builds finish before measurement. Each observation runs in a fresh process, with two seconds of workload warmup and six seconds of measurement. No slow/error samples are selectively retried or excluded. A failed batch retains its partial artifacts but cannot produce a valid comparison.

The unchanged repository load tools run local TCP Add at concurrency 1/32/128, SharedMemory Add at 1/32, and TCP duplex streams of 256 elements at concurrency 1. They use Balanced profile, one fixed connection, a fixed 64 MiB send queue, and formal latency recording. SharedMemory bypasses the affected reader and is a noise control. The stream cell is a targeted check, not exhaustive transport/stream coverage.

An added minimal executable uses the production runtime assembly through its existing benchmark friend access. A deterministic zero-allocation fake inner reader measures sync, true suspension, and an awaiting consumer; both raw and wrapped cells are recorded. Tiered compilation is disabled only for these micro processes. Its small fixed construction-allocation probe is reported separately from steady-state read allocation. Allocation counters cannot prove reference-lifetime correctness; result/exception release and execution/synchronization context semantics require the independent regression suite.

Artifacts include immutable revisions, explicit synthetic source patch, copied runtime DLL identity, build logs, exact command and order, machine/.NET data, per-observation JSON and logs, descriptive means/medians/stdev/ranges, and within-round paired differences. Load JSON contains stage summaries, not every individual latency sample. CPU/allocation/GC are combined client/server/harness process metrics and include the bounded drain of measured operations. GC/1M RPC normalizes for differing completed work. Per-stage p99 values are never pooled into an overall p99.

The zero-failure gate applies only to measured stages. Existing load tools discard warmup outcomes, so this report does not assert zero warmup failures.

The six-round series is descriptive evidence on one hosted runner. It cannot reproduce or be directly compared with historical Apple M4 measurements, establish statistical equivalence, or independently justify a production Go decision. The previous local historical raw files are not inputs to this run.

Run harness self-tests with `python3 eng/perf/test-read-ownership-harness.py`. The GitHub workflow runs the same tests and retains raw artifacts even if setup, validation, or a sample fails.
