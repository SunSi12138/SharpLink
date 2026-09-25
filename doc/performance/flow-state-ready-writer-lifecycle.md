# B3 owner-ordered lifecycle and real state reuse

This increment builds on `3fe2156a0ff7988c723e5f5430cb471378ca455c`.
A follow-up on `876e57b034101bfb97f2e1703d15048b6b29b401` starts dynamic
connections with empty identities and free bounded slots, rather than synthetic
active test keys. Fixed-lifetime A/B controls retain their original setup.
It preserves Phase A, active-key wire permission/settlement separation, blocked
preparation cancellation, SendPump budgeting and all existing evidence gates.
Shipping `src/` is unchanged; the actual pump hooks are still disposable research
transforms. This is not a production-ready replacement or complete #735 acceptance.

## Ownership and identity

The B3 coordinator now has opt-in `dynamicLifetimes: true`. Its bounded stream
slots begin unregistered at generation zero. First Open binds generation one;
retirement/reopen increments it. Slots have a composite wire identity, closed/retired state and a
retained completion target. `OpenStreamAsync` and `CloseStreamAsync` use the SAME
bounded writer inbox as key-only WindowUpdate. The writer alone mutates its sole
identity map, pool and ready list. Producer DATA uses an immutable
`StreamHandle(owner, slot, generation)` and validates under the existing ring gate.
Legacy integer-slot calls deliberately address generation one, never implicitly
resolve a reused slot. A-ready remains the fixed-lifecycle reference harness;
these new dynamic APIs do not pretend to implement a dynamic A-ready adapter.

Close stops new preparation and discards only the unadmitted ring. It retains
wire-credit tombstones and writer pins. A state can return to its pool only when
all DATA is settled, all writer callbacks have completed, its permission is full,
no producer owns preparation, no ready notification is in flight, and no capacity
ValueTask result remains unconsumed. A signaled result is still owned until its
single consumer finishes. Close cleanup failures quarantine the state.

The embedded completion belongs to generation one. Later lifetimes use an
immutable epoch target (one allocation per reopened lifecycle, not per frame).
An old target or notification cannot mutate new-generation DATA. This epoch guard
is NOT a substitute for SendPump's exactly-once callback ownership within a
lifetime. Key-only wire frames still cannot distinguish old/new uses of an
identical retired and legitimately reused key. Tests retain that ambiguity;
internal ABA rejection is not advertised as a wire protocol feature.

Retirement wakeups and scans occur only on closed-generation cleanup. The ordinary
item loop does not unconditionally exchange a new connection-wide atomic. Initial
producer ownership is now claimed within its existing capacity gate rather than
an independent Exchange. New handle checks and wire identity lookup are real
cost changes: old performance numbers cannot be used as this head's results.

## Checks and negative controls

The shared JIT/NativeAOT self-test now includes 16 lifecycle cases and two actual
Pipe/SendPump cases in addition to the original 108 checks. The cases cover empty-capacity first registration/overflow rejection, empty
and prepared close, tombstones, early-credit writer pins, held capacity results,
blocked producers, ordered close/update/open, foreign and stale handles, stale
notifications/completions, generation overflow, pending commands on connection
stop, ready order, wire key ambiguity and 100,000 actual reuse cycles. Each reuse
cycle prepares/takes/releases/credits/closes/reopens the same Stream object.

The 16 B and 4 KiB real-pump cases read actual DATA while Flush is deliberately
paused, queue Close, parse actual peer-SendPump WindowUpdate bytes, then queue
same-key Open. Neither command may complete through a second owner while Flush is
blocked. On resume the original writer releases its frame and executes the ordered
inbox; the same state is reused and another generation's DATA is parsed and settled.
Handshake state is installed in the fixture, not exchanged by a full RPC handshake.

Three unsafe mutations are independently rejected: omit the writer pin from
retirement, omit held-capacity ownership, and accept an old completion generation.
They are counterexamples to proposed unsafe policies, not defects attributed to
the prior fixed-lifetime implementation. No old test or timeout is weakened.

## Remaining boundaries

Open currently rejects exhausted retained-slot capacity; it does not yet implement
the original controller's queued state-capacity admission contract. Ready arrival
order is checked, but complete global waiter FIFO/fairness under heterogeneous
items is not established. Bytes/item stay fixed per coordinator. The dynamic
control uses its own reader boundary, not generated calls or full duplex. Stream
Close is writer-ordered and therefore waits behind blocked Flush; the previously
implemented connection-level prepared cancellation is independent. Full logical
call cancellation/deadline and per-stream abort arbitration remain outstanding.

Dynamic lifecycle operations, epoch targets and command completions allocate and
are NOT included in the existing fixed-lifetime transport timing matrix. Cold and
retained allocation acceptance remains open. `Completion` retains fixed-count
semantics in the default control; dynamic mode must be declared before starting
and joins explicit lifecycle operations rather than reusing a previously completed
fixed-count task. Existing JIT/native matrices measure fixed lifetimes on the exact
new source; they do not become dynamic RPC performance evidence.

Keep #735 open and #742 Draft until remaining production invariants, the complete
transport population and allocation/performance acceptance are actually verified.
