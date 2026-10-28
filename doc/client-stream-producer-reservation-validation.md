# Client producer reservation transfer

## Scope and ownership

Production candidate `8af020df361c5b07f4a225c50fdde95833c762ed` changes the lifetime of
an already acquired connection-selection reservation. There is one reservation per
RPC, not one per stream parameter. No maintenance-specific call predicate, counter,
registry, epoch, declared owner/session field, or stream-element work is introduced.
Generated async-state-machine layout is separately subject to cost verification.

All six production pending-registration sites were audited:

- Unary invocation, retry attempts and health calls release after successful pending
  registration, before request serialization. Their failed-registration catches release.
- One-way client streaming retains the reservation through the whole writer group,
  releasing in its existing terminal `finally`, even if pending ownership ended first.
- Client streaming uses a lexical pre-transfer catch to release acquisition/send/start
  failures, then transfers the reservation to `RunGeneratedClientStreamsAsync` before
  tracking that task. The group's `finally` releases after writer completion, including
  asynchronous enumerator disposal. The earlier candidate's hoisted ownership Boolean
  is absent; early missing-pending completion releases before consuming its operation.
- Shared server/duplex stream preparation releases on failure and hands ownership to
  its successful caller. Server streaming immediately releases before serialization;
  duplex releases only after the complete outgoing writer group.

A synchronous producer can enter user code before returning its task or being
supervised. The reservation already exists during that prefix. A canceled or early
response may remove pending state, but cannot release this producer's reservation.
A failed preparation never assigns its connection to the outer stream invoker, so
only the preparation catch releases it. No supervisor snapshot is used as evidence.

The obsolete internal `IPendingCallOwner.OnPendingCallRegistered` notification is
removed. Its sole production implementation performed the relocated selection release;
there was no other production action. Table capacity publication, `MarkRegistered`,
deadline observation, cancellation checks and completion-owner callbacks remain in their
existing order. A successful Rent returns only after that sequence; its caller releases
or transfers selection ownership exactly once. No empty indirect callback is retained.

Counting/race tests use an explicitly scoped, test-only MeterListener at the existing
`sharplink.requests.pending` +1 event, immediately before `MarkRegistered`. AsyncLocal
identity excludes other scopes and unmarked registrations. The old counters, winner
and minimum-active assertions remain. Listener failures are rethrown by the test wrapper
after scope restoration; that is test-observation behavior, not a claim that obsolete
owner-callback exception timing survives. No production hook or telemetry change was
introduced. Benchmark and standalone validation-probe adaptations are separate review
items and must not silently add listeners to timed current benchmark paths.

This intentionally strengthens planned session-refresh and maintenance retirement:
an uncooperative outgoing `MoveNext` or `DisposeAsync` can hold retirement indefinitely.
Wait cancellation does not release physical ownership. Public client-stream response
completion can still precede producer disposal; the public response contract is unchanged.

## Controlled evidence

The test-only negative is `08255494`, atop `038ce547` with unchanged production tree
`c242f33f0041439b176c43460864a888237b79d9`. Its real TLS test uses two client runtimes
and four physical connections, observes response 7 while disposal is held, then starts
maintenance. Baseline fails the assertion that the old implementation remains Running
and the update incomplete. This is a demonstrated retirement gap, not a new response
completion regression. The same test passes with the corrected candidate: release
permits update, held proxies reach the replacement over unchanged sessions, and the old
implementation ALC is collected with the successful public operation/result retained.

On official .NET 10.0.13, the first corrected candidate evidence was:

- The new held-disposal control and existing abandoned immutable unary serializer
  control both pass, 2/2.
- Complete RuntimeAssemblyIntegrationTests class passes 105/105, zero skipped.
- The initial filter selected 327 SharpLinkClient*, ClientConnection* and architecture
  cases. Its `RequestManagerTests` filename-derived alternative selected no cases; the
  actual class is `PendingRequestTableTests`. The final expanded run below corrects this
  inventory rather than attributing that suite to the initial result.
- Release integration and unit builds have zero warnings and errors. Maintainability,
  project-reference boundaries and diff whitespace checks pass.

An initial unsealed edit accidentally retained the old registration decrement. That
invalid candidate failed the new oracle; it was corrected before the source seal.
Its failed evidence is retained and is not counted as acceptance. Exact cardinality,
additional failure controls and ordinary native/layout/allocation evidence remain
required before adoption; the existing prohibited rejection-handoff fixture was not
run or reproduced.

## Final representation checkpoint

Production source tree `8915e61e5534f6e47a5d679bfbea5c0dcb812eaa` includes the lexical
ownership change and notification removal. With test checkpoint `b9dc4c4f`:

- The expanded client/connection/pending-table/architecture/observer/producer subset
  passes 441/441, zero skips, in 24.843 seconds on official .NET 10.0.13.
- RuntimeAssemblyIntegrationTests passes 105/105, zero skips, in 11.997 seconds. This
  includes held producer disposal through the real four-session update and the unchanged
  abandoned immutable unary serializer control.
- The 60 producer cardinality/failure controls are included in the 441, not additive.
  Their independently preserved untouched-parent result is 26 intended negatives and
  34 positive controls; see `producer-reservation-unit-validation.md`.
- Unit DLL SHA256: `3f938bf267814d3619f66892bdfb07e089ba47d9b5f1be0bcd27c1de8f1c95cb`.
  Integration DLL: `4605faac872cd08a571d18944861009b2e5e1c6dc05d27ff81ebac4d5d805e6d`.
  Both use Client DLL: `6497c07d5f2157b7364f3782c3fb6cbf930b4fa40c939e3d47d6e2472b0905cb`.

The registration witness has a source-counterfactual negative: moving the existing
metric after `MarkRegistered`, with no test changes, fails the preserved assertion
"completion must not precede owner registration" (51 ms, not a timeout). The unchanged
source passes the same test. Parallel/nested scope and observer-failure controls pass.

Cost acceptance is not inferred from correctness. The first candidate added a hoisted
Boolean and retained an empty indirect notification; those adverse measurements remain
recorded. The lexical candidate removes that field and the notification, but initial
native measurements still show additional streaming method-entry zeroing/frame space.
Actual generated state/box sizes remain unchanged in the measured contracts. Final
same-mode native review and explicit acceptance are required before adoption; this
checkpoint makes no zero-cost claim.

## Public source reconstruction

The local pre-repair metadata identifier `038ce547` is source/tree-equivalent to
public commit `c60393508cecd20886e798a474f189f4faf52c62`. For an independently
reproducible negative, create a separate worktree at that public commit and copy
only `test/SharpLink.IntegrationTests/RuntimeAssemblyIntegrationTests.MaintenanceProducers.cs`
from this repaired checkout. Build the integration project with the pinned SDK,
verify `git diff -- src` is empty, and select the held-producer maintenance case.
That public checkpoint is a known-negative source, not a deployment recommendation.
For the 60-case unit comparison, overlay only the six
`ProducerReservationOwnershipTests*.cs` files onto the same public checkpoint.

The final composed source has now executed 2,394 unit cases locally: 2,366 passed
and 28 failed. The exact 28 class/case identities match the preceding c242 run: 26 direct
SocketException permission denials and two expected-exception-type assertions
receiving that denial. They remain failures, not passes. The complete RuntimeAssembly class passed 105/105 on
official .NET 10.0.13, zero skipped, with four relevant DLL hashes stable before
and after execution. Exact final hosted full-suite checks remain mandatory.
