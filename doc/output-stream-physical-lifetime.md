# Keep output owners until the producer physically exits

A call can reach its deadline while user `MoveNextAsync`, a real send, or enumerator
`DisposeAsync` is still running. The caller's logical terminal stays prompt; that
terminal does not release the invocation's physical owners.

## One existing invocation owns the physical join

The generated Runtime pump still returns its logical deadline/error without waiting
for non-cooperative user code. On an actual abandon path it hands the unfinished
operation to `GeneratedOutputPhysicalTail`. Its sealed chain first observes the
unfinished operation's actual exit, even when it faults, then invokes and awaits
enumerator disposal exactly once. An already-started disposal is joined, not restarted.

Only after the chain has registered both its pending work and any deferred disposal
does the pump seal it and transfer its original linked CTS. Completion observes late
faults and disposes that same CTS once, after the entire chain exits. A cold asynchronous
handoff prevents synchronous user disposal from blocking the logical pump/read loop.
This also covers a deadline selected at clean EOF, with no unfinished MoveNext/send:
the handoff happens before calling user disposal, not after it returns a ValueTask.

This ordering also supports compiler-generated async iterators: their DisposeAsync
rejects an in-flight MoveNext before entering iterator cleanup. Microsoft documents
that these operations must not overlap in its [async-enumeration threading guidance](https://learn.microsoft.com/en-us/archive/msdn-magazine/2019/november/csharp-iterating-with-async-enumerables-in-csharp-8#much-ado-about-threading).
There is no public SharpLink contract promising concurrent iterator disposal.

The Server's existing output-entry allocation, now `OutputInvocation`, owns this
join. Its stream-reference slot becomes the cold physical-tail reference after
enumerator acquisition; there is no shared cache, pool, new generation lookup, or
copied release authority. Its returned work finishes only after the physical join.

The existing outer invocation therefore retains, without reacquisition:

- the exact original registration and its ServiceLease/module call+stream lease;
- the original call-state CTS and cause;
- the original Codec and request context captured by generated dispatch;
- the per-call service scope until ordered scope cleanup finishes.

After output completion, existing Duplex input draining and service/scope/module
cleanup continue in their original order. The #984 OneWay/input physical join is
unchanged. Cleanup failures cannot skip the outer lease's remaining cleanup.

Standalone Runtime bridge callers keep the same entry point. An adapter is created
only when they actually abandon work, retaining their linked CTS until physical exit;
they do not need a new public SPI or a Server module owner.

## Logical terminal, connection progress, and Stop stay separate

The receiver's deadline scheduler selects the original cause on its System clock.
The client observes its own normal deadline/cancellation; no new server wire
`DeadlineExceeded` promise is introduced. Business error mapping and local send-flow
termination occur before the Server's physical join.

The connection read loop already starts independent request dispatch work. Awaiting
this invocation's physical completion does not await it in the read loop. A permanent
public test sends a second healthy request through the same original Session while
the first output tail is held.

Unregister's immutable bounded result remains `ReferencesReleased=false` while the
old physical call survives its original grace. Background retirement finishes after
the real tail exits; an earlier result is not rewritten. Stop keeps its existing
absolute grace+cleanup budget and defers physical service cleanup when needed. It
must not reclaim the original service/CTS to make that budget appear satisfied.

## The normal allocation shape stays unchanged

Release compilation on SDK10.0.115/x64 gives these actual state-machine struct sizes:

| Existing allocation | Before | After |
|---|---:|---:|
| Runtime generated output pump frame | 224 B | 224 B |
| Server output pump frame | 88 B | 88 B |
| Existing stream-entry wrapper fields | two references + one long | two references + one long |

The Server async method moves into its existing wrapper, replacing the old `this`
reference and removing redundant stream/request-id parameter slots. No normal-call
object is added. Cold drains/adapters exist only after actual abandon. Normal output
RPCs add one enumerator-entry cast and a fixed completion/ownership check, not a new
per-item operation. These static facts establish the allocation shape, not timing
equivalence or a throughput improvement.

## Regression evidence is about the held interval

The original dev2c77 public baseline used explicit method2s deadlines and independently
ordered receiver expiry before client clock advancement. Server/Duplex × held
MoveNext/Dispose all released their module/service/CTS prematurely. Its four failures
are preserved in run43633653926, toolf53d571d; earlier R1/R2 fixture failures remain
separate and are not product evidence.

Permanent regressions retain the original2s deadlines, shared5s observation watchdog,
and2s unregister grace. They require original module1call/1stream, undisposed original
service and CTS while held, then physical exit and actual old-module retirement before
zero/once assertions. They also cover per-call ownership, late service-disposal failure,
caller cancellation, same-Session progress, bounded Stop, and collectible unload.
Caller cancellation preserves the actual original remote cause: the pending-call
callback can select `RemoteCancel`, or the response enumerator's cancellation and
cleanup can select `ConsumerAbandoned`. Neither is treated as a receiver deadline.

The permanent gates remove a historical probe dependency on concurrent disposal:
held MoveNext now requires Dispose not yet entered; for held Dispose, receiver
cancellation releases only the non-target MoveNext. The Dispose gate is released
only by the final controller. The historical R3 failures remain their original data,
not results for this revised fixture.

A separate exact send component exhausts genuine flow credit, resumes the real sized
send into a held Codec serialization, and crosses the deadline. It checks physical
send/snapshot/CTS completion, original deadline priority, returned credit and no late
data publication, including a snapshot cleanup failure. It is not presented as a
second public Module/ServiceLease experiment.
