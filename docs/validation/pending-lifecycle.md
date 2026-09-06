# Pending lifecycle validation — tests only

Issues: #556, #557. Baseline: `dev@acb160faa72a07835b01d049a2fbcf9070b061df`.

No production source, public API, wire format, or policy is changed. This PR does not close the issues and must not be described as a fix.

## Run

```sh
dotnet build test/SharpLink.UnitTests -c Release
python3 eng/validate-pending-lifecycle.py --mode characterize
python3 eng/validate-pending-lifecycle.py --mode regression
```

The default is **regression**, checking correct invariants. The CI step explicitly selects **characterize**: green means healthy controls pass AND each precise suspected baseline failure is observed. It does NOT mean the invariants pass. Every scenario's `invariant` is recorded in `artifacts/validation/pending/summary.json`. Startup, build, filtering, worker exceptions, and unarmed timeouts are infrastructure failures, never positive reproductions. The characterization gate will fail after a fix until its expectations are intentionally updated.

## Deterministic deadline experiment

Each scenario runs in a fresh filtered TUnit process, so no unrelated test can access the static PendingCall queue or listener. A no-op ITimer suppresses autonomous scheduling; the real private scanner is invoked by reflection. A controlled TimeProvider blocks the scanner *inside* IsExpired after the old Deadline struct has been read. A is already expired. The competing response / real CancellationTokenSource.Cancel / FailAllPendingRequests entry point completes A and returns its object; all correctly select DeadlineExceeded for A. Therefore the entry-point name must not be mistaken for A's authoritative completion reason.

B must rent the **same object reference** with a distinct ID and a future deadline. Releasing the scanner lets the old deadline check finish before it reads the recycled object's ID. The assertions distinguish fixture setup failure from a successful premature timeout. Correct behavior is that B remains pending, then completes only from its own response. No sleeps, stress loops, production hooks, pool clearing, or simulated replacement implementation are used to produce the interleaving.

## Diagnostic experiments

The listener enables only `SharpLink` / `sharplink.requests.pending` and throws only on the selected delta. Controls run without a listener and with a nonthrowing listener. The -1 experiment records physical slot count, active capacity, the first operation's outcome and whether a later request succeeds.

For +1, the child atomically writes the exact published-but-unregistered state immediately before Dispose. The POSIX parent kills the entire process group if Dispose remains blocked for 15 seconds. A timeout without this exact marker is a harness failure. The overall startup bound is 120 seconds. Neither deadline is used to orchestrate the race. The explicit TUnit worker must not be run directly without an external watchdog.

## Scope still to add / validate

Admission Report plus throwing logger, DateTime cross-zone semantics (#558), and DateTimeOffset segmented-input measurements (#559) are tracked separately. No execution conclusion is claimed by merely adding this document. CI run links and observed results belong in the PR conversation after execution.
