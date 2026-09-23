# Flow-state Phase B research (#735 / #742)

This continues Phase A; it does not undo the No-Go for the old production
candidate, and does not close the reopened architectural investigation.

## Frozen comparison

- Phase A source: `65892a1d9cef5a79b6afb865e667935a4b5b5e33`, including atomic
  first-admission/generation capture. `prepare-flow-state-phase-b.py` checks
  exact Git blob hashes before generating any experimental controller.
- The established pre-Phase-A baseline remains
  `56c643cd308f294cb03df79d9f1214fb8292affb`. Historical A-vs-dev results must
  not be relabeled as B-vs-A results.
- Production `src/`, wire protocol, default windows, routing, and the Phase A
  implementation are unchanged by this research increment. Generated A/B0
  controllers live only in the non-packable test project's `obj/` directory.

## Experiments and what they establish

**B0** is the complete Phase A controller with receive state, receive connection
credit, consumed batching, and receive pooling moved to a separate gate. Send
state, send connection credit, first admission, FIFO waiters and send pooling
retain the original gate. Terminal publication takes send then receive, the
only nested gate path, and completes callbacks after releasing both. The CI
validation job applies exactly this split to a disposable checkout and runs
the existing flow-controller matrix, including session first-admission tests.

A/B0 compare send acquire+unsent-return, receive accept+consume, and concurrent
send/receive loops. They are controlled microkernels, not TCP/SharedMemory
throughput tests. Splitting gates does **not** reduce two gate entries per pair;
it measures unnecessary cross-direction coupling. Same-direction regression
or improvement must not be hidden behind a Duplex aggregate.

**B1** is a send accounting model with one logical connection owner and a
bounded, single-reader Channel. It submits every acquisition to the owner.
**B2** uses the same model and queue but grants 256 B / 1 KiB / 4 KiB of bounded
connection credit. Local debit and receipt settlement take only a per-stream
gate and read a shared pressure flag; no connection-wide RMW is added to the
local item loop. Peer-update commands use the same every-64-items schedule in
B1 and B2. All commands, including peer updates, count toward handoffs/item.
A 4 KiB item with a grant of at most 4 KiB is an essential negative control:
it still requires a handoff on every item.

The grant model is **not a drop-in production controller**. In particular:

- It is send-only; receive thresholds remain tested on the real A/B0
  controller, not implemented by the grant model.
- Updates are generation-tagged model events, not key-only wire WindowUpdate
  messages. Real wire tombstone routing/duplicate update behavior still needs
  differential integration tests.
- It permits one unsettled receipt/publication and one pending acquire per
  stream. This is an explicit model restriction, not proof of all production
  multi-handle interleavings. Explicit writer ownership is now modeled below;
  a real SendPump/transport has not been connected to that contract.
- Pending state-capacity admission and the exact production abort/error
  contract are not implemented. Adaptive grant sizing is not implemented.
- Refill/update commands and their completion sources are preallocated per stream.
  Lifecycle commands, overlapping held updates, replacement generations with held
  completions, cancellation/error paths and bounded-queue backpressure can still
  allocate. This is not a production no-allocation-regression claim.

The ledger invariant includes acquired but unpublished receipts:

```
free connection credit + unspent local grants
+ pending unpublished bytes + committed outstanding bytes
= negotiated connection window
```

Oversized acquisition is permitted only with the existing full-window
borrow precondition. On waiter pressure the owner first publishes a freeze
flag, takes each local gate to reclaim unspent grants, and admits only the
requested bytes in FIFO order. It skips stream-credit-blocked requests, but
not a connection-credit-blocked head. Cancellation and closed/stale waiters
are removed independently of that blocked prefix. This avoids both grant
hoarding and cancellation/close starvation behind a blocked head.

## Attribution and measurement limits

Run from the repository root with .NET 10 and Python 3:

```sh
python3 eng/test-flow-state-phase-b.py
dotnet run -c Release --project test/SharpLink.FlowStatePhaseB -- --self-test
dotnet run -c Release --project test/SharpLink.FlowStatePhaseB -- --b0-only --items 10000 --repetitions 4 --output artifacts/phase-b/b0.json
dotnet run -c Release --project test/SharpLink.FlowStatePhaseB -- --grants-only --items 1024 --repetitions 4 --output artifacts/phase-b/grants.json
python3 eng/summarize-flow-state-phase-b.py artifacts/phase-b
```

Use `--diagnose --b0-only` for separate wait/hold timestamp attribution.
Non-instrumented A/B0 counts use the audited two-entry inventory; diagnostic
runs count actual entries under the corresponding gate. Wait/hold sums across
workers are not wall-clock percentages. Timestamp/counter instrumentation can
perturb contention and is not used for performance acceptance.

