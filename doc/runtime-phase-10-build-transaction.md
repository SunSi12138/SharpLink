# Runtime Architecture Phase 10: Synchronous build transactions

`SynchronousBuildTransaction` is the construction-only ownership boundary for Client and Server
builder materialization. A builder registers a resource when Build takes framework ownership, then
commits only after its final client, coordinator, prepared cluster, or server has been constructed.
An unsuccessful materialization releases resources in strict reverse registration order.

## Ownership and terminal behavior

| Materialization path | Registration order | Successful terminal behavior | Failure terminal behavior |
|---|---|---|---|
| `SharpLinkRuntimeContextBuilder.Build` | RuntimeContext | caller receives the fully constructed Context; transaction commits | constructor-local generated registration cleanup runs first; the transaction preserves the primary failure |
| `SharpClientBuilder` build-plan materialization | direct transport or endpoint resolver, RuntimeContext, endpoint factory/factories | final `SharpLinkClient` receives ownership; transaction commits | factory/factories, RuntimeContext, then direct transport or resolver are released in reverse registration order |
| `SharpClientBuilder.DisposeUnbuiltResources` | unbuilt direct transport and resolver | not applicable | releases each distinct builder-owned resource once; this is a cold cleanup path |
| `SharpLinkMultiClusterClientBuilder.Build` | each completed child Client | final coordinator receives all children; transaction commits | completed children release in reverse cluster-materialization order |
| `PrepareRuntimeCluster` | completed candidate child Client | returned `SharpLinkPreparedCluster` transfers the child to its caller | the transaction owns any later failure; with immutable valid manifests, route type/ID conflicts are detected by child construction before a child is returned |
| `PrepareReplacementCluster` | completed replacement child Client | returned `SharpLinkPreparedCluster` transfers the child to its caller | after child construction the current path only packages a slot and an empty frozen route map; there is no separately injectable normal failure point |
| `SharpLinkServerBuilder.Build` | listener, RuntimeContext, framework ServiceProvider or tracked caller provider, admission controller, each `ServiceRegistration` | final `SharpLinkServer` receives framework-owned resources; transaction commits | registrations reverse, then admission/provider/RuntimeContext/listener; caller provider is tracked without a cleanup action |

The transaction uses reference identity, not `Equals` or `GetHashCode`; attempting to register the
same object twice is an ownership error. `OwnRange` performs that check for every item. The metadata
also distinguishes framework-owned resources, which require a cleanup action, from caller-owned
resources, which may be recorded but cannot be disposed by the transaction.

On a primary Build failure with no cleanup failure, the original exception is rethrown through
`ExceptionDispatchInfo`. If cleanup also fails, the result is a non-flattened `AggregateException`
whose first item is the original failure and whose remaining items follow actual reverse cleanup
order. Cleanup continues after every failure.

## Deliberate boundaries

`SharpLinkRuntimeContext.ThrowAfterConstructionRollback` remains unchanged. It is constructor-local
cleanup for prepared generated-manifest registrations and its codec provider before a
`SharpLinkRuntimeContext` exists; it is not Client or Server Builder materialization and is not a
second Builder rollback path.

The three-argument endpoint-factory helper is now named `CreateRuntimeTransportFactory` to make its
scope explicit: dynamic endpoint generations are created after Client Build has committed and remain
runtime-owned. Its local cleanup was deliberately not moved into the build transaction. Builder
endpoint factories instead use the transaction-aware `CreateBuildTransportFactory` and have no
second local rollback path.

The transaction uses `SharpLinkAsyncCleanup.DisposeSynchronously` only for Build rollback or explicit
unbuilt-builder cleanup. The existing dynamic endpoint-generation helper retains its separate local
cleanup only for a runtime factory-binding failure; it is not a build-transfer path. Successful Build
paths call `Commit`/`Transfer`, which clears transaction tracking without disposal. Runtime stop and
drain paths retain their asynchronous ownership behavior.

## Fault-matrix evidence

