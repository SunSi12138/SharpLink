# Built-in inbound child disposal: focused research follow-up

**Access limitation:** this report summarizes locally verified candidate-only
experiments. The matching source/raw-log archive was not authorized for public
upload and is not included in this branch. See the [public evidence index](../eng/research/cold-control/README.md).
There is no public/CI replay claim for this candidate snapshot.

## Disposition

The archived candidate's `PreAdmissionStreamDispatcher` registry branch does **not** add registration/bookkeeping to correctly awaited, successful built-in inbound early-return consumption. In particular, ordinary unread OneWay completion has a synchronous first child disposal. A custom internal async-disposable test child is not evidence of production-path overhead.

This is not the stronger claim that every RPC with a successful terminal result avoids the registry: a service can start and abandon its own pending `DisposeAsync`, then return success. The mailbox can join that still-pending first disposal, reaching the tracking branch. That is application-owned escaped work, not a framework-invoked operation abandoned by the framework. The same boundary applies to arbitrary detached tasks and unawaited `MoveNextAsync`.

The earlier real-TCP generated outbound deadline counterexample is different and remains decisive: the framework invoked and then abandoned user `MoveNextAsync`/`DisposeAsync`, so physical drain must retain those operations. Nothing in this follow-up removes that obligation.

## Scope and isolation

- This is an isolated snapshot of the **unapplied ownership candidate**, copied at approximately 2026-10-21 16:54:44 UTC, not pristine dev. The tests require candidate-only registry inspection APIs.
- Only the isolated copy's new test file and this note were authored for this follow-up. `production_edits=false` in the raw provenance means this follow-up did not edit its input candidate; it does not mean that candidate equals dev.
- The candidate continued changing after the copy, specifically client interceptor code. All runtime production files used by these controls matched the candidate archive when checked. The source-integrity evidence records that comparison and the later live difference.
- Tests are unit characterizations of the real sealed `PooledAsyncStreamDispatcher<T>` bound to the real `PreAdmissionStreamDispatcher`, not replacement dispatcher fakes. Only existing test hooks and a gated user codec are used to stop relevant execution boundaries. The registry is inspected with test-only reflection.

## Source proof

References are relative to the candidate source snapshot.

1. Successful OneWay cleanup invokes `DrainCompletedOneWayStreams` only after invocation completion: `SharpLink.Server/SharpLinkServer.AdmissionDispatch.cs:382-397` and `SharpLinkServer.OneWayCompletion.cs:19-28`. The helper calls `AbandonExistingRequestStreams`: `SharpLinkServer.PreAdmissionStreams.cs:187-193`. Other client-stream success paths do not automatically imply this mailbox-abandonment path.
2. The mailbox's `TryFinalizeChildDetach` requires no attachment in progress and zero active child dispatches; it removes the child before disposal: `SharpLink.Runtime/PreAdmissionStreamDispatcher.Ownership.cs:28-45`. Admission of another child operation is closed: `PreAdmissionStreamDispatcher.cs:835-881`.
3. The active child lease spans user codec/data dispatch completion, including asynchronous/faulted completions: `PreAdmissionStreamDispatcher.cs:884-914`. It also spans forwarded peer terminal publication: `:857-869,984-990`, with publication completed in `PooledAsyncStreamDispatcher.Terminal.cs:25-69`.
4. Actual generated inbound creation only rents and registers the pooled dispatcher, with no consumer-abandonment callback: `RpcSession.GeneratedServerBridge.cs:5-27`. Reset clears both callback fields: `PooledAsyncStreamDispatcher.cs:225-230`. The sole production callback installation is on client response streams, `SharpLink.Client/SharpLinkClient.Invokers.cs:1003`.

Every possible incomplete built-in disposal path was checked:

- Dispatch drain (`PooledAsyncStreamDispatcher.cs:1013-1018`): impossible for the mailbox's first disposal after its zero-active-child guard.
- Terminal publication (`:912-915`): already completed before the terminal child lease releases.
- Async consumer-abandonment callback (`:1070-1076`): not installed for generated server inbound streams.
- Concurrent disposal (`:875-876,889-891,923-973`): a properly awaited consumer disposal has already published finalization before the handler reports success; a second dispose is synchronous at `:925-928`.
- Producer/consumer-operation and waiter leases (`:1343-1356`) delay pool reuse, not disposal completion. They are not asynchronous user disposal. This distinction matters for physical execution versus pooling claims.

The out-of-scope unawaited-disposal schedule is concrete in source: a consumer calls Dispose while a codec dispatch is active, ignores its pending result, and returns success. Abandon requests detach. The last `ReleaseChildDispatch` signals the asynchronous disposal waiter then immediately attempts detach (`Ownership.cs:19-20`; `PreAdmissionStreamDispatcher.cs:574-582`). If the original disposal has not resumed, the second disposal returns a pending concurrent-disposal task, and `Ownership.cs:77-84` registers it. This is not claimed as a measured supported-success hot-path regression.

## Executable controls

`test/SharpLink.UnitTests/Runtime/BuiltinInboundDisposalResearchTests.cs` contains five deterministic cases:

1. Unread successful OneWay cleanup: typed buffer drained, child detached and pooled synchronously, no per-runtime registry created.
2. Held user codec: child lease remains active and detachment is blocked while codec runs; after release, first disposal is synchronous and no registry is created.
3. Held peer terminal publication: the completion child lease prevents detach until publication finishes; subsequent disposal is synchronous, no registry.
4. Normally awaited early consumer disposal: first dispose is demonstrably incomplete during the held codec dispatch; handler-success cleanup occurs only after awaiting it, and creates no registry.
5. Escaped waiting consumer: an application-started pending MoveNext is deliberately left beyond handler completion. A held consumer continuation prevents pooling, but first disposal and mailbox detach finish synchronously and registry drain reports complete. This is a boundary control, not a supported framework quiescence guarantee for arbitrary escaped application work.

Tests inspect whether the candidate CWT has ever created a registry for this runtime's unique buffer pool, not merely whether the registry is empty at observation time. Entries are not removed by the candidate, so an already-completed registration would still be detected. The reflection check itself initializes the storage table; it is not production overhead.

## Removal assessment

For this built-in generated-inbound, properly awaited ownership scope, the mailbox registry path can be omitted without losing required user-code physical-drain coverage. Existing child leases retain codec/terminal execution until detachment, and normal invocation ownership covers correctly awaited consumer execution. The same-session FIFO fence and ordinary-owner draining remain necessary; pool safety alone is not a global quiescence proof.

Removing mailbox tracking would stop covering arbitrary internal custom asynchronous dispatchers and application-abandoned concurrent disposal. If either is explicitly part of the intended framework contract, that narrower change needs its own ownership mechanism and tests. Do not remove generated-outbound or interceptor abandonment ownership on the basis of these inbound controls.

## Validation

Both Debug and Release builds passed with zero warnings/errors. Each configuration passed ten repeated five-case runs, totaling 100/100 repeated case executions. The initial Debug run also passed 5/5.

The matching raw logs and source-integrity manifest are retained in the local
sealed archive, not publicly linked. The tests require candidate-only registry
APIs and cannot be reproduced by building this unchanged-runtime evidence branch.
No performance, full-suite or remote-CI result is claimed for this follow-up.
