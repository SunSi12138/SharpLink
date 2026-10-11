# Deterministic send-state lease capture race

Status when authored: **source audit and overlay generation only; .NET red/green
execution remains pending**. These materials are correctness diagnostics and
carry no B2/B3 or performance claim.

The audited baseline is `f8ba89121c87a6410a0139c44d7db30eab877f41`. In
`StreamFlowController.AcquireSendCreditAsync`, the original code captures a
`SendState` reference under `_gate`, releases that gate, then reads `state.Lease`
while preparing the contended-method arguments. Another thread can retire and
rent the same pooled object during this interval. This experiment checks the
actual public method, with a deterministic pause at that real interval.

## Two arms

```bash
python3 <materials>/apply.py \
  --root <disposable-checkout> \
  --mode original \
  --record <artifacts>/original-source.json

python3 <materials>/apply.py \
  --root <another-disposable-checkout> \
  --mode fixed \
  --record <artifacts>/fixed-source.json
```

Start each checkout from the exact baseline for the primary experiment. The
script additionally recognizes the exact D-only split-gates controller digest so
that a later, explicitly separate diagnostic control is possible. Any unknown or
already-instrumented source is rejected. The script copies the same TUnit source
into `test/SharpLink.UnitTests/Runtime/StreamFlowControllerLeaseRaceTests.cs` and
records source/test hashes. Both arms have identical diagnostic code:

- An instance callback invoked immediately after the initial `_gate` exits and
  before the contended-call arguments are evaluated.
- A read-only, `_gate`-protected identity/lease/credit snapshot getter.

The fixed arm changes only the snapshot timing: a local `expectedLease` is assigned
inside the original gate, then passed after the pause. `old-vs-fixed.patch` shows
the entire semantic difference. The #840 WINDOW_UPDATE arithmetic, unsent refund
arithmetic, pool algorithm and public call path stay unchanged. The diagnostic
hook/getter must not be carried into a production patch or performance binary.

## Deterministic interleaving

The principal case uses stream window 16, connection window 32 and legal frame
maximum 128 solely as a small unit-test fixture:

1. Public `TryAcquireSendCredit(1, 0, 16)` exhausts stream 1's credit.
2. Another thread enters public `AcquireSendCreditAsync(1, 0, 8, token)` and pauses
   immediately after its unsuccessful initial gated probe.
3. The test calls public `ReturnUnsentCredit(1, 0, 16)` and
   `CompleteSendStream(1, 0)`, placing the fully settled object in the actual pool.
4. A new public acquire on the same key reserves 8 bytes. Snapshots must prove
   object reference equality and an increased lease. The new state has 8 bytes
   left; connection credit is 24.
5. Release the old public call. Original code is expected to read the replacement
   lease and incorrectly reserve another 8 bytes, leaving state/connection credit
   0/16. Fixed code must reject the stale generation with ConnectionClosed and
   leave 8/24 unchanged.

No private contended method is called directly and no reflection is used. The
paused operation uses a dedicated long-running test task to avoid depending on
ThreadPool injection while its synchronous diagnostic callback waits. Every
pause and test observation has the same fixed five-second bound in both arms;
existing tests, production timeouts, workload concurrency and options are not
relaxed.

## Eight focused cases

TUnit class: `SharpLink.UnitTests.Runtime.StreamFlowControllerLeaseRaceTests`.

| Case | Original expected | Fixed expected |
|---|---|---|
| Same key, same pooled object, new lease | Fail: stale acquire accepted | Pass: reject without credit mutation |
| Different key, same pooled object, new lease | Pass: reject | Pass: reject |
| Same generation, ordinary credit return | Pass: admit exactly 8 bytes | Pass: admit exactly 8 bytes |
| Cancellation after same-key reuse | Pass: exact cancellation token | Pass: exact cancellation token |
| Connection Complete during pause | Pass: original exception reference | Pass: original exception reference |
| Stream Complete with no replacement | Pass: reject, no state recreation | Pass: reject, no state recreation |
| Stream Abort during pause | Pass: original exception reference | Pass: original exception reference |
| Missing-state caller, tombstone capacity returns | Pass: create/rent/admit | Pass: create/rent/admit |

Preserve both test exits, counts, stdout, complete exceptions, source records and
compiled artifacts. A nonzero original exit alone is insufficient: confirm the
specific same-key case failed with `originalAccepted=True`, identical object,
increased lease and 0/16 balances, while all seven controls passed. Then require
the same case and controls to pass on the fixed arm, followed by repeated focused
and complete existing suites without retrying away failed runs.

## Already performed static checks

`overlay-static-validation.json` records generation of both arms from the exact
f8 source. Python syntax and source anchors were checked. Reapplying an overlay
to instrumented source was rejected; corresponding stderr is retained. Generated
source texts and the arm-to-arm diff are provided for review. These checks do not
establish that the C# compiles or that the race has been reproduced.
