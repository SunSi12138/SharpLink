# Ready-writer retained-capacity admission (#735 / #742)

This increment starts from `a10ba8a145bbad92cc7013020260bff196b2b552`.
It extends the opt-in B3 dynamic-lifecycle research path. Shipping `src/`,
protocol fields, windows, defaults, and the fixed A-ready/B3 timing workload
are unchanged. It is not a production Go or a fix for TCP slow progress.

## Capacity policy, not an unbounded registration queue

The frozen `StreamFlowController` distinguishes live-stream exhaustion from
completed tombstones retaining its bounded state map. All live slots occupied
means ResourceExhausted; tombstone pressure permits **one** pending state-capacity
waiter. A second such waiter fails. The constant is one, not max-streams.

`AcquireStreamAsync(requestId, streamId, token)` now provides that bounded waiting
portion for B3 registration. `OpenStreamAsync` remains the existing fail-fast
probe. Both use the same writer inbox and the existing sole identity map/pool.
An older waiting admission is serviced before a younger fail-fast Open can take
a newly released slot. Active/retained duplicate identities still fail instead
of binding a second registration owner to them.

A closed slot remains unavailable until the existing DATA, writer pin, producer,
notification and held-capacity-result conditions permit retirement. Only the
writer mutates the pool, pending-admission reference or identity map. A late
WindowUpdate, final writer release or consumed capacity result can complete
retirement and service the pending admission without a new producer notification.
Generation increment remains checked, with the same Stream object reused.

## Cancellation and ownership transfer

Each cold request has an independent command/completion and an atomic state:
waiting, owner-claimed, canceled, failed or admitted. Cancellation and owner claim
compete for the waiting state. If cancellation wins, no state may be registered;
if owner claim wins, the real admission result must be delivered even if the
caller's token is subsequently canceled. The returned result is not wrapped in
`Task.WaitAsync(token)`, which could hide an already-owned generation.

Cancellation only completes that request and publishes a coalesced owner hint.
It neither changes credit nor needs space in the owner inbox. This permits
cancellation while Flush is blocked and all four entries in a one-stream inbox
are occupied. The private linked token cancels `Channel.WriteAsync`; its
cancellation is normalized to the original caller/connection token. A deterministic
real-Pipe control caught the initial linked-token leak before publication.

Registrations are disposed by the awaiting caller, outside stream gates. No
command/completion is reset or pooled. A canceled request can remain as the one
bounded owner reference until its next turn or Stop, but it cannot later admit.
Terminal failure releases that reference and rejects an unclaimed result without
allocating a state. A claimant returns the common Open result (including an
allocation/generation failure), rather than silently leaking it.

There is no new unconditional retirement/admission RMW on normal DATA selection.
The dynamic path reads a hint and exchanges it only when set; registration and
cancellation still incur cold allocations/RMWs. Neither a bounded Channel nor the
single parked admission caps the number of caller-owned tasks waiting to enter
the inbox. Full call-admission/backpressure remains a separate integration task.

## Validation

All prior 126 shared checks remain. Sixteen additional mechanism checks cover
late credit, live/pending limits, cancellation before inbox/while parked/after
admission, writer pin, held results, non-barging, terminal failure, connection
cancellation, same-key rejection, generation overflow, 1,000 actual capacity-wait
reuse cycles, the frozen controller's capacity policy, and 100 cancellation/claim
races. They do not substitute for mixed-size DATA FIFO or a proof of all races.

Four actual Pipe/SendPump controls cover 16 B and 4 KiB DATA, with and without a
blocked Flush/full inbox. They register the first stream through the actual
writer, retain a closed state, park the next registration, parse peer-SendPump
WindowUpdate bytes, reuse the same state and settle a second DATA generation.
The blocked variants additionally cancel an inbox write while preserving every
published command, original writer ownership and the exact message count.

The parent's fail-fast strategy fails the retained-capacity test. Removing the
retirement-to-admission handoff and changing the live capacity bound to permit
waiting separately fail targeted mutations. All mutations are reverted. Strict
NativeAOT linked-source inventory includes exactly the three new research files;
no allowlist or timing-population guard is weakened. New-head CI remains required.

## Remaining acceptance

This is **registration-capacity** waiting. The production acquire combines state
creation with ordered DATA-credit admission. B3 has not yet integrated its mixed
item/global credit FIFO with this wait slot, nor completed per-call deadline/abort,
blocked-writer Close, generated/full-duplex RPC, cold/retained allocation or fairness
distributions. Open success does not grant DATA credit. Old TCP/AOT timeouts and
negative controls remain failures; prior performance percentages are not assigned
to this increment. Keep #735 open and #742 Draft.
