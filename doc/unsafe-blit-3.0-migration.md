# UnsafeBlit migration to 3.0

3.0 removes automatic reflection-based serialization of arbitrary unmanaged types. This applies to ordinary untrimmed JIT too. There is no optional reflection package and no switch to restore the old fallback.

## Generated RPC contracts

Regenerate every contract/service assembly with the matching 3.0 SDK. Generated UnsafeBlit factories carry their ABI requirements in the owning manifest. Native nested payloads and generated collection dependencies resolve through that frozen graph. Loading a generated plugin does not require arbitrary unmanaged reflection.

For a custom wire representation, use compile-time `[RpcCodec(typeof(MyCodec))]` or a Codec/Adapter policy binding. The former standalone runtime `AddCodec` and `UseCodecResolver` methods have been retired in 3.0. Missing generated metadata fails closed; there is no endpoint resolver fallback.

Generated API 5 requires the exact identity `sharplink-3.0-api5-owner-bound-unsafe-blit-v1`. API4 binaries, or a different API5 development identity, are rejected; a shared numeric API version alone is insufficient.

## Direct codec use outside generated RPC

Code such as `context.Codecs.GetCodec<MyUnmanagedStruct>()` no longer automatically falls back to reflection for arbitrary unmanaged types. There is no standalone runtime Codec registration or fallback resolver in 3.0.

SharpLink 3.0 intentionally retains public `SharpLinkRuntimeContextBuilder` / `SharpLinkRuntimeContext` for standalone use of **generated** serialization. Build an isolated context, retrieve `context.Codecs.GetCodec<MyGeneratedPayload>()` when the matching generated manifest is admitted, and serialize/deserialize or compare golden bytes and cross-release decoder behavior without starting a Client/Server. The caller must dispose the context. The owner-local registration and ABI identity checks still apply; an unregistered unmanaged type must fail closed.

If an application already owns an explicit `IRpcCodec<T>` unrelated to the generated manifest, it may use that codec directly without building a context. This does **not** restore the removed standalone `AddCodec` / `UseCodecResolver` methods and does not imply cross-version compatibility without verification.

For RPC contracts, annotate the payload with `[RpcCodec]` or use the compile-time Codec/Adapter route, and regenerate with the matching 3.0 SDK. Prefer field-wise codecs when padding or cross-runtime representation is a concern. Low-level internal session/stream consumers use admitted owner-generated Codec factories.

## Trimming and deployment

ProjectReference and PackageReference consumers exercise the same generated path in JIT, full/partial trimming and NativeAOT. No global `IsTrimmable=true` is added to Runtime. The old UnsafeBlit reflection feature switch, linker substitution, package trim suppression and public `SharpLinkGeneratedUnsafeBlitCatalog` are removed.

This change does not make arbitrary .NET reflection or dynamic loading valid in NativeAOT; it removes one specific unmanaged field-graph fallback. Raw UnsafeBlit still includes padding and remains subject to the [existing ABI and security limits](unsafe-blit-padding-security.md).

## Generated capability provenance

The raw-codec capability accepts only the exact factory instance admitted into the receiving owner's active scope. Its target Type and ABI requirement must match metadata snapshotted when the manifest was prepared. Passing `(8, false)` for an arbitrary unmanaged Type, reusing another owner's factory, or substituting a requirement cannot create a raw codec. This is owner-registration validation, not a security boundary against an application that explicitly supplies its own manifest/codec implementation.

The earlier unbound `sharplink-3.0-api5-generated-unsafe-blit-v1` development identity is incompatible and must be regenerated. A combined release with #754 must coordinate a single identity, generator contract and 3.0 public API baseline before publication.
