# Extension fault-containment matrix

Issue #576 is the release-hardening inventory for application-owned code executed inside SharpLink RPC lifecycle boundaries. The matrix is test-first: it exercises production paths without adding fault-only state to the runtime. Product defects discovered by the matrix are split into focused child issues/PRs.

## Global invariants

Every applicable row must prove the same ownership properties, not merely an exception type:

- one authoritative terminal winner per logical call / pending request / stream lifecycle;
- pending capacity, client active-call/stream counts, server active-call/admission counts, leases and scopes return to baseline exactly once;
- an observer/reporting/secondary-cleanup failure cannot replace a terminal outcome already selected by the lifecycle owner;
- a valid connection remains reusable after a contained extension fault; when stop/disconnect is the intended winner, shutdown is bounded and leaves no stranded state;
- late producer/observer work cannot enter a new pooled lifecycle;
- no release hardening adds a global hot-path lock or an unjustified per-call allocation.

The matrix uses task gates, framework cancellation tokens, monotonic deadline machinery and existing diagnostics. Wall-clock sleeps are not correctness or race-ordering oracles.

## Boundary inventory

| Extension boundary | Invocation phase | Sync/async | Framework resources already owned | Expected failure surface | Terminal owner | Cleanup owner | Coverage |
| --- | --- | --- | --- | --- | --- | --- | --- |
| Client interceptor | before `next` | sync/async | logical call telemetry/context; no terminal attempt required | local user failure | interceptor pipeline | client call scope | `ExtensionFaultContainmentTests.ClientInterceptorBeforeNextShouldFailOnceReleaseStateAndReuseConnection` |
| Client interceptor | after awaited `next`, nested chain | async | pending/attempt may already have completed | local unwind failure; terminal RPC executes once | interceptor pipeline over completed continuation | pending owner first, interceptor generation/call scope on unwind | `ClientInterceptorAfterNextAndNestedChainShouldFailOnceAndRemainReusable`; `RuntimeInterceptorUnwindIntegrationTests`; `RuntimeInterceptorFaultRaceIntegrationTests` |
| Client interceptor | callback re-enters another generated RPC | async | outer interceptor generation and call scope | both nested and outer RPC complete once; no lock deadlock | each call's independent pending owner | each call scope/generation | `ExtensionFaultContainmentLifecycleRaceTests.ClientInterceptorMayReenterAnotherRpcWithoutDeadlockOrDuplicateTerminal` |
| Server interceptor | before/after `next` | async | server call/admission ownership and invocation context | mapped RPC error; continuation at most once | server invocation/pending response owner | server call/admission owner | `ServerInterceptorBeforeAndAfterNextShouldMapOnceAndReuseSameSession`; existing interceptor suites |
| Server interceptor | re-entry after deadline terminal | async | server interceptor generation retained; deadline already won | later continuation is rejected before downstream side effects | deadline terminal owner | interceptor/server-call owner | `ServerInterceptorDeadlineReentryTests.ExpiredCallShouldNotEnterLaterServerInterceptor` |
| Endpoint admission | `TryAcquire` throws | sync | logical attempt exists, no policy lease yet | local `FailedPrecondition` | client attempt | attempt outcome owner | `AdmissionAcquireFailureShouldNotCreateLeaseOrPoisonNextCall` |
| Endpoint admission | terminal `Report` throws; logger also throws | sync | endpoint attempt has already reached one terminal outcome | authoritative RPC result remains unchanged | pending call | attempt outcome consumes report lease exactly once | `AdmissionReportAndLoggerFailuresShouldNotReplaceBusinessResultOrDoubleReport` |
| Retry policy | policy throws after a retryable attempt | sync | first physical attempt already terminal | `FailedPrecondition`; no manufactured second attempt | logical retry loop | first pending attempt already cleaned | `RetryPolicyFailureShouldNotManufactureAnotherAttemptAndClientShouldRecover` |
| Codec | client request serialize | sync | call/pending registration and writer state according to send path | local serialization failure | pending/send owner | pending slot/writer/send owner | `CodecFaultsShouldReleasePendingStateAndKeepProtocolConnectionReusable` |
| Codec | server request deserialize | sync | server call/admission and request frame | mapped application decode failure | server invocation | request/call/admission owner | same codec matrix test |
| Codec | server response serialize | sync | server call and response writer | structured failure | server invocation | writer/call owner | same codec matrix test |
| Codec | client response deserialize | sync | matched pending slot/operation | response decode failure | pending call | pending owner | same codec matrix test |
| Client-stream enumerator | `MoveNextAsync` throws/faults | async | pending call, producer token, stream/send state | producer failure unless another terminal already won | pending call | producer supervisor/pending/stream owner | `ClientStreamMoveNextAndDisposeFailuresShouldReleasePendingAndProducerState` |
| Client-stream enumerator | `DisposeAsync` throws | async | producer terminal/cleanup in progress | secondary cleanup failure per current call policy; internal resources must still drain | existing call terminal owner | producer supervisor | same P0 stream test |
| Client-stream enumerator | suspended `MoveNextAsync`, caller cancellation wins, then MoveNext + Dispose fault | async race | pending slot, producer cancellation token, stream/send ownership | caller cancellation remains terminal | pending call cancellation claimant | producer supervisor + pending/stream owners | `ExtensionFaultContainmentLifecycleRaceTests.SuspendedMoveNextFaultAfterCallerCancellationShouldPreserveCancelAndReleaseState` |
| Client-stream enumerator | suspended `MoveNextAsync`, deadline wins, then MoveNext + Dispose fault | async race | same | `DeadlineExceeded` remains terminal | deadline claimant | producer supervisor + pending/stream owners | `ExtensionFaultContainmentLifecycleRaceTests.SuspendedMoveNextFaultAfterDeadlineShouldPreserveDeadlineAndReleaseState`; `ClientStreamProducerDeadlineTests.ExpiredCallShouldNotReenterProducerBeforeDeadlineTimerRuns` |
| Client-stream enumerator | suspended `MoveNextAsync`, server stop/disconnect wins, then MoveNext + Dispose fault | async race | same plus physical connection | stop/connection terminal remains public result; shutdown bounded | connection/stop claimant | connection + producer supervisor + pending/stream owners | `ExtensionFaultContainmentLifecycleRaceTests.SuspendedMoveNextFaultDuringServerStopShouldNotStrandCallOrProducerState` |
| Server-stream producer | failure after partial output | async | dispatcher/pending/stream generation | one structured stream terminal | server stream/pending owner | dispatcher/stream owner | `ServerStreamProducerFailureAfterPartialOutputShouldReleaseDispatcherForReuse` |
| Stream consumer / pooled dispatcher | abandon/cancel/stale work | async race | dispatcher lease and stream slot | old generation work rejected | stream terminal CAS/generation | dispatcher/stream owner | `ClientConnectionConsumerAbandonmentTests`; `PooledAsyncStreamDispatcher` tests; `PreAdmissionStreamActivationRaceIntegrationTests` |
| Service factory / DI activation | per-call creation throws | sync | scope/module acquisition may have started | mapped `Internal`; no active generation leak | server invocation | `ServiceRegistration` scope/module rollback | `ServiceFactoryCreationAndDisposalFailuresShouldRollbackPerCallOwnership` |
| Service disposal | per-call `DisposeAsync` throws | async | service call already produced primary outcome | cleanup failure follows explicit precedence without skipping scope/module cleanup | server invocation/lease policy | `ServiceLease` | same service matrix test |
| Service disposal vs stop | connection-owned disposal blocks while server stops | async race | connection service/scope + server stop ownership | Stop waits for owned cleanup; dispose exactly once | server stop | connection service owner | `ServiceLifetimeIntegrationTests.ServerStopShouldJoinConnectionServiceCleanup` |
| Cancellation callback on framework producer token | callback throws while pending completion cancels producer | sync callback | pending slot and producer token | callback failure is observed diagnostically; pending terminal still completes and releases slot | pending call | pending owner | `PendingRequestTableTests.ThrowingProducerCancellationCallbackShouldNotStrandCompletion` |
| Metrics observer | call-start `MeterListener` callback throws | sync | logical call scope is being created | diagnostic fault isolated | RPC lifecycle | telemetry no-throw boundary | `ExtensionFaultContainmentTests.MeterListenerFaultShouldNotReplaceBusinessResultOrPoisonReuse` |
| Metrics observer | client `calls.completed` callback throws after successful result | sync completion | authoritative response has already arrived; client call scope is unwinding | successful business result preserved; same session reusable | completed pending call | telemetry completion + client call scope | `TelemetryObserverIsolationIntegrationTests.ThrowingCompletionMeterListenerShouldNotReplaceResultOrPoisonSameSession` |
| Metrics observer | client `calls.failed` callback throws after remote/business failure | sync completion | authoritative RPC error already selected | original structured error preserved; same session reusable | failed pending call | telemetry completion + client call scope | `TelemetryObserverIsolationIntegrationTests.ThrowingFailedMeterListenerShouldNotReplaceAuthoritativeErrorOrPoisonSameSession` |
| Metrics observer | completion callback re-enters client lifecycle API | sync re-entry | call terminal selected; telemetry completion is on unwind path | no deadlock, duplicate terminal or poisoned generation | completed pending call | telemetry/call owner | `ExtensionFaultContainmentLifecycleRaceTests.CompletionMetricCallbackMayReenterClientLifecycleApiWithoutDeadlock` |
| Tracing observer | Activity sampler / ActivityStarted throws | sync start | logical call/attempt creation | diagnostic fault isolated and ambient parent restored | RPC lifecycle | telemetry no-throw boundary | `ExtensionFaultContainmentTests.ActivitySamplerFaultShouldNotReplaceBusinessResultOrPoisonReuse`; `SharpLinkTelemetryObserverIsolationTests` |
| Tracing observer | client `ActivityStopped` throws during completion | sync completion | authoritative result already selected | result preserved, same session reusable, ambient Activity restored | completed pending call | telemetry completion | `TelemetryObserverIsolationIntegrationTests.ThrowingActivityStoppedCallbackShouldNotReplaceResultOrPoisonSameSession`; unit isolation suite |
| Dynamic module / generated generation | register/replace/quiesce/drain/cleanup | sync + async race | manifest/module/service/codec generation leases | unpublished generation rolls back; draining generation releases after users leave | module generation state | module/runtime registry owners | `RuntimeAssemblyIntegrationTests.RegistrationAndReplacement.cs`; `RuntimeAssemblyIntegrationTests.ModuleLifecycle.cs`; `RuntimeAssemblyDrainingReferencedDependencyRegressionTests` |
| Admission / flow-control pressure | queue/permit/rejection/stream activation | async race | bounded permits, queued-call bytes, stream reservation | reject/cancel/terminal without permit or queue leak | admission terminal owner | admission/stream owners | `DynamicAdmissionRuntimeResourceRegressionTests`; `PreAdmissionStreamBudgetIntegrationTests`; `PreAdmissionStreamActivationRaceIntegrationTests` |
| Repeated pooled reuse | alternating interceptor fault / healthy call | repeated | same connection/pending capacity/generation | 50 injected failures + 50 successes; zero lifecycle counts after every cycle | per-call terminal owner | normal owners | `RepeatedFaultReuseShouldRemainCleanForOneHundredCycles` |

