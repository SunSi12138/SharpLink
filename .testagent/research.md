# 0.8.44 regression-test research

## Baseline and audit boundary

- Exact baseline: local 0.8.43 commit `9789fbedd5af5e4b2b21be84684150047f26c6e2`.
- This round starts with parallel shutdown aggregation, then continues timing, async ownership,
  pooled memory, dynamic modules, and protocol length boundaries not promoted in 0.8.43.
- A multi-cluster cancellation-callback hypothesis was rejected: the initiating failure remained
  intact, every child stopped, and the coordinator reached Faulted. Its probe was removed.

## Finding 1 — shutdown joins inspect only the exception selected by `await` (P2 lifecycle)

This is one root cause with three independently reproduced manifestations. They are counted once,
not once per component or call site.

### Server session cleanup

- `SharpLinkServer.DisposeAllSessionsAsync` awaited a `Task.WhenAll` and filtered only the single
  exception rethrown by `await`. When that exception was an expected `IOException`, the catch
  swallowed the entire aggregate even if another session had failed with an internal exception.
- The deterministic witness injects one `InvalidOperationException` transport failure and enough
  synchronous `IOException` closures to put an expected close first in the shutdown snapshot.
  Baseline disposed every transport but returned success, losing the unexpected failure.
- Pre-fix evidence: `artifacts/0.8.44-prefx-server-mixed-session-cleanup.log`.
- Fix: after `WhenAll` fails, flatten every child task exception, discard only known terminal
  close types (including structured `ConnectionClosed`), and preserve one or aggregate multiple
  unexpected failures with their original exception identities.
- Post-fix evidence: `artifacts/0.8.44-postfix-server-mixed-session-cleanup.log` (1/1 pass).

### Client background-worker cleanup

- `SharpLinkClient.IgnoreExpectedStopExceptionAsync` caught every exception once the shared
  shutdown token had been cancelled. Because `StopCoreAsync` cancels that token before joining
  `_reconnectTask` and `_expansionTask`, an already-faulted background worker carrying an unrelated
  internal exception was silently treated as normal cancellation.
- The witness installs a completed `InvalidOperationException` reconnect task, calls the public
  `StopAsync`, and verifies both that cleanup reaches Stopped and that the failure is retained.
- Pre-fix evidence: `artifacts/0.8.44-prefx-client-reconnect-cleanup.log`.
- Fix: flatten aggregate connect failures and suppress only expected shutdown/transport terminal
  types for background reconnect/expansion work; preserve one unexpected failure with its original
  stack or aggregate multiple failures. The caller-owned initial `ConnectAsync` task remains
  suppressed during Stop to avoid reporting an already-observed failure twice.
- Post-fix evidence: `artifacts/0.8.44-postfix-client-reconnect-cleanup.log` and
  `artifacts/0.8.44-postfix-client-connect-observed-control.log` (both 1/1 pass).

### Nested Server/Client background joins

- Server and Client background joins filtered only the exception rethrown by `await Task.WhenAll`.
  A tracked task that internally aggregated an expected transport close with an unexpected internal
  failure could therefore be accepted as normal shutdown, even though its full `Task.Exception`
  still contained the unexpected cause.
- The deterministic Server witness tracks one nested `WhenAll` whose first failure is an expected
  `IOException` and whose sibling is `InvalidOperationException`. The baseline join returns success.
- Pre-fix evidence: `artifacts/0.8.44-prefx-framework-join-mixed-failure.log`.
- Fix: Server framework/session joins and Client background joins flatten every tracked task's
  complete exception tree, suppress only explicit shutdown terminal types, and rethrow one or
  aggregate multiple unexpected failures.
- Post-fix evidence: `artifacts/0.8.44-postfix-framework-join-mixed-failure.log` and
  `artifacts/0.8.44-postfix-client-background-join.log` (both 1/1 pass).

### Static endpoint-cluster worker join

- The first convergence pass found one remaining manifestation in
  `StaticClusterRuntime.WaitForWorkersAsync`: the join added only the exception selected by
  `await Task.WhenAll`, so an expected transport failure inside a nested worker hid its unexpected
  sibling. This is the same shutdown-join root cause and does not increase the finding count.
- Pre-fix evidence: `artifacts/0.8.44-prefx-static-cluster-worker-join.log`.
- Fix: after the aggregate join fails, inspect every worker's complete exception tree and retain
  non-shutdown cancellation exactly as before.
- Post-fix evidence: `artifacts/0.8.44-postfix-static-cluster-worker-join.log` (1/1 pass).

## Finding 2 — a failed error-response enqueue leaks Server call admission (P1 lifecycle)

- The synchronous Server dispatch branches released their call state only after sending an error
  response. If a handler failed synchronously and the bounded send queue then rejected that error
  frame, `ReleaseDispatchResources` was skipped, leaving both global and per-connection active-call
  counters elevated. Graceful stop could consequently wait for a call that had already ended.
- The witness binds a session to a one-byte send budget, holds one frame in a blocking output flush,
  invokes a synchronously failing service, and deterministically forces the error response enqueue
  to return `ResourceExhausted`. The baseline leaves both admission counters at one.
- Pre-fix evidence: `artifacts/0.8.44-prefx-server-error-enqueue-release.log`.
- Fix: place terminal stream/cancellation/error handling inside `try/finally` cleanup in both
  synchronous response shapes, and mark a manually returned response writer as transferred before
  a module-drain send can fail.
- Post-fix evidence: `artifacts/0.8.44-postfix-server-error-enqueue-release.log` (1/1 pass; both
  active-call counters return to zero while the original queue failure remains observable).

## Finding 3 — rejected stream terminal frames retain flow-control slots (P2 availability)

- `SendStreamCompleteAsync` and `SendStreamErrorAsync` marked their send stream complete only after
  the terminal frame entered the session queue. A bounded-queue rejection therefore left the
  `StreamFlowController` state open forever even though the producer had ended. Repetition could
  exhaust `MaxConcurrentStreamsPerConnection` on an otherwise healthy connection.
- The witness opens the sole permitted flow-controlled stream, repays all byte credit, blocks one
  frame in the send pump, and forces `StreamComplete` to fail with `ResourceExhausted`. On the
  baseline, a second stream is rejected because the first slot remains retained.
- Pre-fix evidence: `artifacts/0.8.44-prefx-stream-complete-slot.log`.
- Fix: terminal-frame enqueue and flow-state completion now share a `try/finally`, for both success
  and error terminal frames. The original send exception still propagates.
- Post-fix evidence: `artifacts/0.8.44-postfix-stream-complete-slot.log` (1/1 pass; the next stream
  acquires the single configured slot after the terminal enqueue rejection).

## Rejected observations

- DNS refresh jitter near `TimeSpan.MaxValue` does not wrap to the minimum interval on the target
  runtime: the floating-point-to-`long` conversion saturates. The probe passed unchanged and was
  removed, so this is not counted.
- Shared-memory writer spill completion reads `SemaphoreSlim.CurrentCount`, but `_completed` is
  published before that read. A later flush observes completion before touching spill state, while
  an earlier flush already owns the zero-count gate. The apparent TOCTOU does not create concurrent
  spill access and is not counted.
