# Client-stream producer terminal ordering

This test-only correction preserves the existing single-winner policy. It does not
change production code, EOF publication, disposal precedence, timeouts, or task
supervision.

## Source-backed ordering

`ClientConnection.SendClientStreamAsync` publishes clean `StreamComplete` inside
the enumerator's `await using` scope. Enumerator disposal follows that publication.
The server can consume EOF and return its result before disposal finishes.
`InvokeClientStreamingCoreAsync` tracks `ClientStreamingProducer` separately and
awaits the pending response. A later producer failure attempts
`PendingCalls.TryComplete(SendFailure)` but cannot replace an already-selected
reply. This agrees with the secondary-cleanup invariant in
[the extension fault-containment matrix](extension-fault-containment.md).

These producer, pending-table, and supervision semantics predate the current
physical-ownership changes and are unchanged from `13928a35`.

## Exact test coverage

- The original named test still requires failure for MoveNext-only and
  MoveNext-plus-Dispose faults. Those paths do not publish clean EOF. It now names
  each shape in assertion diagnostics and waits for producer supervision to drain.
- `ClientStreamDisposeFailureMustRespectSelectedTerminalAndDrainProducer(false)`
  holds the real server interceptor's successful return, releases the producer's
  disposal fault, and requires the exact original exception to win first.
- The `true` case holds disposal, releases the successful server return, and
  requires result `7` while the disposal task and supervised producer remain
  incomplete. A later disposal fault cannot change that result.
- Both ordered cases release all gates in `finally`, join the public call, the
  interceptor, and producer supervision, assert two MoveNext calls and one
  disposal, and prove same-session reuse. Supervisor registration is observed
  before testing ownership because disposal can suspend before registration.

Only these two explicit disposal-order cases are added to selected test inventory.
The original MoveNext cases remain in their existing single test. No test is
skipped and no watchdog, count, or assertion threshold is relaxed.

## Validation and limits

The historical Extended CI failure at `c6039350` reported only the aggregate loop
assertion, not its iteration. Its exact shape remains unknown. A diagnostic-only
version of that unchanged loop passed ten local launches; this is not a claim
that the historical failure was directly reproduced or identified.

The same authored tests were built against untouched `13928a35` production and
the #933 `c6039350` production tree `c242f33f0041439b176c43460864a888237b79d9`. Both passed
the 20 selected extension-containment cases and five additional two-case ordered
replays on official .NET 10.0.13 with `DOTNET_PROCESSOR_COUNT=1`,
`TUNIT_MAX_PARALLEL_TESTS=8`, and the existing integration runner's
`--maximum-parallel-tests 1` setting. Builds had zero warnings and errors, and the
maintainability debt guard passed without changing its baseline.

Reproduction after a Release integration-project build:

```sh
DOTNET_PROCESSOR_COUNT=1 TUNIT_MAX_PARALLEL_TESTS=8 dotnet \
  test/SharpLink.IntegrationTests/bin/Release/net10.0/SharpLink.IntegrationTests.dll \
  --treenode-filter '/*/*/ExtensionFaultContainment*/*' \
  --maximum-parallel-tests 1
```

This result establishes per-call terminal and producer-cleanup behavior. It does
not certify that a maintenance fence's independent quiescence predicate joins
all physical producers; that ownership boundary requires a separate proof.
