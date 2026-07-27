# SharpLink 0.8.31 迁移指南

English: [`en/migration-0.8.31.md`](en/migration-0.8.31.md)

0.8.31 不改变 Protocol v2 wire framing、合法 payload 或生成代理/桩的调用路径，但收紧了 transport 所有权并清理不受支持的公共实现 API。

## AnonymousPipe 子进程交接

外部子进程继承两个 handle 后，父进程应立即完成交接：

```csharp
using var offer = await allocator.AllocateAsync(cancellationToken);
StartChildProcess(offer.InHandle, offer.OutHandle);
offer.CompleteHandleTransfer();
```

`CompleteHandleTransfer()` 与 `Dispose()` 共享幂等状态，会关闭两个父进程本地 client-handle 副本。必须先确保子进程已经继承 handle；如果在同一进程直接用 handle 构造 client stream，不要提前调用它。

## API 清理

- `ProtocolV2FrameWriter`、`ProtocolV2FrameToken`、`RpcBufferWriterExtensions`、`PacketToken`、`PacketScope`、`StripedLongMap<T>` 改为 internal。应用应使用 Source Generator 代理/桩与公开的 Protocol 模型；不要手工回填 frame offset。
- `GeneratedProxyRegistry`、`GeneratedStubRegistry` 已删除。当前 Source Generator 通过 generated assembly manifest 注册，不需要进程级静态 registry。
- `ISerializer`、`IServiceRegister` 已删除。自定义序列化使用 `IRpcCodec` / `IRpcCodecAdapter` 和 Builder；服务注册使用生成 manifest 与 Server Builder。
- `StripedLongSet` 已删除；它不是受支持的状态容器 API。业务代码使用自身并发容器，框架状态由 Runtime Context 所有。
- 自定义 socket `EndPoint` 必须正确实现 `Serialize()` 和返回独立实例的 `Create(SocketAddress)`；否则 factory 构造会抛 `ArgumentException`。
