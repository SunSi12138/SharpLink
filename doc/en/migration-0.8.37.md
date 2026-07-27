# SharpLink 0.8.37 migration guide

Chinese: [`../migration-0.8.37.md`](../migration-0.8.37.md)

0.8.37 does not change valid Protocol v2 framing, route hashes, or business payloads. It turns models that previously emitted broken C# or silently lost derived state into SharpLink compile-time diagnostics.

`[RpcService]` and native `[RpcSerializable]` types must be reachable from a sibling generated namespace. Public, internal, and protected-internal declarations remain valid. Private, protected, private-protected, file-local, or declarations beneath such containing types now report `SHARPLINK018`/`SHARPLINK009`; promote the type or use an accessible Adapter.

Native generated record classes must now be sealed. Seal fixed-schema records, or use an explicit Codec Adapter for polymorphic record graphs. Ref structs and span-like RPC payloads now report `SHARPLINK009`; replace them with persistent DTOs, arrays, `Memory<T>`, or `ReadOnlyMemory<T>`.

RPC contracts cannot require static abstract operators/conversions from a generated Proxy and now report `SHARPLINK054`; separate generic-math constraints from RPC contracts. Keyword DTO members need no migration and now generate valid C#.

The admission/drain race-probe correction affects test gating only and does not change Server runtime behavior.