## Lifecycle-sensitive P1-targeted gates

The permanent workflow runs `ExtensionFaultContainmentLifecycleRaceTests` separately from the normal P0 rows so lifecycle races stay explicit in evidence. The required dimensions map as follows:

- fault vs cancel: suspended client-stream `MoveNextAsync`, framework producer token cancellation, late MoveNext fault and throwing `DisposeAsync`;
- fault vs deadline: the same dual fault after deadline wins, plus the fake-time `ClientStreamProducerDeadlineTests` re-entry barrier;
- fault vs disconnect/stop: suspended producer while `Server.StopAsync(TimeSpan.Zero)` closes the session, followed by late MoveNext/Dispose faults;
- fault vs Dispose: the three race rows deliberately make producer `DisposeAsync` fault after cancellation/deadline/stop has already claimed the lifecycle;
- targeted reentrancy/deadlock: a client interceptor performs one guarded nested RPC; a client completion Meter callback synchronously calls `ReplaceInterceptors`; the existing server deadline re-entry test calls `next` after the deadline has already won.

Each new race has a three-second supervision bound. Cancel/deadline rows additionally prove client pending/call/stream plus server call/admission counts return to zero and then immediately reuse the same physical session. The stop row proves both sides return to baseline and shutdown leaves the server non-Ready.

