# Session transport shutdown: abort, join, then complete

## Scope and failure

Baseline: [the source-only pre-transport checkpoint](https://github.com/SunSi12138/SharpLink/commit/fcd9fad97cb354a67ce3c0bbb1a8adc8105ebe99), tree `b2826fbd22ceb69784ae240e10c388c8dbdffd1f` (local source seal `93ec3275`). This is a known-negative reproduction checkpoint, not a deployable version. A real TLS partial-commit
peer-disconnect run reached `SslStream.WriteAsyncInternal` from
`StreamPipeWriter.CompleteAsync` while the send pump still owned a pending flush.
The resulting `NotSupportedException` was a real concurrent-write failure, not an
expected-disposal exception. Fault-triggered shutdown previously started transport
completion immediately after signalling the pump to stop. Explicit session disposal
had the opposite problem: it could wait for a noncooperative writer before asking the
built-in underlying stream to abort.

This correction changes session-owned shutdown only. It adds no send-path field,
registry, counter, check, frame wrapper, or accounting. The send pump, its pending-node
owner, and all ordinary enqueue/flush methods are unchanged. This is a source-level
scope statement, not a throughput or zero-total-runtime-cost claim. In particular,
the previously measured deadline-scan Tier0 cost remains in this baseline.

## Ownership protocol

1. Reuse the session's existing single-flight disposal task and gate. Force an
   asynchronous entry before invoking transport/user code or joining the pump, so a
   pump-originated fault can return to its own physical completion.
2. Reuse the built-in transport's existing single-flight task and gate. Its
   session-controlled cleanup also forces asynchronous entry before underlying stream
   disposal can reenter it.
3. For built-in stream/TLS and anonymous-pipe transports, dispose the underlying stream
   handles to request I/O abort. Independently attempt the anonymous input handle even
   if output abort fails. An abort request is not an I/O-completion certificate.
4. Await the existing actual pump lifetime and its intrinsic pending-enqueue cleanup.
   Do not complete the writer or return its buffers before this join.
5. Complete the writer with the already-selected session terminal exception. This
   discards buffered bytes after the physical join instead of retrying a write on an
   already-aborted stream. Then independently complete the reader through the existing
   `ReadOwnershipPipeReader`, which still waits for an active read/result release.
6. Preserve unexpected abort/join/completion failures, attempt independent cleanup,
   and rethrow the original failure or aggregate. `NotSupportedException` is not added
   to any ignored-exception filter.

Unknown/custom transports receive `CancelPendingRead` / `CancelPendingFlush` requests,
then the runtime joins the actual pump before invoking provider disposal. Their own
pipe completion, resource cleanup and abort implementation remain the provider's
responsibility. An ignored cancellation request can retain ownership indefinitely;
no timeout is treated as physical drain. As before, application-created detached work
and synchronous self-wait inside a user disposal callback are not discovered or solved.

Public standalone transport disposal keeps its existing buffered-flush behavior. A
session already cancelled its lifetime before the old pump wait, so this does not
remove a documented graceful session flush guarantee. Server graceful shutdown retains
its separate `FlushAllSessionsAsync` phase. This patch does not coordinate separate application-owned direct transport disposal
racing a live session; use the session-owned shutdown boundary for that lifetime.
No public interface or generated ABI is changed; the phased capability is internal.

## Deterministic tests

`SessionTransportQuiescenceTests` adds 12 selected cases:

- Four built-in combinations: explicit disposal versus fault, cooperative abort versus
  a deliberately noncooperative physical write. A separately gated physical tail proves
  that abort is not completion. Test-only reflection installs an equivalent counting/
  poisoning pool-backed writer before session creation, without a production hook.
  Original frames and pipeline segments remain owned; all segments return exactly once
  after the join, with no overlapping or post-abort write.
- Two custom-provider combinations prove that cancellation cannot release pipeline
  storage or invoke provider disposal before the noncooperative writer physically ends.
- Pump-originated fault starts cleanup without requiring an explicit disposal call and
  cannot self-join. Reentrant session/transport disposal observes the published owner.
- Abort failure preserves exact exception identity and does not skip independent reader
  completion or the physical writer join.
- Standalone buffered output still flushes on direct transport disposal.
- Already-completed custom pipes remain compatible with repeated session shutdown.
- A failed custom cancellation request still joins physical I/O and invokes independent
  provider disposal, then reports the original failure.
- Anonymous output abort failure still disposes the input and completes the reader.

Two existing lifecycle tests keep their immediate terminal identity and single-publication
assertions, then explicitly await session disposal before checking the physical disposal
count. They no longer conflate logical fault publication with inline physical reclamation.
No timeout, assertion budget, suite parallelism or negative control is relaxed.

## Reproduction and evidence

Build with SDK 10.0.102:

```sh
dotnet build test/SharpLink.UnitTests/SharpLink.UnitTests.csproj -c Release \
  -m:1 -nr:false -p:UseSharedCompilation=false
DOTNET_PROCESSOR_COUNT=1 TUNIT_MAX_PARALLEL_TESTS=8 \
  dotnet test/SharpLink.UnitTests/bin/Release/net10.0/SharpLink.UnitTests.dll \
  --treenode-filter '/*/*/SessionTransportQuiescenceTests/*' \
  --progress off --output Detailed
```

For a source-bound negative, use pristine public `fcd9fad97cb354a67ce3c0bbb1a8adc8105ebe99` and copy only the two new test files
(and the two-line existing lifecycle assertion-order correction for that positive group).
The tests use baseline APIs; there is no no-op adapter or production shim.

The initial draft passed 7/9 cases. Its two custom-pool failures exposed an incorrect
fixture/provider `Complete(null)` after abort: it retried a closed stream and did not
return the segment. The same completion policy in the built-in draft was corrected to
use the existing terminal exception, and the built-in cases were strengthened to observe
pooled storage and prohibit a post-abort retry. Those earlier failures are retained in
the local validation record, not relabelled as passes.

Focused validation does not replace the real TLS partial-disconnect case or final
integrated CI. Named-pipe/shared-memory tests that cannot create their Unix-domain
control sockets in this environment remain recorded as permission failures, not skips.

### Local validation checkpoint

- Release build: zero warnings/errors, SDK 10.0.102.
- Runtime 10.0.2 and official runtime 10.0.13, processor count 1, TUnit parallelism 8:
  all 12 new cases and all 30 lifecycle cases pass on each runtime.
- Pristine `93ec3275` with the identical final new fixtures: eight intended ownership/
  abort-order failures and four positive controls pass. The two corrected existing
  lifecycle tests remain baseline-positive; the complete lifecycle class passes 30/30.
- Other existing groups on 10.0.2: transport cleanup 6/6, send pump 40/40, anonymous
  allocator 10/10 and named-pipe options 9/9 pass. Reader ownership is 105/110 and
  shared memory 26/37: all 16 failures are `SocketException: Permission denied` creating
  named-pipe Unix-domain control sockets. No test was skipped or assertion changed for them.
- Project-reference boundary, maintainability debt, whitespace and diff guards pass.

The original real TLS `CooperativeMaintenancePartialCommitDisconnectFailsClosed` case
also passes against this correction on the same `93ec3275` fixture (1/1). This is not
a full integrated CI acceptance claim: parent validation must additionally bind the
final transport commit with its protocol-phase and exhaustive fixture-cleanup corrections.
