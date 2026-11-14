# Migrate to contract-scoped channels in 3.0

Keep the proxy handle you prepare, then call its Service:

```csharp
var rpc = client.CreateProxy<IMyService>();
await rpc.PrepareAsync(TimeSpan.FromSeconds(10), cancellationToken);
var result = await rpc.Service.GetAsync(id, cancellationToken);
```

`CreateProxy` is synchronous local construction. It freezes the local contract/hash,
registration generation and immutable options; it does not Connect, wait for a Manifest,
activate a service or reserve a remote call. `PrepareAsync` has a mandatory positive finite
budget and prepares that same receiver. Retaining only `rpc.Service` also retains its receiver.

Connection readiness and contract readiness are different. Until at least one matching
physical Session has confirmed Bind, calls fail fast with `Unavailable/NoBoundRoute`, before
business serialization or producer work. A Manifest observation is not Bind confirmation.
Prepare is separate from the subsequent logical RPC deadline and does not reserve its capacity.

## Replace old factories and raw casts

- `Get<T>()` becomes `CreateProxy<T>()`, then use `.Service`.
- `GetWithMetadata<T>(metadata)` becomes `CreateProxy<T>(new RpcProxyOptions { Metadata = metadata })`.
- Keep the returned handle when you need explicit preparation; do not create another receiver
  merely to Prepare the first one.
- Read RuntimeContext from the client or a contract channel. The client no longer implements
  the removed `IRpcChannel`; there is no raw cast, default identity or obsolete forwarding API.

Server registration replacement and reconnect can rebind the same receiver under its original
scope. A new physical Session needs its own token; redirects cannot carry the predecessor's
token. Prepare the retained handle again when you require immediate post-replacement traffic.
If its **local** module retires, create a new handle from the newly registered local generation.
Multi-cluster handles capture one child: replacing that slot does not retarget old handles.

## Handwritten calls use the same receiver

```csharp
var local = client.GetLocalContract<IMyContract>();
var channel = client.CreateContractChannel(local);
await channel.PrepareAsync(TimeSpan.FromSeconds(10), cancellationToken);
var answer = await channel.InvokeUnaryAsync(method, in request, requestCodec,
    responseCodec, metadata: null, cancellationToken);
```

Use descriptors, typed Codecs and stream writers for that contract. The opaque local selector
is client-issued; foreign clients and retired generations cannot select another owner's current
registration. Non-generic code uses `GetLocalContract(exactRegisteredAssembly, contractId)`.
The returned selector contains no Assembly/Type/Codec reference and never follows latest.

All five invocation families retain their typed Codec/writer, metadata, cancellation and return
shapes. ClientStreaming/Duplex producers wait for **this call's** RequestAccepted, not merely Bind.
OneWay input retains no-per-call-Ack semantics and a separate bounded stream count. Stream writers
receive the real connection-owned `IRpcClientStreamSink`; a contract channel is not a stream sink.

## Update manifests and custom Stubs together

Rebuild all contract, service and plugin projects using matching 3.0 packages. The combined
Generated API is **6**, exact ABI **`sharplink-3.0-api6-contract-binding-v1`**. API4, API5 and other
ABI families fail metadata preflight; changing only a locator's integer cannot make old output valid.
Owner-bound generated serialization remains supported without a Client/Server; see
[UnsafeBlit migration](unsafe-blit-3.0-migration.md).

A custom `IRpcStub` implements required `TryResolveMethod(id, out descriptor)` and all four
invocation members with `in RpcMethodDescriptor`, rather than a bare MethodId. Return false
for unknown methods and supply complete scalar facts for known methods. The same original
facts and registration remain protected through queue/decode/invocation and physical stream exit.
There are no default narrow-query fallbacks or old-signature adapters.

## Wire and registration receipts

Both peers must speak Protocol v2 **minor 5**, minimum compatible minor **5**. Bind validates the
full contract hash and optional receipt once on the actual data Session. Ordinary route prefixes
carry that Session's BindingId plus MethodId, not ContractId; minor4 peers are rejected during
handshake. No cross-major interoperability is implied by the unchanged protocol major.

For a control-plane publication result, use Server `RegisterAssemblyWithReceipts` or
`ReplaceAssemblyWithReceiptsAsync`; each returns immutable per-service receipts captured at its
own commit. Select the ContractId and supply that receipt/requirement in `RpcProxyOptions`.
`GetRegistrationReceipt` is a separate current-state snapshot, not an operation result. The
existing client/shared registry result types are unchanged. See [receipt usage and constraints](calls-and-streaming.md#用注册结果准备精确服务).
