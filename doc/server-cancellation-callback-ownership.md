# Physical cancellation tails at a closed server-connection boundary

## Scope and closure

A selected deadline on inbound stream progress can schedule cancellation notification after the
reader continues. Its existing call-state external use delays `ReturnCore`, but not the request
permit or service/module lease. A maintenance ingress fence therefore cannot infer callback
completion from an empty call map and zero ordinary owners alone.

The existing exceptional notification branch now calls an outlined, non-inlined helper. Before
scheduling, the helper publishes an owner in a lazy weak table keyed by the existing physical
connection's `StripedLongMap`. The actual worker task includes both notification and `ReleaseUse`,
including any deferred `ReturnCore` cleanup. Ownership transfers from the reader only after
successful scheduling. The observer completes the cold batch only after that actual task ends.
No connection, call-state, ordinary-request, or healthy-frame field/counter/lookup is added.
Outlining removes the baseline capturing lambda from the healthy stream-progress helper.

The deadline scheduler already sets `_scanRunning` before copying/acquiring call leases and clears
it after callbacks, lease release, and snapshot return. After ingress is sealed and ordinary owners
and the call map drain, a cold join of that existing flag closes the concurrent scan. Future scans
cannot acquire removed generations. The connection then captures the weak sidecar, in that order:
a failing in-flight scan can publish the first sidecar before clearing its flag. Capturing both
drains in parallel would miss that failure and is deliberately not used.

Other cancellation producers remain covered by existing parents: remote Cancel runs on the sole
reader; pre-admission retention notifications run on that reader or under invocation ownership;
parent-token notification is synchronous. Connection/server shutdown invalidates the maintenance
cohort. This is not a census of async-void callbacks or application-detached tasks.

The caller of `DrainPhysicalCancellationCallbacksAsync` must establish ingress/ordinary-owner/map
closure first. Its empty-map check is not itself an admission barrier. Closed connection cleanup
also waits for already-canceled queued admission map entries to disappear before using that API.
Queued wait state is published before its first await, and transfer reuses that state. Map removal
precedes the existing tracked-count decrement under the stripe lock.

## Completion, failures, and shutdown

An ordinary callback exception keeps the existing best-effort notification behavior. A scheduling
or physical resource-cleanup failure instead leaves a sticky cold lifecycle failure. Only a type
name and synthetic diagnostic remain, not the original exception, task, codec, or service graph.
A new failure after a previously completed batch publishes a new faulted completion. Concurrent
workers still have to finish before a faulted batch becomes terminal.

Connection cleanup records a completed drain failure and still attempts independent service,
scheduler, and cancellation-source disposers. Its task ultimately faults. This is best-effort
cleanup, not a successful maintenance/ReferencesReleased certificate.

Global cleanup first joins the existing framework-shutdown task, which closes connection
publishers, then the existing active/retired connection service-cleanup tasks. The public Stop
observer retains its existing budget. Expiry can return a failure while physical cleanup remains
pending; it does not authorize disposing the shared provider/runtime beneath a callback. A
noncooperative synchronous callback can consequently retain resources indefinitely. Pre-existing
synchronous cancellation entry points are not made universally time-bounded by this change.

Standalone module Unregister and per-call service-scope retirement are separate lifetime
boundaries and are not advertised as repaired here. The maintenance coordinator must join this
tail before retiring old services/modules.

## Exceptional representation and cost

This is real new exceptional ownership work, not an allocation-free or intrinsic-only handoff.
A map's first exceptional publication creates a weak-table entry and owner/lock. Each nonoverlapping
batch creates a completion source and its promise task. An incomplete worker also needs an async
observer state-machine task; overlapping workers share the batch promise but each is observed.
The existing callback closure, delegate, worker task, and ExecutionContext behavior remain on the
selected cancellation path. The final cost report measures both healthy progress and deliberately
held callbacks, distinguishing first publication, steady batches, caller-thread allocations, and
object layouts. It must not extrapolate healthy allocation savings to cancellation-heavy traffic.

An alternative is to construct the actual `Task` with `CancellationToken.None` and `DenyChildAttach`,
register it, then `Start(TaskScheduler.Default)`. This can publish before scheduling without a
separate promise for that individual task. It still needs a cold pending-task collection for
concurrent callbacks, a failure/removal observer, atomic failure publication and removal, and a
closed-set snapshot/WhenAll (or equivalent) for a drain. Merely retaining completed tasks until the
next maintenance request would retain original fault graphs indefinitely. Merely removing from
the worker's own finally would precede actual task completion. Start failure must also preserve
the original reader lease and cannot make a previously captured drain hang on an unstarted task.

