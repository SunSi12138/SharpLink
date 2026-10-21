# Same-session cold quiescence: test-only research evidence

Local historical log paths below are provenance, not public attachments. The raw
archive was not authorized for public upload. The originally planned test source
is independently included in this branch and rerun by its unchanged-runtime CI;
see the [public evidence index](../eng/research/cold-control/README.md).

## Verdict

A normal-FIFO delimiter on the same physical `RpcSession` is a usable parsing
boundary, but **existing logical request/admission ownership is not sufficient to
prove physical execution quiescence**. Do not publish a production update API that
claims no overlap with old application execution based only on the existing drain
counters plus this delimiter.

The decisive counterexample is generated server-stream deadline abandonment.
After a deadline wins, all client reservations/pending/untracked owners, all live
and retired admission-program uses, admission queue/permit accounting, server
pending acquisitions/global active calls, and the server request map can be zero
while the old iterator's `MoveNextAsync` and asynchronous `DisposeAsync` are still
running. The same TCP session remains alive and answers the FIFO delimiter.

This is an ownership gap, not a failure of FIFO ordering. The tests deliberately
assert that the counterexample occurs; a passing research test does not mean the
candidate quiescence algorithm is safe.

## Baseline and scope

- Baseline commit: `13928a35df4bff3cd99b14bd1df131aa3eb3bcdb`.
- .NET SDK: pinned `10.0.102`; Linux x64; real loopback TCP.
- One fixed endpoint, exactly one physical connection; no concurrent external
  configuration publisher or noncooperating peer.
- Production files under `src/` are unchanged. The original probe added only integration-test files
  and this research document. The combined evidence branch also includes models,
  unit characterizations, validation workflows and the separate API-tool correction;
  its production runtime source remains unchanged.
- This is not a production pause API, an incompatible-codec proof, or an exact
  dynamic-registration/proxy-generation cutover implementation.

## Test-only coordinator

`ColdSessionFenceResearchTests.Harness.cs` uses reflection solely to expose the
question without adding production hot-path work:

1. A dedicated coordinator thread holds the existing fixed-endpoint pool gate,
   freezing new Ready publication for the bounded test phase.
2. It temporarily closes the existing selection-eligibility indirection using a
   suspended redirect with a null target. This is explicitly a research-only
   state, not a supported session-refresh operation. `ClientConnection` and its
   `RpcSession` remain Ready, so pre-cut calls and stream progress can finish.
3. It waits selection reservations plus existing pending/untracked call owners.
4. It registers a test health pending operation directly on the captured
   `ClientConnection` and emits normal-queue `HealthCheck` on that exact session.
   It does not use pool-selected `CheckHealthAsync`, Ping, a new connection, a
   numeric request-id cutoff, or a priority control-frame ordering assumption.
5. The external coordinator waits all program uses in the existing kernel
   registry, admission queue/permits, pending server acquisitions and global
   active calls. It never blocks the server reader waiting for drain.
6. It restores the temporary eligibility state and verifies an ordinary unary RPC
   succeeds through the identical `ClientConnection` and `RpcSession` objects.

Reusing `ClientConnection.Draining` is not a reversible-pause implementation:
normal pending completion invokes `RetireDrainingConnectionIfIdle`, and
`MarkDraining` also drains the session. No production behavior was changed to work
around this fact.

## Deterministic probes

The suite contains thirteen parameterized cases:

| Cases | Observation |
| --- | --- |
| 2 delayed async OneWay, admission off/on | Client send completion and FIFO fence do not complete server execution. Existing server permit/program use correctly remains held. |
| 1 queued retired generation | Current-program uses and global active/pending server calls are all zero while the retired program still owns a queued request. All-generation inspection is necessary. |
| 1 selected-but-unregistered unary | A selection reservation exists while pending/untracked count is zero. The pre-cut call must drain before the delimiter. |
| 1 untracked OneWay serialization | A gated request codec has transferred its reservation into auxiliary ownership but has no pending entry. Pending/reservation-only inspection is insufficient. |
| 4 normal server-stream/duplex cleanup, admission off/on | Stream progress continues after an exploratory early delimiter on the paused session. Existing ownership includes normal asynchronous iterator cleanup. |
| 1 retired-program physical reclamation | Existing test hook holds physical admission-state reclamation after request-map removal and generation-use release. The final server request permit still prevents false zero. |
| 2 deadline abandonment, admission off/on | **Counterexample:** all existing drain observations are zero while a custom iterator's old MoveNext and Dispose both remain held. |
| 1 compiler iterator deadline abandonment | **Counterexample:** concurrent Dispose throws NotSupportedException; eventual MoveNext returns true; framework never retries Dispose, leaving the iterator finally unentered. |

The streaming positive controls intentionally emit an early exploratory delimiter
while a stream is still pending to establish that reader progress remains
possible. They then perform the full client drain/fence/server-drain sequence.
They do not mislabel the early delimiter as complete quiescence.

