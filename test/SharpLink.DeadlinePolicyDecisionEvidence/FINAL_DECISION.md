# Issue #280 final evidence decision

Run: `32569453616`  
Artifact: `issue-280-deadline-policy-evidence`  
Runtime: .NET `10.0.11`, 4 logical CPUs  
Production `src/`: unchanged by the evidence branch

## Decision

- **Mode A / default request timeout enabled:** per-call runtime timer **No-Go**.
- **Mode B / default request timeout disabled:** policy-wide routing to per-call runtime timers **No-Go**.
- Keep the current shared scheduler architecture for now; do **not** add a Builder-policy scheduler split from `DisableRequestTimeout()` alone.
- The evidence does confirm a useful sparse crossover: 1% explicit deadlines strongly favor the per-call candidate and 10% is modestly favorable, but 50%/100% explicit deadline density produces an unacceptable CPU/allocation cliff. Since disabled default timeout does not imply sparse explicit deadlines, the Builder policy is not sufficient to choose the scheduler safely.

## Primary evidence

| Scenario | per-call QPS delta | per-call CPU delta | allocation delta |
|---|---:|---:|---:|
| Mode A, 100% deadline, 0 expiry, single | +3.36% | -3.34% | +184.00 B/op |
| Mode A, 100% deadline, 0 expiry, 4 workers | **-11.26%** | **+15.55%** | **+184.00 B/op** |
| Mode A, 100% deadline, 1% expiry | +0.62% | **+27.27%** | **+184.01 B/op** |
| Mode B, 0% deadline, single | +0.12% | -0.21% | ~0 B/op |
| Mode B, 0% deadline, 4 workers | -2.46% | +2.50% | ~0 B/op |
| Mode B, 1% explicit deadline | **+16.87%** | **-29.32%** | +1.84 B/op |
| Mode B, 10% explicit deadline | +2.22% | -3.91% | +18.40 B/op |
| Mode B, 50% explicit deadline | +0.24% | **+25.48%** | **+92.00 B/op** |
| Mode B, 100% explicit deadline | +0.67% | **+26.04%** | **+184.01 B/op** |

Zero-deadline guardrail: 100,000 operations, **0 allocated bytes**, **0 per-call runtime timer creates**, **0 callbacks**.

Deterministic lifecycle/correctness suite: **16/16 passed**.

`TimeProvider.System.CreateTimer` captured `ExecutionContext` in the probe. A future timer experiment should suppress flow where production semantics allow it, but the decision above does not depend on the capture cost: the dense controls already show substantial timer-queue/create-dispose CPU and allocation costs.

## Capacity / stress evidence

At 10% explicit deadline density, the 1M configured-capacity boundary favored per-call timers (+6.55% QPS, -10.97% CPU), confirming that runtime timers decouple expiry cost from configured pending capacity. However dense-expiry stress moved in the opposite direction: 50% and 100% clustered expiry increased per-call CPU by about 43% and 32%, with materially higher callback rates and allocations.

This is useful evidence, but not sufficient to justify a scheduler split keyed only by `DisableRequestTimeout()`.

## Separate correctness finding in the current shared scheduler

The evidence also exposed a current shared-earliest-timer final-arm race. In `ScanExpiredDeadlines()` the scanner releases `_deadlineScanRunning`, reads `_approximateEarliestDeadline`, then calls `ArmDeadlineTimer(next)`. A concurrent registration can install and arm an earlier deadline after the scanner reads `next` but before the scanner arms its stale later `next`; the scanner then overwrites the earlier timer. `_approximateEarliestDeadline` remains the earlier value, so subsequent registrations do not repair the timer, and the deadline can wait until the stale later timer fires.

The symptom was reproducible in the evidence artifact: some 10 ms expiry calls were delayed until approximately the 30 s normal deadline, with histogram lateness saturating its 1 s cap. This correctness issue is independent of the per-call architecture decision and should be fixed separately with an arm-and-validate/reconciliation protocol plus a regression test.
