# UnsafeBlit migration to 3.0

3.0 removes automatic reflection-based serialization of arbitrary unmanaged types. This applies to ordinary untrimmed JIT too. There is no optional reflection package and no switch to restore the old fallback.

## Generated RPC contracts

Regenerate every contract/service assembly with the matching 3.0 SDK. Generated UnsafeBlit factories carry their ABI requirements in the owning manifest. Native nested payloads and generated collection dependencies resolve through that frozen graph. Loading a generated plugin does not require arbitrary unmanaged reflection.

For a custom wire representation, use the existing compile-time `[RpcCodec(typeof(MyCodec))]` or Codec/Adapter policy binding. Endpoint `UseCodecResolver` and standalone `AddCodec` do **not** override generated RPC owner policy. Missing generated metadata fails closed even if an endpoint resolver can handle that Type.

Generated API 5 requires the exact identity `sharplink-3.0-api5-owner-bound-unsafe-blit-v1`. API4 binaries, or a different API5 development identity, are rejected; a shared numeric API version alone is insufficient.

## Standalone runtime codec use

Code such as `context.Codecs.GetCodec<MyUnmanagedStruct>()` formerly fell back to reflection when the Type had no builtin, generated or explicit codec. It now throws `PlatformNotSupportedException`.

For this **standalone context** case, implement `IRpcCodec<MyUnmanagedStruct>` and register it through the public `SharpLink.Runtime.SharpLinkRuntimeContextBuilder.AddCodec<T>` API:

```csharp
using var context = new SharpLinkRuntimeContextBuilder()
    .AddCodec(new MyPayloadCodec())
    .Build();
var codec = context.Codecs.GetCodec<MyPayload>();
```

An application-owned standalone resolver may instead use `UseCodecResolver`. `AddCodec` is not a client/server-builder API, and these registrations are not a generated RPC policy override. Prefer a field-wise codec where padding or cross-runtime representation is a concern.

Builtin scalars/enums and their supported builtin collections are unaffected. Low-level session/stream calls that ask the context for an otherwise unregistered unmanaged codec need the same explicit registration or generated binding.

## Trimming and deployment

ProjectReference and PackageReference consumers exercise the same generated path in JIT, full/partial trimming and NativeAOT. No global `IsTrimmable=true` is added to Runtime. The old UnsafeBlit reflection feature switch, linker substitution, package trim suppression and public `SharpLinkGeneratedUnsafeBlitCatalog` are removed.

This change does not make arbitrary .NET reflection or dynamic loading valid in NativeAOT; it removes one specific unmanaged field-graph fallback. Raw UnsafeBlit still includes padding and remains subject to the [existing ABI and security limits](unsafe-blit-padding-security.md).

## Generated capability provenance

The raw-codec capability accepts only the exact factory instance admitted into the receiving owner's active scope. Its target Type and ABI requirement must match metadata snapshotted when the manifest was prepared. Passing `(8, false)` for an arbitrary unmanaged Type, reusing another owner's factory, or substituting a requirement cannot create a raw codec. This is owner-registration validation, not a security boundary against an application that explicitly supplies its own manifest/codec implementation.

The earlier unbound `sharplink-3.0-api5-generated-unsafe-blit-v1` development identity is incompatible and must be regenerated. A combined release with #754 must coordinate a single identity, generator contract and 3.0 public API baseline before publication.
