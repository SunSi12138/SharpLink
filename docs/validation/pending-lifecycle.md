# Pending lifecycle validation — #556 + #557 regression evidence

Issues: #556, #557. Original characterization baseline: `dev@acb160faa72a07835b01d049a2fbcf9070b061df`.

#556's pooled-deadline identity fix was merged by #565. #557 now adds the production diagnostic-exception isolation that the same validation surface previously characterized. No public API, wire format, pool topology, second pending table, or global synchronization policy is changed.

## Run and interpretation

```sh
dotnet build test/SharpLink.UnitTests -c Release
# #556: the three deterministic deadline-reuse scenarios retain the correct invariant.
python3 eng/validate-pending-lifecycle.py --mode regression \
  --scenario deadline-response --scenario deadline-cancel --scenario deadline-disconnect
# #557: healthy controls plus throwing pending metrics / admission logger must all preserve lifecycle invariants.
python3 eng/validate-pending-lifecycle.py --mode regression \
  --scenario no-listener --scenario metric-control --scenario metric-minus \
  --scenario metric-plus --scenario logger-control --scenario logger-throw
```

The driver defaults to **regression**, checking correct invariants. Characterization mode is retained only so the original #557 baseline evidence remains reproducible against an unfixed revision. Every scenario records its `invariant` in the evidence directory. Startup, build, filtering, worker exceptions, and unarmed timeouts are infrastructure failures, never positive reproductions.

The evidence workflow archives the exact tested commit and `dotnet --info`. The #557 worker job checks out the PR head because its process-isolated validation is specific to the proposed production fix; PR Fast separately validates the normal merge ref against current `dev`.

## #556 deterministic deadline experiment

Each scenario runs in a fresh filtered TUnit process. A no-op ITimer suppresses autonomous scheduling; the real private scanner is invoked by reflection. A controlled TimeProvider blocks the scanner *inside* IsExpired after the old Deadline struct has been read. A is already expired. The competing response / real CancellationTokenSource.Cancel / FailAllPendingRequests entry point completes A and returns its object; all correctly select DeadlineExceeded for A.

B must rent the **same object reference** with a distinct ID and a future deadline. Releasing the scanner lets the old deadline check finish before it reads the recycled object's identity. Correct behavior is that B remains pending, then completes only from its own response. No sleeps, stress loops, production hooks, pool clearing, or simulated replacement implementation are used to produce the interleaving.

The merged #556 fix keeps the first deadline sample only as a non-authoritative candidate filter. Before timeout is committed, the scanner enters the existing CompletionGate, revalidates the slot reference and captured request ID, rechecks the current deadline, and only then removes the slot.

## #557 pending metric experiments

The listener enables only `SharpLink` / `sharplink.requests.pending` and throws only on the selected delta. Controls run without a listener and with a nonthrowing listener.

The original characterization established two distinct failures:

- throwing on `+1` happened after the PendingCall slot was published but before owner registration and `MarkRegistered`, so terminal cleanup could remove the slot and then wait forever in `WaitUntilRegistered`;
- throwing on `-1` happened after the physical slot was removed and operation completed but before `_activeSlots` capacity was refunded, so later requests saw false `ResourceExhausted`.

The production fix makes the internal pending-occupancy telemetry helper a no-throw diagnostic boundary. The PendingRequestTable sequencing itself is unchanged. Regression mode now requires:

- `metric-plus`: Rent does not expose the listener exception, the published call reaches `_registered=1`, owner registration is committed exactly once, Dispose returns, the outstanding operation reaches normal connection-close completion, and count/capacity return to zero;
- `metric-minus`: the listener exception does not escape response dispatch, the original operation succeeds, physical count and active capacity are both zero, and the capacity is immediately reusable;
- healthy controls remain unchanged.

The +1 worker still writes its pre-Dispose state atomically. On an unfixed baseline the POSIX parent keeps the original 15-second watchdog and only classifies the hang when the exact published/unregistered marker is present. The overall startup bound remains 120 seconds; neither timeout orchestrates the race.

## #557 admission Report and logger experiment

A production-builder-created endpoint client supplies the real private AttemptOutcomeState, instantiated through reflection and attached as the actual pending completion observer. Its real TryAcquire obtains a token from a custom policy whose Report throws. A nonthrowing logger is the control; a logger throwing only while reporting that exact exception is the fault case. Neither fixture starts a connection or replaces the completion state machine.

The policy Report exception remains isolated as before and is still offered to the configured logger. The #557 production fix adds a second narrow boundary around that error log: a logger failure cannot escape into the authoritative pending completion. Regression mode requires policy and logger to each execute exactly once while the original operation completes, the old PendingCall has its Id/Operation cleared for return, pending count/capacity are zero, and a subsequent request succeeds.

This is deliberately narrower than swallowing every `IPendingCallCompletionObserver` exception in `CompleteTakenCall`; internal observer bugs remain visible instead of being reclassified as diagnostics.

## Related codec evidence

`docs/validation/codec-semantics.md` covers DateTime cross-zone semantics (#558) and DateTimeOffset segmented-input measurements (#559). Those remain characterization/measurement work and are not changed by the #557 production fix.
