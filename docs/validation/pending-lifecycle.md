# Pending lifecycle validation — #556 fix + #557 evidence

Issues: #556, #557. Characterization baseline: `dev@acb160faa72a07835b01d049a2fbcf9070b061df`.

#556 now carries its minimal production fix in this validation PR. #557 remains characterization/evidence only. No public API, wire format, pool topology, or global synchronization policy is changed; neither issue is auto-closed by this PR.

## Run and interpretation

```sh
dotnet build test/SharpLink.UnitTests -c Release
# #556: the three deterministic deadline-reuse scenarios must now satisfy the correct invariant.
python3 eng/validate-pending-lifecycle.py --mode regression \
  --scenario deadline-response --scenario deadline-cancel --scenario deadline-disconnect
# #557 and controls remain characterization evidence in this PR.
python3 eng/validate-pending-lifecycle.py --mode characterize \
  --scenario no-listener --scenario metric-control --scenario metric-minus \
  --scenario metric-plus --scenario logger-control --scenario logger-throw
```

The default is **regression**, checking correct invariants. CI now runs #556's three deadline scenarios in regression mode while the remaining #557 scenarios stay in characterize mode. Every scenario's `invariant` is recorded in its evidence directory. Startup, build, filtering, worker exceptions, and unarmed timeouts are infrastructure failures, never positive reproductions.

The evidence workflow checks out the PR head SHA, not the moving merge ref, and archives `commit.txt` and `dotnet-info.txt`. PR Fast separately checks the normal merge ref against current dev. Do not attribute head-baseline experiments to a newer untested production commit.

## Deterministic deadline experiment

Each scenario runs in a fresh filtered TUnit process, so no unrelated test can access the static PendingCall queue or listener. A no-op ITimer suppresses autonomous scheduling; the real private scanner is invoked by reflection. A controlled TimeProvider blocks the scanner *inside* IsExpired after the old Deadline struct has been read. A is already expired. The competing response / real CancellationTokenSource.Cancel / FailAllPendingRequests entry point completes A and returns its object; all correctly select DeadlineExceeded for A. Therefore the entry-point name must not be mistaken for A's authoritative completion reason.

B must rent the **same object reference** with a distinct ID and a future deadline. Releasing the scanner lets the old deadline check finish before it reads the recycled object's ID. The assertions distinguish fixture setup failure from a successful premature timeout. Correct behavior is that B remains pending, then completes only from its own response. No sleeps, stress loops, production hooks, pool clearing, or simulated replacement implementation are used to produce the interleaving.

The #556 fix intentionally keeps the first deadline sample as a non-authoritative candidate filter, so this deterministic barrier still forces the original A -> B object-reuse interleaving. Before a timeout is actually committed, the scanner enters the existing CompletionGate, revalidates the slot reference and captured request ID, rechecks the current deadline, and only then removes the slot. Therefore the same fixture now proves the ABA is rejected: B stays pending and completes from its own response. The driver still rejects unarmed timeouts.

## Metric experiments

The listener enables only `SharpLink` / `sharplink.requests.pending` and throws only on the selected delta. Controls run without a listener and with a nonthrowing listener. The -1 experiment records physical slot count, active capacity, the first operation's outcome and whether a later request succeeds.

For +1, the child atomically writes the exact published-but-unregistered state immediately before Dispose. The POSIX parent kills the entire process group if Dispose remains blocked for 15 seconds. A timeout without this exact marker is a harness failure. The overall startup bound is 120 seconds. Neither deadline is used to orchestrate the race. The explicit TUnit worker must not be run directly without an external watchdog.

## Admission Report and logger experiment

A production-builder-created endpoint client supplies the real private AttemptOutcomeState, instantiated through reflection and attached as the actual pending completion observer. Its real TryAcquire obtains a token from a custom policy whose Report throws. A nonthrowing logger is the control; a logger throwing only while reporting that exact exception is the fault case. Neither fixture starts a connection or replaces the completion state machine.

After real response dispatch, inspect operation completion and the old PendingCall's return-cleared Id/Operation **before** the next rent, plus capacity and a subsequent healthy request. An orphaned operation is not awaited without a bound: the isolated worker reports its state and exits. The later production decision may isolate diagnostics; this test does not implement that decision.

## Related codec evidence

`docs/validation/codec-semantics.md` covers DateTime cross-zone semantics (#558) and DateTimeOffset segmented-input measurements (#559). CI run links and observed results are recorded in the PR and issue conversations, not assumed merely because a probe was added.