| Case | Evidence |
|---|---|
| Transaction identity, reverse order, exact-once cleanup, primary/cleanup ordering, commit/transfer, terminal state, `OwnRange`, caller ownership, and reentrancy | `SynchronousBuildTransactionTests` |
| C0 RuntimeContext acquisition and C8 context cleanup | `BuilderOwnershipRollbackTests.ClientRuntimeContextConstructionFailureShouldRollbackTheConsumedTransport`; `StaticEndpointBuilderTests.ClientMaterializeRollbackShouldPreserveBuildAndRuntimeContextCleanupFailures` |
| C1 direct transport profile bind; C7 final Client/logger construction; C8 direct cleanup | `BuilderOwnershipRollbackTests.DirectClientProfileFailureShouldDisposeTransportAndPreserveBothFailures`; `DirectClientConstructionFailureShouldDisposeTransportAndPreserveBothFailures` |
| C2 endpoint-factory throw; C3 profile bind; C5 factory #N; C6 duplicate identity; C8 factory cleanup | `BuilderOwnershipRollbackTests.EndpointFactoryFailureShouldRollbackPreviouslyMaterializedFactories`; `StaticClientFactoryBindingFailureShouldRollbackFactoriesInReverseExactlyOnce`; `StaticEndpointBuilderTests.ClusterShouldRejectAFactoryInstanceSharedAcrossEndpoints`, and cleanup-aggregation cases. Phase 11 moves C4-style option validation into pure Compile, so `CompileValidationFailureShouldNotAcquireEndpointFactory` and `CompileValidationFailureShouldNotRunEndpointFactoryCleanup` prove that no factory exists to roll back. |
| Dynamic Client resolver acquisition and caller-owned codec/logger | `BuilderOwnershipRollbackTests.DynamicResolverValidationFailureShouldDisposeResolverAndPreserveBothFailures`; `ClientConstructionFailureMustNotDisposeCallerProvidedCodec` |
| MultiCluster coordinator failure after one child has materialized | `BuilderOwnershipRollbackTests.MultiClusterConstructionFailureShouldRollbackCompletedChildren` |
| `PrepareRuntimeCluster` | Existing candidate-connect, route-conflict, budget, and cancellation cases in `SharpLinkMultiClusterClientTests` exercise candidate cleanup around the caller. There is deliberately no claimed direct post-child route-freeze test: every normal duplicate type/ID condition later checked by `BuildStaticRoutes` is already rejected by `SharpLinkClient.BuildStaticProxySnapshot` before `MaterializeCompiledPlan` returns the child. |
| `PrepareReplacementCluster` | `SharpLinkMultiClusterClientTests.PrepareReplacementClusterShouldTransferItsChildAfterSuccessfulPreparation` proves that successful preparation commits/transfers rather than performs cleanup. The current post-child code has no normal failure seam beyond allocation failure. |
| S0 RuntimeContext construction and S1 listener profile bind | `BuilderOwnershipRollbackTests.ServerRuntimeContextConstructionFailureShouldRollbackTheConsumedListener`; `ServerProfileFailureShouldRollbackListenerAndRuntimeContext` |
| S2 provider ownership and S4 admission construction | `BuilderOwnershipRollbackTests.ServerConstructorFailureShouldDisposeRuntimeContextAndPreserveBothFailures` exercises the default framework-provider rollback route; `ServerAdmissionFailureMustRollbackFrameworkResourcesWithoutDisposingCallerProvider` and the registration test exercise caller-provider/admission ownership. The internal default provider is intentionally not externally observable. |
| S3 service-definition validation and S7 final Server/logger construction | `BuilderOwnershipRollbackTests.ServerCompileValidationFailureShouldNotMaterializeRuntimeContext`; `ServerConstructorFailureShouldDisposeRuntimeContextAndPreserveBothFailures`. Phase 11 makes S3 pure Compile validation, so only its pre-existing listener is released and no RuntimeContext cleanup is created. |
| S5/S6 every registration materialization, #N failure, strict reverse release | `BuilderOwnershipRollbackTests.ServerRegistrationBuildFailureShouldRollbackPriorMaterializationsInReverse` uses a third connection-lifetime replacement to fail inside `ServiceRegistrationDefinition.Build` after two registrations have materialized. It observes second-registration → first-registration → listener cleanup and proves the provider made the third scope-factory request. |
| Caller-owned provider, service, logger, and codec stay non-disposing | `BuilderOwnershipRollbackTests.ServerConstructionFailureMustNotDisposeCallerProvider`; `ServerFinalConstructionFailureMustNotDisposeCallerOwnedService`; `ClientConstructionFailureMustNotDisposeCallerProvidedCodec` |

The registration regression intentionally creates two framework-owned singleton replacement definitions through a
test-only private-state seam: the public `ReplaceService(instance)` contract is caller-owned, so it cannot
otherwise expose the disposal of a materialized singleton registration. The paired caller-owned-service test
keeps the public API guarantee explicit. No production ownership behavior is changed by that test seam.

No performance benchmark is required for this phase: the transaction exists only on synchronous
construction/failure paths and adds no per-RPC, frame, session, stream, start, stop, or drain work.
The validation gate nevertheless includes focused UnitTests, then the existing integration/trim/AOT
gates under the coordinating Agent's serialized remote schedule.