Owner handoffs mean submitted owner commands, not OS thread switches. Queue
operations count enqueue plus dequeue. Explicit RMW counts cover only our
submission counters, not Channel, Lock, task or runtime internals; the latter
are explicitly `null` (unmeasured), never zero. Direct metrics are required
because process-wide `Monitor.LockContentionCount` cannot attribute cost to
one controller lock. See the official runtime metric definition:
https://learn.microsoft.com/en-us/dotnet/core/diagnostics/built-in-metrics-runtime

Fixed worker counts and alternating A/B order reduce some scheduler bias,
but these short exploratory loops are not a stable throughput threshold
claim. Grant measurements use asynchronous producers; their timings must
not be compared directly with the full-controller family. Warmup/setup,
serialization and transport are outside the measured owner-model loop.

## Acceptance boundary

B0 correctness and B2 model conservation are separate checks. A lower
B2/B1 handoff count validates the batching mechanism, not full Phase B Go.
Still required: full lifecycle/pressure allocation accounting, real per-stream
publication leases, key-only wire events, full lifecycle/FIFO differential
coverage, receive-side batching/connection-threshold liveness, waiter latency
and fairness distributions, independent instruction attribution, one-item
stream controls, and end-to-end TCP/SharedMemory A/B against the same Phase A
base. JIT PGO ON/OFF and NativeAOT research runs belong to their exact head.

Neither the old Phase A contention regression nor an incomplete B2 model is
a reason to close the reopened issue or declare the architecture No-Go.

## Reusable completion increment

`ReusableOwnerCommand<T>` implements a single-consumption `IValueTaskSource<T>`.
The completion gate serializes publication/consumption, never credit mutation.
The only nested order is stream gate then completion gate. GetResult releases
completion ownership before disposing cancellation registration or taking the
stream gate. An acquisition releases its pending bit and command slot under that
same stream gate. The local item path gains no connection-wide RMW or queue entry.

A completion is not reusable just because the owner has called SetResult. Held
results keep their slot busy until consumed. Renting the same pooled state while
a previous generation still holds a result installs a new slot rather than
resetting the old token. Overlapping updates likewise get an independent slot.
Those slow-path allocations are counted, not hidden. Waiters reuse intrusive
nodes that the owner must unlink **before** signaling completion. Cancellation
wakes only the authority; callbacks never retain a recyclable request token.
Queue-full wakes may coalesce only because a queued command guarantees another
cancellation scan. Queue backpressure uses an allocating slow helper and remains
in the counters.

The public .NET ValueTask contract requires single consumption, and a pending
result must not be read synchronously. Defensive invalid-token checks do not
relax that contract:
https://learn.microsoft.com/en-us/dotnet/api/system.threading.tasks.valuetask-1?view=net-10.0
https://learn.microsoft.com/en-us/dotnet/api/system.threading.tasks.sources.manualresetvaluetasksourcecore-1?view=net-10.0

The first 17 checks remain; 11 additional checks cover held and canceled
completions across real state reuse, independent overlapping updates, intrusive
node reuse, bounded backpressure, coalesced cancellation wakes, closed queue
failure and stale completion tokens. These remain model checks, not the full
production publication/wire contract.

The `completion-control` CI job compares the exact allocating model at
`4d7335c8c542136d895d60339131164b0052426b` against the new head. It overlays
identical Program/GrantProbe files on both; the only conditional compilation is
for counters absent from the reference. The reference credit algorithm is not
modified. The source hashes, revisions, runtime and affinity are retained.
JIT PGO ON/OFF and NativeAOT run AB/BA launch order at c128/16 B with 1024 and
16384 items/stream. Every launch includes all four grant settings and two
samples each. The report requires all 24 files / 192 matched rows; missing
cases, unknown reports, wrong revisions and fake zero counters are errors.

`ReusableCommandsAllocated == 0` in a settled long-stream loop is only an object
inventory statement. `AllocatedBytesPerItem` still reports actual process-wide
allocation, including the fixed Task/closure harness cost. Comparing lengths
helps distinguish that fixed cost from per-command slope, but excludes stream
setup and cold lifecycle work. Source-level counter success is **not** substituted
for measured zero B/item. Time regressions are printed with a positive sign;
allocation improvement is not a speed or production acceptance claim.

Use `--grants-only --streams 128 --item-bytes 16` for the narrowed control.
The full default c1/c8/c32/c128, 16 B/4 KiB matrix remains unchanged. Connection
handoff counts include all peer updates and the 64-item warmup's residual grant.

## Writer publication ownership increment

Credit admission is not equivalent to writer completion. `BeginPublication`
transfers an admitted receipt from unpublished pending bytes to outstanding
bytes and installs a generation/sequence/size-bound writer pin under the same
stream gate as Close. If Close wins first, Begin rejects; if Begin wins first,
Close may reclaim unused grants, but not the writer-owned bytes. This is still
an isolated accounting model, not a production SendPump integration.

