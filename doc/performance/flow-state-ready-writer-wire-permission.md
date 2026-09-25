# Ready-writer independent wire permission and DATA settlement

This increment is based on `6d556349dc32514d1927f5994524b4eb5bd83efc`.
It preserves Phase A, the ready-writer schedule, frame layout, protocol, default
windows and all failed/complete historical evidence. Shipping `src/` is unchanged.

## Active-key compatibility, not a replacement lifecycle implementation

B3 previously applied independent stream/connection permission clamps and then
threw when an update exceeded that stream's outstanding DATA. A-ready already
used the original controller's clamps, but its research completion counter added
raw wire bytes. A repeated update could therefore complete the A-ready transport
while another stream still had outstanding DATA. These are research adapter and
harness defects, not claims of corresponding production controller defects.

The writer now separates these two owner-only quantities:

- Advertised permission uses the original independent stream/connection clamps.
- DATA settlement consumes at most the addressed stream's outstanding debit.

For window 16 / connection 32, after A and B each send 16, two `Update(A,16)`
messages leave connection permission at 32, A credit at 16 and B credit at 0.
B still has 16 outstanding bytes; only 16 bytes have settled. B3 must not reject
the second update, debit B's stream, or complete the joined transport. There are
no speculative local credit grants in B3, so this is not the standalone B2 grant
ledger's unresolved reservation/permission compatibility problem.

A-ready and B3 both maintain an owner-only shadow DATA ledger at frame selection.
The same persistent completion targets and per-frame SendPump budget remain.
Known fixed-slot keys with no B3 send admission, or keys outside the fixed map,
are obsolete. Unknown composite keys do not restore another stream's permission.
A-ready still delegates wire permission to the original keyed controller.

Unsent return has a DIFFERENT contract from WindowUpdate. The original controller
throws if returning it would exceed either window; it does not silently clamp.
B3 now checks both prospective credits before mutation and preserves this failure,
including when an earlier duplicate wire update restored connection permission.
Such a failure does not increment released-frame counts or alter outstanding debt.
Early peer credit still cannot release a frame retained by the writer.

`CreditBytesApplied` remains settled DATA bytes. Additional `WireCreditBytesObserved`,
`ExcessWireCreditBytes` and `IgnoredWireUpdates` explain extra wire input. All are
owner-written; no connection-wide per-item RMW, command or allocation is added.
A-ready gains the same shadow-ledger increment already present in B3. This is a
real harness cost change and requires exact-head timing; old numbers are not new
performance results.

## Checks

Twelve deterministic A-ready/B3 cases cover repetition, `int.MaxValue` excess,
unknown/pre-admission keys, rejected conflicting refunds, held writer completion,
and repeated actual WindowUpdate frames through a peer SendPump and parser.
The wire fixture drives DATA ownership synchronously; it is not a full RPC loop.
Three fixed random seeds each execute 12,000 send/update steps against the complete
Phase A controller and probe every stream's admission after each step. These 36,000
steps establish fixed-active-key permission behavior, not waiter/FIFO or reuse.
The original 93 shared checks remain; this adds 15, for 108 checks.

The original policy fails these new regressions. Independently mutating the fix
to count raw bytes again, or omit connection-window validation on refund, makes
checks fail. Both mutations are reverted. The timing validator rejects the new
counter group if incomplete or if a balanced timing run contains excess/ignored
wire input. Historical reports lacking the entire group remain readable as old
reports; they do not retroactively prove the new semantics.

## Remaining boundaries

Streams are still fixed-lifetime. Dynamic registration, retirement/tombstones,
pooled generation ownership and original global waiter FIFO remain unimplemented
here. Key-only frames cannot identify an old generation after legitimate key reuse.
This increment does not claim to solve that ambiguity, logical-call/deadline
arbitration, full-duplex generated RPC integration or TCP receive-window stalls.
No timeout, population, default socket buffer or performance gate is relaxed.
Keep #735 open and #742 Draft until those acceptance conditions are validated.
