# 0.8.34 regression-test research

## Bounded target inventory

- A clean 0.8.33 120-second shared-memory Chaos run at commit `35c8cd2` completed 419,817 calls and 11 restarts but captured two `NullReferenceException` failures from `SharedMemoryPipeReader.ReadAsync`. `CompleteAsync` waits only for an outstanding returned buffer, not `_readOperationPending`; completion can therefore dispose `_staging` in the interval between a read's field check and publication and can release the mapping before that operation exits.
- The same Chaos report was marked `Passed` with `UnexpectedFailures=0` even though `ClientErrors` contained both NREs. `ChaosLoggerFactory` captures only Error-or-higher events, but the final gate ignores its snapshot and restart `Clear()` calls can erase earlier evidence.
- `GetContractMethods` collapses inherited methods by CLR-like parameter signature. It does not compare `[Oneway]`, so a fire-and-forget route and a response-bearing route are selected by base-interface name ordering.
- The same collapse ignores `[Timeout]` and `[Idempotent]`, allowing retry/deadline behavior to depend on the arbitrary selected inherited declaration.
- Parameter names and nullable annotations participate in SharpLink request schema fingerprints but are also ignored by inherited collapse, so compatibility identity can depend on base-interface ordering.
- Once the Chaos Error oracle was fixed, its three-second TCP self-test exposed two additional client background errors: normal transport teardown completed a `StreamPipeReader` while the request loop still held its returned buffer, and the loop promoted the expected `AdvanceTo` race to an Error. The server loop has the same unguarded terminal `AdvanceTo` pattern.
- The final 120-second shared-memory rerun then exposed a recoverable pool-expansion handshake interruption as `BackgroundLoopUnhandledException`. Fixed, static-cluster, and dynamic-cluster expansion/reconnect loops all used the same Error event for connection failures they catch and retry; this made a normal restart fail the corrected release oracle.
- Repeated full-suite execution exposed a GC assertion that kept the last awaited dispatcher alive through an async state-machine/JIT temporary. The production pool count remained capped at 1,024; moving burst construction and synchronous completed disposal behind a non-inlined helper made the weak-reference proof deterministic.

## Existing conventions

- Runtime lifecycle probes belong in `SharpLink.UnitTests`, use TUnit `[Test]`, explicit bounded waits, and direct state assertions.
- Generator probes build isolated Roslyn sources and count stable `SHARPLINK057` diagnostics.
- The Chaos executable is itself a release gate; an opt-in deterministic injected Error is validated by its process exit/report rather than by duplicating its oracle in another project.
- All new probes are run against production 0.8.33 before production fixes. Generator/Unit use Microsoft Testing Platform with minimum expected counts.

## Acceptance checklist

- Reader completion remains incomplete while either a read operation or a returned ReadResult is active, then releases staging after both are clear.
- Any captured client Error makes Chaos exit non-zero, survives restart-generation clearing in aggregate evidence, and remains visible in the final report.
- Conflicting inherited Oneway shape reports one `SHARPLINK057` and emits no contract artifacts.
- Conflicting inherited timeout/idempotency policy reports one `SHARPLINK057`.
- Conflicting inherited request parameter name/nullability schema reports one `SHARPLINK057`.
- Terminal StreamPipeReader completion during client or server teardown does not produce a background Error, while a non-terminal invalid `AdvanceTo` remains a failure.
- Recoverable expansion/reconnect failures use a distinct Warning event, while truly unhandled background-task failures retain the Error event.

## Engineering boundary

Do not serialize the shared-memory read hot path with a lock. Extend its existing terminal-release handshake to cover `_readOperationPending` and retain synchronous completion when no activity exists. Keep Chaos error accounting monotonic while preserving bounded per-generation recovery diagnostics. Extend the existing inherited-signature diagnostic instead of adding multiple overlapping rule IDs; compatible duplicate declarations and explicit derived redeclarations must continue generating normally. Keep recoverable connection failures observable at Warning without weakening the Error gate for unhandled work.
