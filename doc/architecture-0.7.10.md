# SharpLink 0.7.10 multi-cluster architecture

## Ownership

`SharpLinkMultiClusterClient` is a coordinator, not a flattened endpoint topology. Each slot owns an ordinary `SharpLinkClient`, including endpoint selection, retry exclusion, circuit breaker, admission, resolver, transport factories, connections, pending calls, and dynamic modules.

```
application
    |
    +-- Get<IOrders>()  -> orders slot   -> SharpLinkClient -> endpoint topology
    +-- Get<IPayment>() -> payments slot -> SharpLinkClient -> endpoint topology
```

The coordinator stores immutable `FrozenDictionary` snapshots. `Get<T>` does one type lookup and calls the selected child `Get<T>`. The generated proxy retains that child channel, so an RPC call does not read a route table, parse a key, allocate a routing object, use `AsyncLocal`, or add a field to Protocol v2 frames.

## Static routes

The generator emits `ISharpLinkGeneratedClusterRouteManifest` from assembly-level `SharpLinkClusterContractAssembly` attributes. Its catalog keeps weak references and unregisters collectible AssemblyLoadContexts while unloading. At build time the coordinator validates limits, snapshots only declared routes, validates unique contract ownership, expands generated dependency closures inside the same slot, and passes the immutable manifest list to the child builder's internal build context.

An ordinary `SharpClientBuilder.Build()` remains unchanged and still snapshots the complete generated assembly catalog. A multi-cluster child never implicitly exposes unrelated process-wide manifests.

## Lifecycle and locking

`ConnectAsync` is shared and starts all required child slots with bounded parallelism. Initial failure cancels unscheduled work, stops every started child, and transitions the coordinator to `Faulted`. After a successful start, a non-ready child makes the aggregate state `Degraded`; routes remain pinned to their original slot and no fallback is attempted. `StopAsync` first transitions to `Draining`, then stops every child even when another child fails cleanup, releases route snapshots, and reaches `Stopped`.

Lock order is coordinator gate, then child gate. The coordinator never awaits while holding its gate, and child code never calls back into the coordinator while holding a child gate. Route reads are lock-free. User-provided factories, resolver, selector, admission, retry, interceptor, authenticator, and service-provider callbacks are not invoked while the coordinator gate is held.

## Dynamic assemblies

`RegisterAssembly(cluster, assembly)` validates the target slot and global route ownership, then delegates manifest, codec, dependency, and module validation to the selected child. Only after child registration succeeds does the coordinator publish one new route snapshot. A publication failure immediately starts a zero-wait child unregister.

Unregister removes new-proxy visibility from the coordinator before using the child's existing module drain. Existing proxies retain their child module lease semantics. Replacement is limited to the same slot and identical `ContractId` sets; new `Get<T>` calls observe the child replacement after it is published. Dynamic ownership is retained until the child reports that framework references have been released, preserving collectible ALC behavior.

## AOT and observability

Static routing uses generated manifests and module initializers only. It does not scan loaded assemblies. Dynamic registration retains the established structured failure on platforms where it is unsupported. Cluster names are not added to default per-call metrics.
