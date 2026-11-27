# 调用与输入流迁移到 3.0

先用同一套 3.0 SDK 重新生成契约、服务和插件，并一起更新 Client/Server。3.0 不保留旧 `Get`、`GetWithMetadata` 或不绑定契约的 `IRpcChannel`。

## 先准备代理，再调用服务

用 `CreateProxy<T>()` 取得本地代理；连接就绪后，对该代理调用有时限的 `PrepareAsync`，再通过 `proxy.Service` 执行业务。原来用 `GetWithMetadata` 的调用可在 `CreateProxy<T>(new RpcProxyOptions { Metadata = metadata })` 传入 metadata。重新连接后重新准备。查看远端版本清单不会准备代理或授予调用权。

手写的类型化调用使用 `GetLocalContract<T>()` 和 `CreateContractChannel(...)`。在此契约通道上准备并调用；用该通道的 `RuntimeContext` 解析对应生成程序集的 Codec。不要把应用代理或通道转换成框架内部使用的 `IRpcClientStreamSink`。

## 换代中断后，先查清单再重试

手动审批期间连接若被滚动刷新，`JoinVersionChangeAsync` 可能抛出错误码为 `Unavailable` 的 `SharpLinkException`，即使应用没有取消。取消或断线不等于对方拒绝，也不能据此判断服务端是否已经发布。服务端的 `ActivationSent` 只表示发出了激活消息；客户端的 `Resumed` 才表示它已准备好新路由。

如果旧路由已冻结后才收到拒绝，框架会归还旧绑定，不会恢复它。

1. 等连接就绪，用 `GetLocalVersionInventory()` 和 `QueryPeerVersionInventoryAsync(...)` 核对双方当前注册。比较注册 ID；契约哈希相同也可能换了服务实现。
2. 根据发布和激活状态选择连接。如果没有发布，仍就绪的原连接可以重新准备原注册。如果已发布且本连接获激活，可在原连接准备同组其他契约。如果已发布但本跟随连接未获激活，原连接不能准备目标注册组；应用若决定切到新代，先换为新的物理连接。原连接上的独立注册组仍可调用。
3. 对要继续使用的代理执行有时限的 `PrepareAsync`；查询清单不会准备代理，旧连接的绑定不能沿用。如果仍需换代，用当前就绪的连接和注册发起新的协调尝试；手动模式下重新审批，不重用上次的批准。

## 让输入枚举器释放合并的取消源

如果上传、双工或 OneWay 输入已经有自己的取消令牌，SharpLink 开始生产时仍会传入另一个令牌。两个信号都应有效。部分 C# 编译器在用 `EnumeratorCancellation` 自动合并两者时，可能把取消源留在原始 enumerable 上，而由另一个对象完成枚举；完成后仍可能留住内存。

让实际返回的枚举器合并并释放令牌。例如将文件上传包装为：

```csharp
await service.UploadAsync(new UploadFileLines(path, callerToken));
```

```csharp
public sealed class UploadFileLines(string path, CancellationToken caller) : IAsyncEnumerable<string>
{
    public IAsyncEnumerator<string> GetAsyncEnumerator(CancellationToken producer = default)
        => Enumerate(producer);

    private async IAsyncEnumerator<string> Enumerate(CancellationToken producer)
    {
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(caller, producer);
        using var reader = File.OpenText(path);
        while (await reader.ReadLineAsync(lifetime.Token).ConfigureAwait(false) is { } line)
            yield return line;
    }
}
```

只有在实际枚举时才打开文件；不需要提前启动生产者，也不应忽略框架传来的令牌。如果输入本来没有独立的应用令牌，继续使用传给枚举器的令牌即可。

上传、双工和带输入的 OneWay 在请求进入本地发送流程后就可以开始枚举，不等服务端确认。服务端仍先准入，再把输入交给业务。因此，生产者开始枚举（例如打开文件）不表示服务端已接受调用；OneWay 返回也不保证远端业务已执行。[协议](protocol-v2.md)和[整体升级清单](migration.md)列出部署边界。
