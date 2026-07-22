# Migrating to SharpLink 0.7.10

## Existing clients

No migration is needed for `SharpClientBuilder`, `ISharpLinkClient`, endpoint clusters, Protocol v2, or existing generated contract assemblies. A normal client preserves its existing manifest discovery and fixed-endpoint fast path.

## Static multi-cluster clients

1. Keep each reusable contract package independent of deployment topology.
2. In the application or hosting assembly, declare one route for each contract-owning assembly:

```csharp
[assembly: SharpLinkClusterContractAssembly("orders", typeof(OrderContractsMarker))]
[assembly: SharpLinkClusterContractAssembly("payments", typeof(PaymentContractsMarker))]
```

3. Configure matching slots with `SharpLinkMultiClusterClientBuilder`.
4. Call `Get<TContract>()` without a cluster parameter.

The generator rejects a malformed key or one contract assembly assigned to two clusters. Build rejects a route whose slot is absent, duplicate ContractType/ContractId ownership, missing generated dependencies, an empty slot without explicit dynamic opt-in, or a connection budget overrun.

## Runtime plugins

Use the explicit-slot APIs:

```csharp
client.RegisterAssembly("plugins", pluginAssembly);
await client.UnregisterAssemblyAsync("plugins", pluginAssembly, TimeSpan.FromSeconds(10));
await client.ReplaceAssemblyAsync("plugins", oldAssembly, newAssembly, TimeSpan.FromSeconds(10));
```

Contract-owning assemblies can belong to exactly one slot. Dependency-only assemblies may be owned by several slots, but their generated dependencies must already be present in the same child slot. Replacement cannot add, remove, or move contracts; use unregister followed by register when the contract set changes.

## Intentional limits

0.7.10 has no `Get<T>(cluster)`, default cluster, cross-cluster retry/failover, runtime route move, cluster list discovery, or cluster metadata in requests. Every configured slot participates in `ConnectAsync`; a plugin-only slot must set `AllowDynamicContracts = true`.

For hosted applications use `services.AddSharpLinkMultiClusterClient(...)` and obtain only `ISharpLinkMultiClusterClientAccessor`. Child clients are intentionally not registered in DI.
