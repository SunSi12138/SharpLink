# Extension fault-containment matrix

Issue #576 is the release-hardening inventory for application-owned code that SharpLink invokes inside RPC lifecycle boundaries. The matrix is intentionally test-only: it exercises production paths without adding fault-only branches or counters to the runtime.

## Global invariants

Every P0 row must prove the same lifecycle properties after the injected failure:

- one authoritative terminal outcome per logical call / stream lifecycle;
- client pending-request, active-call, and active-stream counts return to zero;
- admission leases report at most once and failed acquisition creates no lease;
- stream producer/consumer ownership is released and pooled state is safe for reuse;
- valid protocol connections remain usable after application-code faults;
- retry / observer hooks do not manufacture extra attempts or replace an already-authoritative result;
- service factory/scope/module ownership is unwound even when cleanup also faults;
- repeated fault/reuse cycles do not accumulate stranded state.

Timing is never used as a correctness oracle. Synchronization uses task completion, bounded polling, or existing deterministic test hooks; sleeps are not part of the P0 fault assertions.

## Inventory

| Boundary | Injection point | P0 ownership / terminal expectation | Coverage |
| --- | --- | --- | --- |
| Client interceptor | synchronous/asynchronous before `next` | no RPC attempt when terminal is not entered; invocation context finishes `Failed`; client counters return to zero | `ExtensionFaultContainmentTests.ClientInterceptorBeforeNextShouldFailOnceReleaseStateAndReuseConnection` |
| Client interceptor | after `next`, nested chain | terminal RPC executes once; unwind fault becomes final local outcome; no duplicate terminal | `ExtensionFaultContainmentTests.ClientInterceptorAfterNextAndNestedChainShouldFailOnceAndRemainReusable` plus existing `RuntimeInterceptorUnwindIntegrationTests` / `RuntimeInterceptorFaultRaceIntegrationTests` |
| Server interceptor | before/after `next` | single mapped remote error, final server invocation context, same valid connection reusable | `ExtensionFaultContainmentTests.ServerInterceptorBeforeAndAfterNextShouldMapOnceAndReuseSameSession` plus existing interceptor regression suites |
| Endpoint admission | `TryAcquire` throws | no admission lease / report is created; failure is local `FailedPrecondition`; next call acquires normally | `ExtensionFaultContainmentTests.AdmissionAcquireFailureShouldNotCreateLeaseOrPoisonNextCall` |
| Endpoint admission | `Report` throws and logger also throws | report ownership is consumed exactly once; observer/logging faults never replace authoritative RPC result | `ExtensionFaultContainmentTests.AdmissionReportAndLoggerFailuresShouldNotReplaceBusinessResultOrDoubleReport` |
| Retry policy | policy throws after retryable attempt | policy failure is `FailedPrecondition`; no second network attempt is manufactured; next logical call works | `ExtensionFaultContainmentTests.RetryPolicyFailureShouldNotManufactureAnotherAttemptAndClientShouldRecover` |
| Codec / serializer | client request serialize | pending/call state released; subsequent call with same codec instance succeeds | `ExtensionFaultContainmentTests.CodecFaultsShouldReleasePendingStateAndKeepProtocolConnectionReusable` |
| Codec / serializer | server request deserialize | malformed application decode terminates once; next codec lifecycle is uncontaminated | same codec matrix test |
| Codec / serializer | server response serialize | response-side application encode fault terminates once; client state drains; next call is usable under the documented connection policy | same codec matrix test |
| Codec / serializer | client response deserialize | response decode fault does not strand pending state; subsequent response decode succeeds | same codec matrix test |
| Client stream producer | `MoveNextAsync` throws | producer cancellation / pending ownership released once; next RPC works | `ExtensionFaultContainmentTests.ClientStreamMoveNextAndDisposeFailuresShouldReleasePendingAndProducerState` plus existing client-stream producer regressions |
| Client stream producer | enumerator `DisposeAsync` throws | cleanup failure cannot strand pending/producer state | same stream matrix test |
| Server stream producer | failure after partial output | stream receives one terminal error; dispatcher/pending ownership drains and client is reusable | `ExtensionFaultContainmentTests.ServerStreamProducerFailureAfterPartialOutputShouldReleaseDispatcherForReuse` |
| Stream consumer abandonment / races | abandon, cancel, stale dispatcher work | generation/lease checks prevent old callbacks from entering a new pooled lease | adopted: `ClientConnectionConsumerAbandonmentTests`, `PooledAsyncStreamDispatcher` unit coverage, `PreAdmissionStreamActivationRaceIntegrationTests` |
| Service factory | per-call factory throws | scope/module acquisition rolls back; mapped error is terminal; next service activation succeeds | `ExtensionFaultContainmentTests.ServiceFactoryCreationAndDisposalFailuresShouldRollbackPerCallOwnership` |
| Service disposal | per-call service `DisposeAsync` throws | service/scope cleanup is attempted deterministically; module lease is released; next call works | same service lifecycle matrix test plus `ServiceLifetimeIntegrationTests` |
| Dynamic module / generation | registration, replacement, quiesce, draining dependency, cleanup | unpublished generations roll back; drained generations release service/codec/scope state; stale generation work cannot re-enter | adopted: `RuntimeAssemblyIntegrationTests.RegistrationAndReplacement.cs`, `RuntimeAssemblyIntegrationTests.ModuleLifecycle.cs`, `RuntimeAssemblyDrainingReferencedDependencyRegressionTests` |
| Metrics observer | `MeterListener` callback throws | observer fault must not replace business result or corrupt subsequent accounting/reuse | `ExtensionFaultContainmentTests.MeterListenerFaultShouldNotReplaceBusinessResultOrPoisonReuse` |
| Tracing observer | `ActivityListener` sampler throws | diagnostics fault must not replace business result or poison client lifecycle | `ExtensionFaultContainmentTests.ActivitySamplerFaultShouldNotReplaceBusinessResultOrPoisonReuse` |
| Generated proxy / dispatcher glue | unary, one-way, client/server/duplex stream | generated bridge keeps one lifecycle owner and delegates terminal selection to the runtime | adopted: `RpcChannelCallShapeIntegrationTests*`, interceptor integration suites, generator tests |
| Admission / flow-control pressure | queue/permit lifecycle, activation races | permits, queue ownership and stream reservation return to baseline on rejection/failure | adopted: `DynamicAdmissionRuntimeResourceRegressionTests`, `PreAdmissionStreamBudgetIntegrationTests`, `PreAdmissionStreamActivationRaceIntegrationTests` |
| Repeated pooled reuse | alternating local extension fault / healthy RPC | 100 cycles, 50 faults + 50 successes, client lifecycle counts zero after every cycle | `ExtensionFaultContainmentTests.RepeatedFaultReuseShouldRemainCleanForOneHundredCycles` |

## CI tiers

P0 is a required deterministic integration gate in `.github/workflows/extension-fault-validation.yml`. It builds `SharpLink.IntegrationTests`, runs only `ExtensionFaultContainmentTests` serially, records exact commit/runtime/OS/CPU provenance, and uploads the log as evidence.

The normal extended PR suite remains the broader P1/release gate and continues to exercise dynamic modules, all transports, generated call shapes, AOT/package smoke, and the existing race/stress suites. Deeper fault/pressure repetitions may be run through a temporary workflow-only commit when needed; temporary trigger changes must be removed from final PR history.

## Tracker policy

If a matrix row exposes a production defect, the tracker must not hide it with a test-side exception or hot-path weakening. The production defect gets a focused child issue/PR with its own regression. The tracker may depend on that fix, but its permanent change remains the inventory, reusable fixtures, and release gate.
