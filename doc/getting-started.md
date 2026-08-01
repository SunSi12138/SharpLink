# 快速开始

SharpLink 是面向 .NET 10 的 Source Generator RPC 框架。契约、代理、Stub 和受支持 DTO Codec 都在编译期生成；正常调用路径不做程序集扫描、`MakeGenericType` 或反射序列化。

## 引用

应用通常只需引用 `SharpLink.Sdk`；Client、Server 或 Hosting 应用再引用对应运行时包。仓库内 Demo 使用项目引用以便开发验证，发布使用 NuGet 包。

```xml
<ItemGroup>
  <PackageReference Include="SharpLink.Sdk" Version="1.0.0" />
  <PackageReference Include="SharpLink.Client" Version="1.0.0" />
  <PackageReference Include="SharpLink.Server" Version="1.0.0" />
</ItemGroup>
```

SDK 包携带 Source Generator；不要另外把 Generator 当运行时依赖发布。

## 定义契约与服务

```csharp
[RpcContract]
public interface ICalculator : IService
{
    ValueTask<int> AddAsync(int left, int right, CancellationToken cancellationToken);
}

[RpcService]
public sealed class Calculator : ICalculator
{
    public ValueTask<int> AddAsync(int left, int right, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult(left + right);
    }
}
```

契约必须是继承 `IService` 的接口。方法返回 `ValueTask`、`ValueTask<T>` 或受支持的 `IAsyncEnumerable<T>` 组合。若方法明确不接受 `CancellationToken`，标注 `[NonCancellable]`，表示业务工作可能在调用方放弃后继续；框架的流泵和资源清理仍可取消。

## 启动 Server 和 Client

```csharp
var server = SharpLinkServerBuilder.Create()
    .UseTcp(19090, "127.0.0.1")
    .Build();

using var stopping = new CancellationTokenSource();
var serverTask = server.RunAsync(stopping.Token);

var client = SharpClientBuilder.Create()
    .UseTcp("127.0.0.1", 19090)
    .Build();

await client.ConnectAsync();
var value = await client.Get<ICalculator>().AddAsync(20, 22, CancellationToken.None);
```

Client 和 Server 都是异步可释放对象。生产代码必须在停止时先阻止新工作，再 `DisposeAsync`，并观察后台运行任务；不要用进程退出替代资源收口。

## 分离部署

推荐把契约放在独立程序集，由 Client 和 Server 共同引用。契约程序集也必须引用 SDK、Abstractions、Runtime 和 Generator，因为生成代理、Stub 与 Manifest 依赖这些类型。完整结构见：

- `demo/SeparatedContracts`
- `demo/SeparatedServer`
- `demo/SeparatedClient`

## 下一步

- 复杂 DTO、集合和第三方序列化：[契约与序列化](contracts-and-codecs.md)
- 流式、取消、超时和 metadata：[调用、流式与取消](calls-and-streaming.md)
- Host/DI 部署：[Hosting 与服务生命周期](hosting-and-services.md)
