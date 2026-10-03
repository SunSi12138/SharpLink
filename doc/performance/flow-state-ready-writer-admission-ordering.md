# Registration cancellation ordering after e50247b5

This increment builds on `e50247b5c6bd959a6e597c286bea6bf7da3f9a4b` in
#742. It preserves the existing `AcquireStreamAsync` waiting entry and
`OpenStreamAsync` fail-fast probe, the one retained-capacity waiter, active
stream limit, sole writer-owned identity map and all generation/credit pins.
It does not apply the earlier, independently prepared capacity patch over the
published implementation. Shipping `src/` is unchanged by this increment.

## Reproduced defects

`CancellationTokenSource.Cancel` marks its token before executing callbacks.
Those callbacks run synchronously in reverse registration order; a callback
can therefore delay forwarding cancellation to a linked token even though
the original caller token is already canceled. Reference contract:
https://learn.microsoft.com/dotnet/api/system.threading.cancellationtokensource.cancel

The existing capacity request inspected only its callback-updated claim state.
Four new deterministic controls were compiled and run against the unmodified
admission policy; all four failed:

1. A request already in the inbox claimed a free generation while the original
   token was canceled and its forwarding callback had not run.
2. A parked request claimed newly retired capacity under the same ordering.
3. An already-canceled request received ResourceExhausted from the active limit
   instead of cancellation with its original token.
4. A cold writer observation ran before a canceled parked request was reaped,
   so the observer still saw the sole capacity-wait slot occupied.

The delayed-callback cases install a later callback with an explicit entry
and release barrier. They wait for the actual callback entry before driving
writer delivery or retirement. Finally always releases the barrier and joins
the cancellation task. Watchdog timeouts prevent a broken fixture from hanging;
no elapsed-time guess is used to establish the tested event ordering.

## Fix and ownership boundary

The cold capacity request observes the original caller and connection tokens
at execution, immediately before its atomic claim, and while the writer services
a parked request. Observed cancellation uses the existing request-local Cancel
arbitration and original token; it does not mutate a stream, credit or pool from
a callback. If claim already won, subsequent cancellation still cannot discard
the admitted handle. Concurrent cancellation after the token observation and
claim remains a race with one winner, not a promise of preemption.

Generic cold writer operations now service older capacity admission before
executing their action. This reaps canceled requests at the next owner operation
boundary and preserves older waiting-registration priority. This servicing is
not inserted into each DATA selection or balanced wire-update operation.

The change adds one owner reference to the existing cold WriterOperation object.
It does not add a new per-item command, callback, frame field, shared atomic
operation or allocation. Cold registration/lifecycle allocation remains an
unmeasured cost; fixed-lifetime B/item is not evidence of zero cold allocation.

## Validation and remaining acceptance

The four failures become passes with the fix. All prior 146 shared checks are
retained, yielding 150 checks, including the original same-state reuse and
cancellation/claim controls. Default and disposable-experiment full UnitTests
were each executed to 1897/1897 with captured exit code zero. One earlier local
tool invocation was interrupted without an exit code and is not counted as a
pass. Local offline restores use NuGetAudit=false, not an online package audit.

The new controls live in the existing shared admission-check source; no native
source allowlist or evidence population is weakened. Exact-head CI must validate
both JIT and NativeAOT. This is not a TCP slow-progress fix, combined state/DATA
admission, global DATA FIFO proof, or complete generated/full-duplex RPC.
Keep #735 open and #742 Draft until those outstanding contracts and stable
complete transmission evidence are satisfied.
