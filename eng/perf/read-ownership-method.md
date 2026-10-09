# Focused read-ownership RPC validation

This isolated evidence helper compares three exact commits on one GitHub-hosted runner:

- `baseline`: frozen dev `072a3a13fca0aa9186d2db44e38e5fc846969b07`, after #790 and before unrelated #752/#751.
- `safe`: safe-dispatch reference `ac903d63c541d972f4268bb404b3c09ce63776e4`, including the test-only queued-wait fixture initialization.
- `normalized`: independently reviewed normalization candidate, pinned to its full immutable SHA before publication.

The runner rejects pending/moving revisions, incorrect ancestry, production differences beyond `TransportConnection.cs`, and unequal load-test/build inputs. No production code is transplanted. Each executed program's copied Runtime DLL hash must match its built Runtime hash. All builds finish before any measurements.

## Bounded scenarios and order

Only three existing load-tool scenarios run:

1. TCP Add, concurrency 1: primary previously regressed workload.
2. TCP Add, concurrency 32: batching/concurrency control.
3. SharedMemory Add, concurrency 1: bypass/noise control; it does not use ReadOwnershipPipeReader.

Each scenario executes all six permutations of the three arms. Every arm occupies every position twice; every pair runs in each direction three times. This yields 54 raw JSON observations and 54 process logs. Every observation is a fresh process. Warmup remains 2 seconds and measurement remains 6 seconds, using the unchanged repository workload, one fixed connection, Balanced profile, 64 MiB send queue and formal recording. The existing one-second separation between observations is retained. No failed or slow observation is retried selectively or discarded.

These short stages remain noisy. The focused result can inform the previous TCP c1 regression question, but cannot prove broad equivalence, refresh all transports, or establish full production acceptance. Micro/dispatch/backlog sources are unchanged and are not built or executed in this batch. Their prior evidence is separate; no earlier run supplies points for this comparison.

## Evidence and gates

The existing metric methods remain unchanged: QPS is measured successes divided by measurement duration; CPU and allocation are process-wide totals divided by completed operations; GC counts and GC per million RPC are retained; p99 is a per-stage latency quantile. CPU/allocation/GC cover combined local client/server/harness work, including bounded drain. The archive holds stage summaries, not individual latency samples; medians do not pool p99 populations.

The strict validator checks exact arm identity, all expected observations and balanced positions, unique filenames, raw hashes, source/runtime environment, operation/transport/concurrency/duration/profile configuration, complete operation/sample counts, zero measured failures, finite nonnegative evidence, QPS arithmetic, drain bounds and percentile ordering. Existing tools discard warmup outcomes, so no zero-warmup-failure claim is made.

Artifacts retain source/build identities, clean-source diffs, commands, order, runtime/machine details, every raw observation/log, means/medians/sample standard deviations/ranges and paired differences. These are descriptive statistics, not confidence intervals or a non-inferiority test. Hosted absolute timings are not compared with historical M4 or other runner instances. No uncollected read-suspension/queue-hop counter is inferred from throughput.

The workflow also copies the repository Fast and allocation-gate jobs exactly, changing only Checkout.ref to the immutable normalized candidate. These run independently of the timing runner, with unchanged assertions and read-only contents permission. A failed correctness gate cannot be hidden by a successful timing job. The separate correctness-only workflow's path is not changed by this helper update, avoiding an unintended extra run.

Run the six local acceptance/negative/known-answer tests with `python3 eng/perf/test-read-ownership-harness.py`. Formal measurement is restricted to GitHub-hosted runners; preparation failures retain status and traceback. Validation evidence does not authorize PR promotion or merge.
