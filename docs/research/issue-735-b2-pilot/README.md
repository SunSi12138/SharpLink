# #735 bounded B2 producer-grant pilot

This isolated research branch starts from dev `f8ba89121c87a6410a0139c44d7db30eab877f41`, with the independently reproduced send-lease capture correction `ae57b4a9ab432832faf7ca1c6a9e7a8b8b2d1015` as its parent. The snapshot ownership correction from `9b6951536d6d1451c694fcf49a1fc73b4994d9d8` is also present. These correctness changes are not performance gains attributed to B2. #840's effective WindowUpdate calculation and #841's route allocation logic remain intact.

## Stage and stop gate

This first candidate is for compiling and executing 41 deterministic controller/bridge cases and the existing complete suites. It has not passed those gates and has no performance claim. A bridge case intentionally exercises a newly identified old-controller defect: an error Complete may precede a late refund from an admitted but never-published item, while AbortException wrongly implies that all debt was already refunded. The first candidate retains that defect to record the actual integration failure before the independent correction is applied.

`eng/issue735/run_b2_correctness.py` writes its complete plan before execution, retains every exit/report/binary identity, stops at the first unexpected failure and marks all remaining steps NotRun. It does not run performance. Its explicit Gi-refund mutant is a new-candidate mutation test, not an old-dev red test. Original tests, timeouts and CI gates remain unchanged.

## Architecture hypothesis

One connection authority owns free connection credit F and the existing state map. Each generation may capture one immutable lease, one lazily allocated stream lock, and a bounded unused grant Gi. The first item is reserved exactly; later small items can consume Gi under only the stream lock. Refill, terminal, cancellation, WindowUpdate, and idle-grant recall enter the existing authority. No SendPump, writer queue, ready-list, frame budget, protocol or public API is added.

The conservation equations are `Wc = F + sum(Gi + Di)` and `Ws = Ci + Di`, where Di is actual admitted item debt, including reserved but unpublished bytes. Unused Gi does not debit stream credit Ci. Under waiter pressure the authority actively recalls Gi from existing states; an idle or noncooperative producer is not required to return it.

Dispose closes future lease admissions and returns unused Gi before waiting for user disposal. An already-admitted item remains owned by its existing item helper. An unpooled immutable generation prevents an old producer's completion or refund from mutating a reused state. Tests cover these ownership transitions and original exceptions.

The pilot deliberately includes its diagnostic cost: local counters update inside the existing stream lock, with global aggregation on disposal. A connection-wide per-item metric is not added. Expected per-stream context and lock footprint still require measurement; source estimates are not allocation results.

## Workload eligibility

The existing generated `IBenchmarkRpc` byte-array streams use `BlitArrayCodec<byte>`, which does not implement `IRpcSizedCodec`. They therefore cannot establish that this server-sized-only pilot runs. A later performance stage must retain that fallback control and separately use an identical A/B, normally generated exact-size DTO contract. It must check actual codec, byte identity, roundtrip, and admission counters before reporting any B2 result. No serializer can be substituted in only one arm.

This branch is a candidate experiment, not a production recommendation. Full RPC results, actual published/executed NativeAOT, CPU, allocations and tails must support a decision after correctness passes. A microkernel or one green run cannot establish Go.
