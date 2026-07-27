# SharpLink 0.8.31 migration guide

Chinese: [`../migration-0.8.31.md`](../migration-0.8.31.md)

0.8.31 does not change Protocol v2 wire framing, valid payloads, or generated proxy/stub call paths. It tightens transport ownership and removes unsupported implementation APIs.

## Anonymous-pipe child-process transfer

Complete the parent side immediately after the child has inherited both handles:

```csharp
using var offer = await allocator.AllocateAsync(cancellationToken);
StartChildProcess(offer.InHandle, offer.OutHandle);
offer.CompleteHandleTransfer();
```

`CompleteHandleTransfer()` and `Dispose()` share idempotent state and close both parent-side client-handle copies. Do not call it before inheritance is complete. When directly wrapping the handles inside the same process, do not complete transfer early.

## API cleanup

- `ProtocolV2FrameWriter`, `ProtocolV2FrameToken`, `RpcBufferWriterExtensions`, `PacketToken`, `PacketScope`, and `StripedLongMap<T>` are internal. Use generated proxies/stubs and public protocol models instead of manually backfilling frame offsets.
- `GeneratedProxyRegistry` and `GeneratedStubRegistry` were removed; generated assembly manifests are the supported registration path.
- `ISerializer` and `IServiceRegister` were removed. Use `IRpcCodec` / `IRpcCodecAdapter`, builders, generated manifests, and the Server Builder.
- `StripedLongSet` was removed. Applications should own their concurrency container; framework state is owned by its Runtime Context.
- A custom socket `EndPoint` must implement `Serialize()` and return an independent instance from `Create(SocketAddress)` or factory construction throws `ArgumentException`.