`FinishPublicationAsync(accepted: true)` releases writer ownership, not credit.
`accepted: false` is legal only when the writer proves no bytes became peer
visible. It refunds that debit once; a closed stream returns the unused refund
to the connection owner even if earlier committed bytes are still outstanding.
Ambiguous transport failure must terminate the connection, not be translated to
`accepted: false`. Receipt size and generation/sequence identity are checked
before releasing the pin.

Ordered, generation-tagged model WindowUpdates consume earlier committed bytes
before an active publication's uncredited bytes. Peer credit can arrive before
the writer continuation runs. Even when every byte has been credited, a closed
state remains pinned and cannot be recycled until the writer settles. A
publication already credited by the peer cannot also be refunded as unsent.
This does not establish parity with the production key-only wire protocol,
including its duplicate/clamped WindowUpdate behavior.

The existing ledger remains unchanged: outstanding includes the uncredited
writer-owned subset; pin counts are lifetime ownership, not a second debit.
Normal Begin/Finish uses only the existing per-stream gate and adds no owner
command, completion allocation or connection-wide RMW. A closed-state refund,
retirement or pressure reconciliation uses the existing allocating cold owner
path. After connection termination there is no further admission or reuse; the
last writer releases its local pin without restarting the closed owner queue.

Thirteen new checks bring the model suite to 41. They exercise early peer credit,
close-before/after-Begin, rejection with earlier bytes outstanding, duplicate
and altered receipts, concurrent settlement, foreign/stale generations, terminal
completion, local coordination counts, and 100,000 real pool reuse generations.
Two additional boundary tests were first run red (closed refund retention and
altered-size settlement) before the corresponding fixes. These tests do not
replace the real runtime's flow-control and publication tests.

`--publication-only` measures the same candidate with immediate legacy Commit
versus explicit Begin/Finish. The full c1/c8/c32/c128, 16 B/4 KiB matrix uses
4 KiB grants and the same every-64-items update schedule. The CI requires three
exact-head reports (JIT PGO off/on and NativeAOT), 96 matched rows, two AB/BA
samples per shape, and complete publication settlement. This is an added-cost
control, not an end-to-end performance win. Its timings cannot be replaced by
the preceding completion-control results, which used immediate Commit.

For heads after `3d4be885`, the allocating-reference completion-control still
compares the frozen `4d7335c8` model with the full current candidate; it therefore
includes later model changes, not just completion reuse in isolation. The
recorded `3d4be885` result remains tied to its own exact source and CI artifact.

## Direct writer-admission increment

`AcquirePublicationAsync` is an explicit writer-facing operation. It binds the
publication pin in the **same stream critical section as credit admission**.
The ordinary `AcquireAsync` + `BeginPublication` contract remains available;
they are not silently fused for callers that only requested a revocable receipt.
A direct caller must consume and settle any successfully admitted publication,
even when cancellation, close or termination wins before its continuation runs.

The normal local path now enters the stream gate once for admission/pinning and
once for settlement, instead of separate acquire/begin/finish entries. This is
an authored local critical-section inventory, not a measurement of all Lock,
Channel or value-source internals. It adds no shared RMW or owner command to the
item path. Credit bounds, extra-grant sizing and waiter scheduling are unchanged.

The refill command exposes either a Receipt or Publication `IValueTaskSource`
view of the **same** preallocated operation. No await-wrapper Task is needed for
the typed conversion. The owner pins before completing the source; GetResult
only unwraps that already-pinned result and consumes the original token. Holding
a completed result must keep its ownership alive across close and early credit.
A first implementation that deferred Begin until GetResult failed the new
held-result/close test deterministically and was replaced before submission.

Thirteen new model checks cover direct held completion, admission/close order,
post-admission cancellation, bounded-queue cancellation, connection FIFO,
stream-blocked-head fairness, alternating receipt/publication views, oversized
borrow/refund, concurrent callers, terminal settlement, differential ledgers and
100,000 actual pooled generations. They supplement the original 41 checks;
neither this count nor the real A/B0 suite proves full production integration.

The `fused-admission-control` job compares exact writer-owned reference
`141608ce1c256c0e92d0c5611491cb24af419cda` with the candidate. Identical Program
and FusedPublicationProbe sources run on each side; only the compile-time choice
of split Acquire/Begin versus direct admission differs. Both sides execute the
same Finish and 64-item peer-update schedule. JIT PGO ON/OFF and NativeAOT run
c1/c8/c32/c128, 16 B/4 KiB, lengths 1/4096, four samples per case and two launches
in AB/BA order. The validator requires all 24 reports / 768 rows and retains
regressions. Length 1 follows the same 64-item warmup: it is a warmed short-tail
control, **not a cold one-item stream** or evidence of cold allocation parity.

This control measures local admission overhead with real pin accounting, not
writer/transport execution. Real SendPump integration, key-only wire updates,
receive batching and complete production latency/fairness/allocation acceptance
are still open. Positive model timing does not change the PR's Draft status.
