# PendingRequestTable performance and recovery matrix

This document defines the reproducible evidence matrix for issue #571. The matrix is deliberately implemented in `test/SharpLink.Benchmarks`; it does not add benchmark-only branches or counters to the production pending-request hot path.

## Commands

```bash
# Fast deterministic CI coverage. This is what pending-validation.yml runs on pull requests.
bash eng/run-pending-request-matrix.sh ci artifacts/perf/pending-request-matrix-ci

# P0 formal matrix.
bash eng/run-pending-request-matrix.sh p0 artifacts/perf/pending-request-matrix-p0

# P0 plus P1 stress/extensions, including protocol-high sparse capacity,
# 128 producers, 99%/25%-long mix, feature-heavy profile, and 20 recovery cycles.
bash eng/run-pending-request-matrix.sh p1 artifacts/perf/pending-request-matrix-p1
```

Each command records a single JSON report plus the console log. The report includes the exact GitHub SHA when running in Actions, runtime/OS/architecture, processor count, GC mode, and `Stopwatch.Frequency`. GitHub Actions additionally uploads `dotnet --info`, `uname -a`, and `lscpu` with the report.

Timing values from shared hosted runners are evidence only; they are not CI thresholds. Compare performance only on controlled hardware, preferably by alternating `dev` and the candidate revision on the same machine and retaining raw reports from every run.

## Matrix coverage

### High occupancy and saturation

The runner holds a real `PendingRequestTable` at controlled occupancy and rotates registrations/completions while keeping long-lived entries resident. It reports requested and actual occupancy, average/P95/P99/max sampled occupancy, QPS, CPU ns/op, allocation/op, P50/P95/P99/P99.9 operation latency, request-ID advances, extra probe attempts caused by occupied slots, rejection counts, and per-producer progress.

P0 covers capacities 64, 1K, 16K and 65K with 50/75/90/95/99% occupancy and 1/8/32 producers. Full-capacity cells verify fail-fast `ResourceExhausted` behavior and immediate full-capacity reuse. P1 adds 128-producer 99% cells.

### Sparse deadlines

The deterministic scheduler path uses a controllable `TimeProvider` and invokes the production deadline scan directly. It records scan cost, capacity inspected, active/deadline counts, and single/staggered/clustered expiration patterns. It explicitly verifies that a deadline never completes before its monotonic boundary and that expiration succeeds at the boundary. A separate real-timer cell measures P50/P95/P99/max deadline lateness while only eight calls are active in a 65K table.

P1 additionally exercises the protocol hard maximum capacity (1,048,576) with sparse active/deadline state without materializing one million pending operations.

### Long/short mixed lifetimes

Long calls are created first and held deterministically while producer workers rotate short calls at steady occupancy. Long calls are never modeled with random sleeps. Terminal modes are response, user cancellation, deterministic deadline, and connection-close cleanup. The report includes short-call latency/QPS, actual occupancy, long-call count/share, producer progress, and terminal duration. P1 adds the 99%-occupied, 25%-long, 128-producer case.

### Production-shaped profiles

The matrix includes real loopback TCP RPC profiles using the generated benchmark contract and validates every response:

- `plain-control`: TCP, no TLS/compression/metrics/retry/breaker/admission.
- `typical-production`: TLS, Brotli compression, normal SharpLink metrics, retry, circuit breaker, admission control, and 0/256/4096-byte payload cells.
- `feature-heavy` (P1): the typical profile plus full client/server tracing.

Each profile reports QPS, process CPU/call, allocation/call, Gen0/1/2 counts, failures, retries, `ResourceExhausted`, pending high-water/after state, and P50/P95/P99/P99.9 latency.

### Overload and recovery

Each recovery cycle is operation-count/barrier driven:

1. low-occupancy sequential baseline probe;
2. ramp to full capacity;
3. verify fail-fast overload and place controlled async renters into the waiter path;
4. release exactly enough capacity, require every waiter to make progress, then simulate disconnect cleanup;
5. prove pending/waiter counts return to zero, refill the entire table to prove all capacity is reusable, drain it again, and run a post-recovery baseline probe.

CI runs three cycles, P0 seven, and P1 twenty. Full-GC heap samples are retained per cycle; the deterministic gate rejects gross retained-state growth (more than 64 MiB above the minimum observed full-GC heap) while leaving normal performance comparison to formal evidence runs.

## Correctness gates

Any violation throws and fails the matrix before the report is marked complete. The gates cover:

- active occupancy never exceeds configured capacity;
- every registered request has one terminal completion;
- owner/capacity accounting never underflows and returns to zero;
- stale responses cannot match a newer request lifecycle;
- deadlines never complete early;
- full-table waiters are released without lost wakeups;
- disposal wakes waiters and strands no pending call;
- disconnect cleanup strands no pending call;
- after every recovery cycle there are zero pending calls and zero waiters;
- the complete configured capacity can be reused after recovery;
- real production-profile RPCs return correct results with zero failures and zero pending requests after the measurement window.

The permanent pull-request gate is the `issue-571-pending-request-matrix` job in `.github/workflows/pending-validation.yml`. It runs the `ci` tier and uploads the exact evidence used for the gate.
