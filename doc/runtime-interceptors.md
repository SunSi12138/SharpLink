# Runtime interceptor replacement

SharpLink client and server builders still freeze their configured interceptor lists during `Build`. That list is the initial runtime generation. A running `ISharpLinkClient` or `ISharpLinkServer` can replace the complete pipeline with `ReplaceInterceptors`.

Replacement is copy-on-write. SharpLink enumerates and validates the supplied sequence, copies it into a new array, and only then atomically publishes the array reference. The caller may therefore mutate or reuse its original collection after `ReplaceInterceptors` returns. A null sequence or null element is rejected before publication, so the previous generation remains active.

The visibility boundary is the next logical RPC. Each client RPC captures the current interceptor array once at its public invocation boundary; telemetry and continuations use that same captured array. Each server invocation captures once when its call context is created and carries the generation through the complete interceptor dispatch. An RPC that started before replacement finishes with its old generation, while an RPC started after replacement returns uses the new generation. Streaming items never re-read the runtime pipeline.

An empty replacement disables interception. The disabled request path keeps the direct invocation branch: it does not create a no-op interceptor, pipeline object, continuation state, or request-path lock. Runtime replacement is a control-plane operation and may allocate the new snapshot.

Interceptor instance ownership does not change. Replacing or removing an interceptor does not cause SharpLink to call `Dispose` or `DisposeAsync`; caller-owned instances can remain in use by in-flight RPCs that captured the old generation.

Replacement is rejected once the owning client or server has started stopping, is draining, has stopped, or is faulted. Full replacement is last-writer-wins for concurrent writers; no read-modify-write mutation API is exposed by this feature.
