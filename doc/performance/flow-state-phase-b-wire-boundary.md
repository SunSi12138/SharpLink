# Phase B: actual receiver credit return and wire compatibility (#735 / #742)

## Scope and baseline

This increment builds on `09638eba6b84809ba67b26cd5de4a2acc89c3d1b` without
changing shipping `src/`, Phase A, negotiated defaults or wire encoding. The
frozen controller remains `65892a1d9cef5a79b6afb865e667935a4b5b5e33`; its
hash guards still apply. B0's disposable split-gate validation also runs the
new boundary tests. No additional production credit map is introduced.

The send model's sole map now keys `(requestId, streamId)`, not only requestId.
Its existing `OpenAsync(requestId)` keeps stream ID 1 for all prior callers.
Internal handles retain generation/owner validation. `ObserveWindowUpdateAsync`
accepts key-only events and resolves the current state on the same connection
owner that serializes lifecycle operations. It does not capture a potentially
stale state before queueing. A reusable command retains each result until
consumption; overlapping held results use independent, counted slots.

## Actual frame path, not a generation-tagged synthetic return

`PhaseBWireBoundaryFixture` exercises:

```
B2 writer admission -> real RpcSession/SendPump -> actual StreamData bytes
  -> ProtocolV2FrameParser -> negotiated receiver StreamManager
  -> receiver accept / consumer notification / threshold accounting
  -> real receiver SendPump -> actual key-only WindowUpdate bytes
  -> ProtocolV2FrameParser + ProtocolV2PayloadCodec
  -> connection-owner key resolution -> conservative B2 ledger
```

A pump flush completion is the barrier before checking whether the receiver
retained below-threshold credit. Pipeline memory is consumed/advanced only
after processing; no returned ReadOnlySequence survives AdvanceTo. The packet
copy delivered to the receiver comes from the sender transport, not from the
fixture's expected frame array. Fixed-header and coalesced-frame segmentation
is exercised separately. These are Pipe-backed transport boundary tests, not
TCP/SharedMemory performance or the production request-loop replacement.

The sender's built-in flow controller is still disabled to prevent double
debit. The receiver **does** negotiate FlowControl and uses its unchanged real
receive controller/StreamManager. This validates the existing receiver's
threshold liveness with B2, not a new receive-side batching algorithm. Test
handshake completion establishes explicit windows rather than exercising the
entire handshake exchange. Logical-call cancellation/deadlines and request
arbitration remain outside this fixture.

## Tests and attribution

Ten new model checks cover composite identities, ignored excess / pending
receipts, early wire credit with writer pins, connection FIFO, oversized debt,
held command results, settled command reuse, ordered lifecycle events, invalid
zero credit, and the lack of a wire generation discriminator. All previous
54 model checks remain, including actual 100,000-generation pooling cases.

Ten new tests in the real UnitTests assembly exercise cross-stream connection
threshold flushes, below-threshold terminal flush, an early credit while the
actual sender Flush is paused, repayment that wakes a second blocked writer,
512 real StreamData/WindowUpdate cycles across eight streams, segmented and
coalesced frames, invalid payload/truncation rejection before mutation, a
balanced differential trace, duplicate-credit inequivalence, and same-key
reuse ambiguity. The eight-stream cycle is deliberately sequential/deterministic,
not a c8 contention benchmark. The old six writer tests are retained.

The balanced differential trace checks the real frozen controller after each
admission and each partial return. The comparable availability is
`model.Free + model.Unspent`, since grants reserve connection credit before
local items debit it. The model's own conservation assertion continues to run
on every snapshot. A test showing no new command objects after warmup is **not**
a zero-total-allocation assertion. Cold command/gate/state storage, overlap,
frame copies and fixture Tasks are not free and are not measured here.

## Explicit compatibility gap: independent clamp versus receipt conservation

Use a 16-byte stream window and a 32-byte connection window. Streams A and B
have each published 16 bytes. A remains active (not retired). Apply the exact
same valid `WindowUpdate(A, 16)` frame twice, with no new send on A:

| Event | Frozen connection credit | Model free credit | Model B outstanding |
|---|---:|---:|---:|
| Both streams sent | 0 | 0 | 16 |
| First A return | 16 | 16 | 16 |
| Repeated A return | 32 | 16 | 16 |

The existing `StreamFlowController.ApplyWindowUpdate` independently caps
stream and connection credits and explicitly tolerates benign double returns.
The model's old generation-tagged policy (and now its key-only observation
control) returns only the target stream's outstanding bytes. Its observation
reports `Returned=0, Excess=16` on the second frame. Neither the shipping
clamp nor the model invariant was changed to make this test agree.

**Passing this characterization records inequivalence, not protocol compatibility.**
The data does not by itself establish a shipping bug: protocol-advertised
connection permission and an exact per-item receipt ledger are different
accounting concepts in this case. A production B2 adapter must explicitly
reconcile them, including local unspent grants, pending unsent refunds and
writer ownership. Simply adding the excess to `_free` violates the current
conservation equation; simply discarding it silently changes the existing
credit policy. Both shortcuts are rejected as production acceptance.

## Wire and local ABA are distinct

Completed, uncredited streams retain tombstones. Fully credited but writer-pinned
states cannot be reused until settlement. Once an identity has been legitimately
retired and reused, the wire contains no generation field: an identical old and
new WindowUpdate frame is indistinguishable. Both the frozen controller and
this control resolve it against the current identity. Internal stale leases
still fail generation checks. That guarantee must not be mislabeled as old-wire
generation rejection; a future production design needs the actual request-id
reuse/transport-order contract, not an invented protocol field.

## Acceptance boundary

Keep #735 open and #742 Draft. This advances the real credit-return boundary
and identifies a specific compatibility decision needed for production. It
neither rejects Phase B nor changes protocol semantics. Remaining work includes
reconciling advertised connection credit with reservation/receipt debt,
logical-call cancellation and state-capacity/multi-handle contracts, receive
batching, retained/cold allocation and fairness, and full B-vs-frozen-A
TCP/SharedMemory comparisons with JIT PGO ON/OFF and NativeAOT. Existing timing
reports do not benchmark this new wire adapter and must not be reused as its
performance evidence.