The batch representation chooses constant-time concurrent capture and prompt removal of original
work/failure references without a per-task collection or snapshot array. Its additional promise
and observer allocation is explicit; the alternative has not been implemented or performance
ranked here. Matching scheduler, ExecutionContext, child-attach, failure, and root-release semantics
would be required before replacing it. Neither option needs healthy-path registry participation.

## Validation

The new focused controls cover original-token deferred and timer callbacks after map/call drain,
publication before callback entry, held ReturnCore, late scan failure after drain starts, sticky
failure after prior success, original-failure payload collection, unrelated-map independence,
ordinary swallowed callback failures, active/retired connection Stop budgets, connection-service
cleanup after a completed physical failure, and an actually queued canceled admission continuation.
The Close/Stop negative controls use only APIs present in the unchanged baseline, without a no-op
compatibility adapter. The maintenance integration owner separately exercises real TLS sessions,
old service retirement, and collectible assembly release while operation/result and proxies remain
alive. Exact test totals and measured costs belong to the sealed validation report, not inference
from source shape.

### First source-bound checkpoint (2026-10-22)

- SDK 10.0.102, Release build: zero warnings/errors.
- New `ServerCancellation*` controls: 16/16 passed with `DOTNET_PROCESSOR_COUNT=1` and
  `TUNIT_MAX_PARALLEL_TESTS=8`.
- Untouched production baseline `03b91814c62d6463b35145a3ba177ed505960251`, with only the same
  fixture/lifecycle/queued test files added: both `ConnectionCloseMustRetainServiceUntilDetachedCallbackReturns`
  variants fail because the connection service is already disposed; both
  `StopBudgetMustNotDisposeRuntimeBeforeActiveOrRetiredCallback` variants fail with
  `ObjectDisposedException` from the already-disposed shared buffer pool. The queued cancellation
  positive control passes on both versions. No new-API shim or baseline production edit is used.
- Existing connection/lifecycle/Stop characterization groups: 7/7, 6/6, and 4/4 passed.
- Reference-boundary, whitespace, diff-whitespace, and maintainability checks pass.

A first version of the active-connection Stop test read `ServiceCleanupTask` before its publication
and saw the property's existing `CompletedTask` placeholder. The final control first joins the
existing `SessionTask` logical-close signal before inspecting the cleanup owner. That initial
fixture failure is not presented as a production defect or discarded as unexplained flakiness.

### Committed-source verification

Production and test seal: `0afa4a3780bd6481b13ee545f3b5d6d397707fde`.
The post-commit Release build again reports zero warnings/errors. Its immutable outputs are:

- Unit tests SHA-256: `cac05c27f8c1304e8d809644d725d4a11de926bc7d2f9d7b95b49ad82d1cc4b3`.
- Server SHA-256: `fee53596b5883519f69c949c9858e1ad5ac35fcda84385e4f6bb0e3e4c67940c`.

The 16 new cases pass on runtime 10.0.2 and independently on official runtime 10.0.13 with
one logical processor and eight parallel tests. The independent post-commit run selected all
16, passed all 16, and skipped zero; binary hashes were unchanged before/after. The earlier
pre-commit-built `879e3db7` test binary is a separate checkpoint, not silently equated with this PE.

Reproduce the focused selection from the repository root, using the chosen runtime host:

```sh
DOTNET_PROCESSOR_COUNT=1 TUNIT_MAX_PARALLEL_TESTS=8 dotnet \
  test/SharpLink.UnitTests/bin/Release/net10.0/SharpLink.UnitTests.dll \
  --treenode-filter '/*/*/ServerCancellation*/*' --progress off --output Detailed
```

Existing post-commit groups also pass, with actual executed totals rather than discovery counts:
`ServerCallDeadlineScheduler*` 9/9, `ServerCallCancellation*` 24/24,
`ServerCallAdmissionTests` 1/1, and `SharpLinkServerInvocationTests` 27/27.
Use the same command with each class pattern substituted for `ServerCancellation*`.

For the direct baseline control, build unchanged `03b91814` production with only
`ServerCallbackOwnershipFixture.cs`, `ServerCancellationCallbackLifecycleTests.cs`, and
`ServerCancellationQueuedShutdownTests.cs` copied from the seal. Select the two named Close/Stop
methods above separately, then the queued-shutdown class. All three files use baseline-existing
production APIs. Their executed outcomes are four expected negative failures and one positive pass.

This checkpoint is scoped source/test evidence. Integrated maintenance authority/failure checks,
full hosted CI, and the separately authored healthy/native/exceptional-cost report remain distinct
gates. Smaller allocation or code size alone is not a throughput claim.
