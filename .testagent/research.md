# 0.8.18 regression-test research

## Target inventory and evidence candidates

- `SharpLinkClientHostedService` and its multi-cluster sibling exchange away their Client before awaiting token-bound Stop. If that wait is cancelled, the memoized stop task prevents later `DisposeAsync` from recovering ownership, so the Host leaks the transferred Client.
- Client and Server dynamic-module unregister use `Task.Delay(gracefulTimeout)` after marking the module Draining. A positive timeout beyond the portable timer range throws and leaves the module in a non-running state instead of continuing the requested drain.
- `SendPump.ToStopwatchTicks` converts huge `RpcSessionFlushOptions.MaxLatency` without saturation. Overflow turns the documented positive latency into an immediate deadline or faults the pump when the timer is armed.
- `SharpLinkFlowControlOptions.MaxConcurrentCallsPerConnection` accepts `int.MaxValue`; the first Server deadline scan rents a snapshot sized to that configured maximum, creating an avoidable multi-gigabyte allocation request.
- `StreamManager.CompleteAll` invokes dispatcher completion inline and stops at the first exception. Sibling streams remain attached, counters remain elevated, and `RpcSession` can skip its later transport cleanup.

## Acceptance checklist

- Hosted Stop always disposes the transferred Client after success, cancellation, or failure and preserves both primary and cleanup failures.
- Dynamic-module drain waits slice timer-range-exceeding durations without changing short timeout behavior.
- Send-pump deadline conversion saturates and timer waits are sliced, while normal configured batching remains unchanged.
- Server call concurrency has a documented hard maximum enforced before the deadline scheduler can allocate its snapshot.
- Stream completion drains every dispatcher before surfacing errors, and Session terminal paths cannot be interrupted by user dispatcher cleanup.

## Performance scan execution checklist

- Critical string recipes: `IndexOf` 0, `Substring` 7, literal `StartsWith`/`EndsWith` 1, literal `Contains` 3. All hits are generator/build-time paths after context review.
- Async recipes: `async void` 0; five `.Result`/`.Wait` hits are guarded completed `ValueTask` reads or the intentional synchronous PipeWriter completion convergence path.
- Memory recipes: parameter arrays 2, parameterless `ToLower`/`ToUpper` 0, three-deep `Replace` chains 0, char LINQ 0, stackalloc 59 with no stackalloc-in-loop finding.
- Collection recipes: static mutable dictionaries 0, static frozen dictionary declarations 0, `new List` 45, `new Dictionary` 40, LINQ-chain hits 140, and `CurrentCulture` comparer hits 0. Runtime hits are configuration, topology publication, shutdown, or contended-path snapshots rather than steady-state hot paths.
- I/O/serialization recipes: per-call `HttpClient` 0, per-call `JsonSerializerOptions` 0, Regex signals 0.
- Structural inverse: 19 unsealed declaration lines versus 238 sealed declaration lines; the unsealed set consists of required inheritance surfaces, exception/builder extension points, or partial generator declarations.

## Audit guardrails

This batch addresses lost resource ownership, timer-range correctness, bounded Server memory, and terminal stream cleanup. Generator substring cleanup, public sealing changes, and cold-path LINQ rewrites are excluded because they lack measured runtime value or would trade compatibility for cosmetic consistency.

## Regression and performance evidence

- The complete pre-fix Unit run had exactly five focused failures among 432 tests; the post-fix run passes 432/432.
- Each probe independently fails if its owner-disposal, delay slicing, stopwatch saturation/timer slicing, concurrency limit, all-entry drain, or Session suppression guard is removed.
- Four counterbalanced nine-sample A/B pairs with tiered compilation disabled retained identical allocations on buffer-pool rent/return (0 B), pending completion (48 B), flow-credit round trips (0 B), empty Session disposal (17,904 B), Runtime Context lifecycle (4,048.13 B), and Server lifecycle (13,224.81 B). Timing ranges overlap without a stable regression.
- Completing one request with two streams changes from 1,280 B to 1,312 B because the terminal path snapshots entries before invoking user callbacks outside the lock. Median pairs remain in the roughly 0.25 microsecond band with process-order noise; the fixed 32 B cost occurs only once per terminal two-stream drain and prevents one callback from stranding all later owners.