## Cross-check with #81 and #86

#81 established the ownership rules this matrix enforces: every counter/lease has one scope and terminal owner; response/cancel/deadline/disconnect/GoAway races converge on one winner; counters return to zero without underflow/double release; shutdown supervises owned work; monotonic time is used for deadlines; and hot paths do not gain a global lock or unexplained per-call allocation. The P0/P1-targeted assertions above are the extension-boundary regression layer for those rules.

#86 is the broader release gate. This tracker does not replace it: after the focused extension matrix passes, PR Fast/CodeQL and the repository PR Extended gate remain required for the affected stack so Release build, full Integration Tests, package/NativeAOT and other release smoke continue to run. The telemetry child fix also retains its deterministic allocation gate.

## CI and evidence

`.github/workflows/extension-fault-validation.yml` is a permanent PR gate. Its path filter includes both `SharpLinkTelemetry.cs` and `SharpLinkTelemetry.ObserverIsolation.cs`, direct client lifecycle owners `SharpLinkClient.Telemetry.cs` and `PendingRequestTable.cs`, plus the tracker/telemetry integration fixtures. It records exact checkout SHA, .NET/runtime/OS/CPU provenance, runs P0 and P1-targeted suites serially, and uploads the logs plus a machine-readable summary.

## Tracker policy

If a matrix row exposes a production defect, the tracker must not hide it with a test-side catch or hot-path weakening. The defect gets a focused child issue/PR with its own regression and is back-linked here. #581 / PR #584 is the first such split: telemetry observer exceptions could replace authoritative RPC outcomes and are fixed independently of this test/inventory PR.
