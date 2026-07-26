# 0.8.20 regression-test research

## Target inventory and evidence candidates

- RPC, TLS, and shared-memory handshake timeouts accept durations beyond the portable native timer range, then construct `CancellationTokenSource(timeout)` only after transport ownership and handshake work begin.
- A disconnected `WaitForReady` call with a far-future absolute deadline passes the full remaining duration to `Task.WaitAsync`, which fails immediately outside the native timer range instead of remaining cancellable.
- A full pending-request table uses the same unbounded absolute-deadline duration for `SemaphoreSlim.WaitAsync`, so a valid far-future call faults before a slot or caller cancellation can win.
- Server graceful Stop saturates its monotonic deadline but passes the resulting multi-year duration to `Task.WaitAsync`; `StopAsync(TimeSpan.MaxValue)` can turn a requested long drain into an immediate fault/forced stop.
- Generated DTO strings use the replacement-fallback `Encoding.UTF8` decoder. Non-canonical invalid UTF-8 is silently changed to U+FFFD instead of being rejected as untrusted `DataLoss`.

## Acceptance checklist

- Every handshake timeout is rejected during configuration when it exceeds the shared portable timer maximum; no connection owner is acquired first.
- Far-future Client readiness and pending-slot deadlines stay pending and respond to caller cancellation.
- Server graceful waits slice long monotonic durations without changing normal timeout behavior.
- Generated DTO string decoding is strict for contiguous and segmented payloads and maps invalid UTF-8 to `SharpLinkErrorCode.DataLoss`.

## Audit guardrails

This batch closes timer-range failures only where public configuration or call deadlines currently trigger runtime faults, plus one independent wire-integrity defect. Cosmetic async cleanup and unrealistic capacity-only proposals are excluded without allocation or failure evidence.

## Regression and performance evidence

The complete pre-fix Unit run contained 441 tests: all 436 existing tests passed and exactly the five new probes failed. The deadline probes observed immediate `ArgumentOutOfRangeException`; the Server wait completed or faulted before its owner; timeout validation accepted every over-range value; and both contiguous and segmented invalid UTF-8 were decoded without `DataLoss`.

After the focused fixes, Generator 83/83, Unit 441/441, and Integration 230/230 pass. Assertion review covers cancellation rather than only task state, every public timeout family, segmented and contiguous wire paths, and the exact `DataLoss` code. Pseudo-mutations (removing the upper-bound checks, restoring either native unbounded wait, returning early from the Server wait, or using replacement decoding) each make its corresponding probe fail.

Against 0.8.19 commit `2d7cd95`, valid contiguous string decoding retained 64 B/op and overlapping latency. Segmented decoding retained 112 B/op and added about 3.5 ns (roughly 3%) for the output replacement-marker scan. Always-strict decoding and pre-validating every byte measured about 8% and 10% slower and were rejected; the final path performs strict byte validation only for strings whose normal decoding actually contains U+FFFD.
