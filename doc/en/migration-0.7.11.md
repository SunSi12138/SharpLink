# Migrating to SharpLink 0.7.11

Chinese: [`../migration-0.7.11.md`](../migration-0.7.11.md)

0.7.11 is an intentional pre-1.0 source/API-breaking migration. Protocol v2 does not change, but generated Manifest API v3 requires every contract and plugin to be rebuilt.

| Before | 0.7.11 |
| --- | --- |
| `SharpLink.Serializer.MemoryPack` | `SharpLink.Serializer.SharpPack` |
| MemoryPack / `using MemoryPack` | SharpPack 1.0.1 / `using SharpPack` |
| `[MemoryPackable]` | `[SharpPackable]` |
| `[RpcExternalCodec]` | a serializer selector or `[RpcCodecAdapter(...)]` |
| `MemoryPackCodec.Resolver` | remove; the Manifest Adapter is automatic |
| `MemoryPackCodec<T>.Instance` | automatic Adapter or `SharpPackRpcCodec.Create<T>(context)` |

Reference `SharpLink.Serializer.SharpPack` from the contract project and mark complex graphs with `[SharpPackable]`. No client/server resolver registration is required. Supported ordinary DTOs remain on the native `sharplink-native/v1` path.

For serializers without a selector Attribute, apply `[RpcCodecAdapter(typeof(Adapter))]` to a source type, or use an assembly-level two-argument binding for a closed third-party type. Open generics, built-in primitives, and conflicting Adapter selections are compile-time errors.

For a caller-owned custom formatter Context:

```csharp
var context = new SharpPackSerializerContextBuilder()
    .Register<ThirdPartyPayload>(new ThirdPartyPayloadFormatter())
    .Build();

builder.UseCodec(SharpPackRpcCodec.Create<ThirdPartyPayload>(context));
```

Explicit Codecs override generated Adapters and are not disposed by SharpLink.

Delete and regenerate development-time contract baselines. Every request, response, stream item, and DTO member must contain a non-empty `wireFormatId`; missing, null, empty, or whitespace values report `SHARPLINK024`. No legacy JSON inference or warning fallback is retained.

The repository keeps MemoryPack 1.21.4 golden bytes only as fixed test fixtures. SharpPack 1.0.1 reads them and writes identical bytes for null, nullable/string/non-ASCII, arrays/lists/dictionaries, nested objects, empty collections, unions, and circular graphs.
