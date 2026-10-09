# Phase B: real SendPump publication-boundary integration control

This supplements [the Phase B research design](flow-state-phase-b.md), preserving
the direct-admission implementation at `7c28e277500bf5c47dc0de6877618709140a8a5b`.

`PhaseBWriterBoundaryTests` links the exact four grant/command source files into
the existing UnitTests assembly. It does not reference the research executable
or its generated A/B0 controller copies. The fixture gates prepared Protocol v2
StreamData frames with the B2 ledger and calls the real
`RpcSession.TryEnqueuePreparedFrame` / SendPump implementation.

This deliberately tests an integration boundary, not a drop-in controller:
the session does not negotiate its built-in flow controller (so credit is not
debited twice), and peer credit is still a generation-tagged model event. Frames
are actually serialized and read from a Pipe-backed test transport; the transport
can pause flush completion after those bytes are readable. No production hooks,
reflection into the pump, timer delays or changes to shipping src are required.

The fixture keeps an uncancellable emission/settlement task after the real pump
accepts a frame. A caller can cancel observation, but cannot thereby refund credit
or release the publication pin. Queue rejection is known-unenqueued and may
refund once. A transport failure after acceptance is ambiguous/possibly visible:
it releases ownership while retaining outstanding credit, never treating it as
unsent. The real session lifetime cancels a different stream's pending model
admission; its prepared buffer and reusable completion slot are then released.

Six tests cover deterministic byte-queue rejection, actual readable bytes plus
early peer credit/closed tombstone, observer cancellation, a real SendPump output
fault after visibility, session-fault cancellation of a credit waiter, and
pre-admission caller cancellation. All asynchronous operations remain tracked
and joined; cleanup observes expected faulted tasks rather than abandoning them.
The tests check the runtime's normalized ConnectionClosed exception and original
inner output failure, not a fixture-invented error contract.

The fixture allocates an emission waiter, ticket, buffers and linked cancellation
source; it is **not** a proposed production fast path or allocation/performance
benchmark. It also does not replace logical-call cancellation/deadline arbitration,
key-only wire update routing, connection receive control, full multi-handle/state
capacity behavior or TCP/SharedMemory end-to-end acceptance. Those boundaries
must be preserved when implementing a production adapter; passing these six
integration controls alone is not Phase B Go.

A local mutation control changed the enqueued-failure settlement into a refund.
The real-visible-bytes test then failed with outstanding credit incorrectly
restored. The mutation was reverted and all six tests passed; this rejects an
unsafe adapter policy, not a claim that shipping SendPump had that bug.
