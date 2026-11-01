# Dynamic intercepted response ownership

This bounded path applies to module-backed server-streaming and duplex calls with
interceptors. It reuses the existing intercepted enumerable as the physical
response carrier. Non-intercepted calls and static intercepted calls do not gain
this guarantee from this change.

## Ownership cuts

The carrier is published to the module caller before interceptor/core callbacks.
It records the exact original pooled response generation immediately after Rent,
and the actual startup task. The existing at-most-once, await-next interceptor
contract supplies one core slot. A replacement/nonforwarding result, synchronous
throw, or failed enumerator acquisition does not erase that original core.
Enumerator acquisition is attempted once; a consumed acquisition error is not
retried during cleanup. A late successful result that has never been acquired is
acquired and disposed by its physical owner.

Successful source readiness includes teardown of the original absolute-deadline
timer. Only then is the pending slot cleared and the source outcome consumed
logically. A failed wait retains the source in its physical slot, regardless of
exception type. Without a deadline the original direct-source await semantics
remain. A detached MoveNext exits before arbitrary enumerator Dispose begins.
The logical deadline is not extended to wait for that physical cleanup.

Before returning detached logical completion, the caller registers an already
owned strict tail rooted by this carrier while it still owns the original module
lease. After definitive acceptance it moves that SAME lease, the existing linked
cancellation source, and any existing producer logical-half handle into the
carrier. No second module acquisition occurs. The actual worker is assigned
before its start gate opens. The anchor completes only after actual worker exit.
Rejected setup keeps the resources with the caller and joins cleanup inline;
partial acceptance is retired empty rather than reporting the caller-owned batch
a second time. A natural continuation-attachment catastrophe is a source branch,
not claimed to have been induced by a disposed-Task fixture.

Every exit reconciles the original sink by exact-generation disposal. The first
disposer owns its raw errors; later disposers only join completion. Existing
supervised startup/duplex-origin outcomes are joined completion-only rather than
reported twice. If server-streaming origin tracking throws after acceptance but
before continuation attachment, the carrier retains that exact registration,
joins actual origin exit completion-only, then independently completes the accepted
registration. Acceptance alone is not evidence that its observer was attached.
Unconsumed physical outcomes, including raw cancellation or the
original Pending deadline error, are retained by strict supervision. This can
make Stop report that original error even though the logical caller already
received a separate deadline outcome. Tests must assert the exact expected
outcome, not an aggregate merely containing it.

## Boundary and costs

The sink is Manager-bound before response frames. The primitive joins captured
Manager dispatch and local-abort credit/abandonment callback exit. It does not
promise a general join for standalone unbound direct DispatchAsync, nor literal
pool-return bookkeeping method exit. Same-thread synchronous local-abort callback
self-disposal is rejected; cross-thread blocking callback cycles are not detected.

The existing intercepted wrapper gains fixed task/result/sink/CTS/lease fields
and scalar phase bits; the existing interceptor state gains a nullable carrier
reference. The linked response CTS is created before callbacks instead of later
at enumeration, with a separate enumeration-token registration when needed.
There is no new ordinary per-call module owner, shared counter, catalog lookup,
lock, or extra readiness adapter. Incomplete cleanup has ordinary async state
machine costs. Actual detached tails additionally allocate two TCS/tasks, a
strict registration/root, worker and continuation; no global admission bound or
performance improvement is claimed.

This is a fresh implementation with separately reviewed Runtime primitives.
Client source controls use actual Connect/Bind/Pending/StreamManager and a
persisted current-ABI owned fixture, but are not a full TCP/Server or ALC collection
proof. Static-after-Seal, dependency adapter cleanup ordering and broader
performance/ALC acceptance remain separate gates.
