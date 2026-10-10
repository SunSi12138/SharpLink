# Focused read-ownership RPC validation

This isolated evidence helper compares three exact commits on one GitHub-hosted runner:

- `baseline`: frozen current dev `130fae63df31af2401aa07e468e73114f5dba52a`.
- `safe`: unchanged dev-synced reference `a4f2d1b56877a8663cbedd12b15cdbf7d9b8fcd2`.
- `normalized`: independently reviewed notification-cell simplification candidate, pinned to its full immutable SHA before publication.

The runner rejects pending/moving revisions, incorrect ancestry, production differences beyond `TransportConnection.cs`, and unequal load-test/build inputs. No production code is transplanted. Each executed program's copied Runtime DLL hash must match its built Runtime hash. All builds finish before any measurements.

## Bounded scenarios and order

Only three existing load-tool scenarios run:

1. TCP Add, concurrency 1: primary workload with a previous noisy negative QPS estimate.
2. TCP Add, concurrency 32: batching/concurrency control.
3. SharedMemory Add, concurrency 1: bypass/noise control; it does not use ReadOwnershipPipeReader.

Each scenario executes all six permutations of the three arms. Every arm occupies every position twice; every pair runs in each direction three times. This yields 54 raw JSON observations and 54 process logs. Every observation is a fresh process. Warmup remains 2 seconds and measurement remains 6 seconds, using the unchanged repository workload, one fixed connection, Balanced profile, 64 MiB send queue and formal recording. The existing one-second separation between observations is retained. No failed or slow observation is retried selectively or discarded.

These short stages remain noisy. The focused result can inform the previous noisy negative TCP c1 estimate, but cannot prove broad equivalence, refresh all transports, or establish full production acceptance. The previously tested dispatch main suite runs separately as described below. The old six-cell micro and backlog do not run. No earlier run supplies points for this comparison.

## Evidence and gates

The existing metric methods remain unchanged: QPS is measured successes divided by measurement duration; CPU and allocation are process-wide totals divided by completed operations; GC counts and GC per million RPC are retained; p99 is a per-stage latency quantile. CPU/allocation/GC cover combined local client/server/harness work, including bounded drain. The archive holds stage summaries, not individual latency samples; medians do not pool p99 populations.

The strict validator checks exact arm identity, all expected observations and balanced positions, unique filenames, raw hashes, source/runtime environment, operation/transport/concurrency/duration/profile configuration, complete operation/sample counts, zero measured failures, finite nonnegative evidence, QPS arithmetic, drain bounds and percentile ordering. Existing tools discard warmup outcomes, so no zero-warmup-failure claim is made.

Artifacts retain source/build identities, clean-source diffs, commands, order, runtime/machine details, every raw observation/log, means/medians/sample standard deviations/ranges and paired differences. These are descriptive statistics, not confidence intervals or a non-inferiority test. Hosted absolute timings are not compared with historical M4 or other runner instances. No uncollected read-suspension/queue-hop counter is inferred from throughput.

The workflow also copies the repository Fast and allocation-gate jobs exactly, changing only Checkout.ref to the immutable notification-cell candidate. These run independently of the timing runner, with unchanged assertions and read-only contents permission. A failed correctness gate cannot be hidden by a successful timing job. The separate correctness-only workflow's path is not changed by this helper update, avoiding an unintended extra run.

Run the local acceptance/negative/known-answer tests with `python3 eng/perf/test-read-ownership-harness.py`. Formal measurement is restricted to GitHub-hosted runners; preparation failures retain status and traceback. Validation evidence does not authorize PR promotion or merge.

## Reused all-thread allocation probe

The Program.cs, project and README under `eng/perf/read-ownership-dispatch` are byte-identical to the independently validated probe from helper e962d9a7efa58c255a9715152c71cb3dda317e5b (original safe-dispatch run 37896372085). No new probe logic or case filtering is introduced. Its existing `main` CLI emits all 20 raw/wrapped ordinary and special-context cells plus two fixed-reader construction cells; backlog is not invoked. Each arm executes that whole suite in six balanced orders: 18 extra fresh-process observations, 72 observations total including the unchanged 54 RPC stages. All nine builds complete before either type of measurement.

Each probe process uses 10,000 measured operations per cell after 5,000 warmup operations, and explicitly sets tiered compilation to 0. RPC processes retain default runtime settings; their performance cannot be inferred from probe timings. Process-wide precise allocated bytes include worker threads. Identical raw/wrapped consumer, context/scheduler, payload and handshake controls are drained and checked, with the existing sampled ThreadPool-quiescence fence. The ordinary consumer/framework may allocate in both controls. Incremental wrapper allocation is wrapped minus raw within a cell; every signed point is retained, including negative noise, and percentages of incremental differences are suppressed. No raw latency subtraction is performed. Special-context costs are reported separately and cannot be described as ordinary zero-allocation overhead. Construction measures fixed bytes per reader, not retained graph size.

The source/probe/project hashes, built probe DLL and its copied Runtime DLL are recorded. All callback, context, completion, allocation-arithmetic, finite-metric and construction gates are reused unchanged from the tested probe validator at e919a6ae946c77156726701c55efa05e61913879. Existing correctness and allocation CI assertions remain unchanged and separate from performance acceptance.

## Artifact transfer

Every raw JSON/log, build/provenance record and machine harness source is retained. The previously tested lossless tar/gzip packaging method is reused with 15 MiB pieces, leaving space below a 16 MiB per-transfer cap for ZIP metadata. Each piece and the reconstructed archive has a SHA256; an index binds every original filename/size/hash. The complete ordinary artifact is also uploaded when its uncompressed input is at most 15 MiB. If more than 32 parts are required, transfer is incomplete, unuploaded parts remain only on the runner, and validation fails. Any size/packaging failure is recorded; it never triggers selective timing retries.