## Deadline counterexample and compiler iterator cleanup

The final deadline probes deterministically isolate the server's deadline from
an earlier client deadline/Cancel contender. The client uses an injected frozen
monotonic clock and immediate frame flushing. After the server deadline has
selected its terminal and logical ownership drains, the test advances the client
clock so the client completes with DeadlineExceeded; it then runs the complete
client-drain/same-session-fence/server-observation sequence. Neither iterator gate
is released until after the false-quiescence observation. This changes only test
clock coordination, not the production dispatch path.

Initial wall-clock-only repeats sometimes selected the client-cancellation path,
which retains disposal ownership instead. Those exploratory logs remain in
`repeat-debug-unisolated/` and `repeat-release-unisolated/`. A safe barrier must
handle both paths; the existence of the server-deadline abandonment path is enough
to refute the universal physical-quiescence claim.

`RpcSession.GeneratedServerBridge.cs` is unchanged:

- Lines 64–73 race an incomplete MoveNext against the RPC deadline, then hand it
  to `ObserveAbandonedGeneratedMoveNextAsync` without retaining the logical call.
- Lines 123–138 invoke Dispose even if MoveNext is still outstanding; after a
  deadline win, incomplete disposal is observed separately rather than awaited.
- Lines 209–224 only observe completion/failure. The MoveNext observer does not
  advance the iterator again or retry disposal.

The custom enumerator in the test tolerates independent concurrent disposal,
which isolates the two outstanding physical lifetimes. After releasing MoveNext,
its completion is observed while async disposal remains gated and all counters
remain zero. Releasing disposal finally completes physical cleanup.

A separate wrapper around an ordinary compiler-generated async iterator records
actual disposal attempts and the second MoveNext result. The framework's sole
concurrent Dispose attempt throws `NotSupportedException`. When the test releases
the old MoveNext, it returns **true**, leaving the compiler iterator suspended at
a yield. At the next same-session delimiter, disposal count is still one, the
finally block has not started, and the existing drain predicate is true. Only an
explicit second Dispose performed by the test enters the asynchronous finally.
This final cleanup is test hygiene, not behavior provided by the framework.

The original failed exploratory test is preserved in
`artifacts/cold-fence/abandonment-debug.log`: both admission variants reached the
false-zero assertion, then timed out waiting for compiler finally to start. The
final `CompilerIteratorConcurrentDisposalIsNotRetriedAfterAbandonedMoveNextYieldsTrue`
case replaces that timeout with explicit observable characterization.

## Reproduction

From this checkout with the SDK selected by `global.json` installed, run `dotnet restore Sharplink.slnx` first:

```sh
dotnet build test/SharpLink.IntegrationTests/SharpLink.IntegrationTests.csproj \
  -c Debug -m:1 -nr:false -p:UseSharedCompilation=false --no-restore

dotnet test/SharpLink.IntegrationTests/bin/Debug/net10.0/SharpLink.IntegrationTests.dll \
  --treenode-filter '/*/*/ColdSessionFenceResearchTests/*' \
  --maximum-parallel-tests 1 --timeout 120s --minimum-expected-tests 13 \
  --output Detailed --no-ansi --progress off
```

Repeat with `Release` in both commands. The single-node/no-node-reuse/no-shared-
compiler flags avoid Unix MSBuild/compiler IPC in this execution environment.

## Evidence files and validation

The canonical final run logs are `artifacts/cold-fence/build.log`,
`build-release.log`, `probes-debug.log` and `probes-release.log`. Both configurations
build with zero warnings and zero errors and pass all thirteen research cases.

The final isolated fixture passed ten complete repeated runs in Debug and ten in
Release: **260/260 repeated case executions**. The focused preexisting suites
`DynamicAdmissionGenerationTests` (15), `OneWayInboundDrainIntegrationTests` (2),
and `RuntimeInterceptorUnwindIntegrationTests` (3) passed **20/20** additional
Release cases. Logs are recorded under `artifacts/cold-fence/`;
`validation-summary.json` records every completed run and total.
The project-reference and maintainability guards have separate logs there.
`git diff --exit-code -- src` verifies no tracked production source changed.

This work does not claim a full repository test, performance, mobile or AOT pass.
Since production source is byte-for-byte unchanged, it introduces no production
normal-RPC counter, lookup, branch, lock, allocation or new wire frame.

## Implication for further design

Any physical-quiescence guarantee must additionally own abandoned iterator,
disposal and send work through physical completion, or explicitly weaken its
contract to logical RPC completion. It also needs an actual reversible pause and
publication protocol across every relevant client/server participant. Existing
admission uses and request permits remain useful, but the deadline exceptions
prevent treating them as a complete physical ownership ledger.

No conclusion here establishes safe incompatible serialization, arbitrary plugin
unload, old-proxy ABA prevention, or exact registration identity. Those require
separate generation and lifecycle proofs.
