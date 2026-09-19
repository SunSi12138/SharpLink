# Generation control extension

`SharpLink.GenerationControl` is an optional, statically compiled control-plane contract for deployments that must reconcile caller-side and server-side generated generations. It is deliberately separate from Protocol v2 and the ordinary RPC data path.

## Contract and source of truth

Both endpoints statically reference the package. `ISharpLinkGenerationControl` exposes `GetInventoryAsync`, `StageAsync`, `ActivateAsync`, and `WatchAsync`.

`GetInventoryAsync` returns the authoritative endpoint-local desired/actual snapshot. `WatchAsync(afterRevision, ...)` is only an invalidation fast path. After reconnect, notification loss, or a revision gap, consumers re-query inventory instead of replaying events to reconstruct correctness.

The package does not choose a single global authority. An application may designate one endpoint/controller as authoritative or implement symmetric negotiation; the package only fixes the state and operation vocabulary.

## Descriptor and lifecycle

A descriptor carries a capability ID, generation ID, wire/contract identity, generated ABI identity, immutable artifact hash/reference, and opaque compatibility metadata. Actual state uses:

`Absent -> Staged -> Validated -> Ready -> Active -> Draining -> Removed`

`Failed` is a terminal observation for a preparation/activation attempt, not an automatic rollback policy.

Stage and activate stay separate. Stage is where an application/provider resolves its artifact reference, verifies the artifact and compatibility metadata, and prepares a candidate. Activate is the publication boundary selected by that provider. Expected control-plane outcomes are typed through `SharpLinkGenerationOperationStatus`; `Message` is diagnostic only.

## Provider boundary

`ISharpLinkGenerationProvider` separates reconciliation from materialization. The package does not download files, verify signatures, solve versions, define rollout policy, or implement rollback.

A JIT provider can map stage/activate to collectible `AssemblyLoadContext` plus `ISharpLinkAssemblyRegistry`. A precompiled provider can map the same operations to application-owned generations.

The provider does not own the control service's inventory revision. Provider results describe materialization only; the service implementation publishes its own immutable desired/actual snapshot after the provider outcome.

## NativeAOT boundary

The RPC contract and DTOs are statically generated and do not require reflection scanning or runtime code generation. `SharpLinkGenerationMaterializationMode.ProcessReplacementRequired` explicitly represents NativeAOT/process-level reconciliation: stage may prepare a new binary, while activation is completed by application/deployment orchestration through process replacement and normal SharpLink drain/stop semantics.

The package does not pretend that NativeAOT can load an unknown managed assembly or provide an in-process replacement adapter.

## Deployment patterns

- Implementation-only update: keep wire/generated ABI identities compatible; no caller generation change is required.
- Additive generation: stage/publish the receiving side first, then activate the corresponding caller generation.
- Incompatible contract update: run generations side by side, migrate callers, then drain/remove the old generation.

Artifact bytes, credentials, signing roots, storage URIs, process supervisors, rollout percentages, health policy, and automatic rollback remain application/deployment concerns.
