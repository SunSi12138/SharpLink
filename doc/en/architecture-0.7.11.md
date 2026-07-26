# SharpLink 0.7.11 Codec Adapter Architecture

Chinese: [`../architecture-0.7.11.md`](../architecture-0.7.11.md)

0.7.11 removes serializer-specific knowledge from the SharpLink core. The generator reads only `RpcCodecAdapterRegistrationAttribute` metadata through Roslyn symbols; it does not recognize SharpPack or MemoryPack names, inspect NuGet package names, load runtime assemblies, or scan directories.

An extension declares its Adapter type, stable `AdapterId`, stable `WireFormatId`, and an optional selector Attribute. The official SharpPack extension uses:

```text
AdapterId:    sharplink.serializer.sharppack/v1
WireFormatId: memorypack-binary/v1
Selector:     SharpPackableAttribute
```

The selection order merges type-level `[RpcCodecAdapter]`, assembly-level closed-type bindings, and registered selector Attributes. Equivalent candidates are idempotent; different candidates fail at compile time. With no candidate, the native generated Codec is attempted. Installing an Adapter never creates an implicit fallback or changes a supported native DTO.

Generated Adapter factories call closed `adapterScope.CreateCodec<T>()` methods. They do not use `MakeGenericType`, `Activator.CreateInstance`, non-generic serializer calls, or a reflection resolver. Manifest API is version 3 while Protocol remains version 2.

Every request, response, stream item, and DTO member in contract JSON has a required non-empty `wireFormatId`. Because SharpLink is pre-1.0, development manifests missing this field are invalid (`SHARPLINK024`); there is no legacy inference path.

Runtime state is owned at exactly this granularity:

```text
SharpLinkRuntimeContext × manifest instance × AdapterId
```

All closed SharpPack Codecs in one group share a single `SharpPackSerializerContext`. Different clients, servers, manifests, plugins, and replacement generations do not share a Scope.

Build/register/replace prepares Scopes, Codecs, and services outside the registry lock. Publication is atomic after generation validation. Any failure or generation retry disposes the complete unpublished candidate. Cache entries carry their exact generated registration identity, so old-module cleanup cannot evict a replacement Codec.

Replacement publishes the new registration before draining the old module. New calls use the new Codec and Scope; admitted old calls retain the old generation until their leases end. Unregister then clears factory/type/manifest references and disposes generation-owned Scopes. The process catalog stores weak manifest references only, allowing the collectible ALC, Assembly, Type, manifest, factory, Codec, Scope, and serializer Context to be collected.

Explicit `UseCodec` remains highest priority and caller-owned. For custom formatters, build a caller-owned `SharpPackSerializerContext` and use `SharpPackRpcCodec.Create<T>(context)`.
