# 快速开始

SharpLink 是面向 .NET 10 的 Source Generator RPC 框架。契约、代理、Stub 和受支持 DTO Codec 都在编译期生成；正常调用路径不做程序集扫描、`MakeGenericType` 或反射序列化。

## 引用

契约项目通常只需引用 `SharpLink.Sdk`；Client、Server 或 Hosting 应用再引用对应应用包。SDK 2.0 只传递引入 Abstractions，不再传递引入 Runtime。仓库内 Demo 使用项目引用以便开发验证，发布使用 NuGet 包。

```xml
<ItemGroup>
  <PackageReference Include="SharpLink.Sdk" Version="2.0.0" />
  <PackageReference Include="SharpLink.Client" Version="2.0.0" />
  <PackageReference Include="SharpLink.Server" Version="2.0.0" />
</ItemGroup>
```

SDK 包依赖 Abstractions 并携带 Source Generator；不要另外把 Generator 当运行时依赖发布。纯契约项目不需要引用 Runtime；直接使用 Runtime API 的项目则应显式引用 `SharpLink.Runtime`，不能依赖 SDK 带入。

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
    .UseRequestTimeout()
    .Build();

await client.ConnectAsync();
var value = await client.Get<ICalculator>().AddAsync(20, 22, CancellationToken.None);
```

每个 Client 都必须在 Build 前显式选择请求超时策略：`UseRequestTimeout()` 使用推荐的 30 秒 Unary fallback，`UseRequestTimeout(timeout)` 使用自定义 fallback，`DisableRequestTimeout()` 则明确关闭 Client-wide fallback。没有选择策略时 Build 会失败；方法 `[Timeout]` 和继承的父调用 `TimeBudget` 仍按各自规则生效。

Client 和 Server 都是异步可释放对象。生产代码必须在停止时先阻止新工作，再 `DisposeAsync`，并观察后台运行任务；不要用进程退出替代资源收口。

`SharpClientBuilder` 与 `SharpLinkServerBuilder` 也是一次性构建器：一次 `Build()` 尝试后（成功或
失败）不能继续配置或再次 Build，需要新的运行实例时请创建新的 Builder。Client 在第一次选择
`UseTransport`、`UseEndpoint(s)` 或 `UseEndpointResolver` 时就确定 topology，不能混用或重复替换。
这保证 transport/resolver 所有权和静态 endpoint 快照只有一个明确归属。

## 分离部署

推荐把契约放在独立程序集，由 Client 和 Server 共同引用。契约程序集只需引用 `SharpLink.Sdk`；SDK 会传递引入生成 Proxy、Stub、Codec 与 Manifest 所需的 Abstractions，并自动携带 Source Generator。API 4 生成程序集不引用 Runtime。Client 和 Server 项目再分别引用契约程序集及自身所需的 `SharpLink.Client` 或 `SharpLink.Server` 包，这些应用包负责引入 Runtime。完整结构见：

- `demo/SeparatedContracts`
- `demo/SeparatedServer`
- `demo/SeparatedClient`

## 下一步

- 复杂 DTO、集合和第三方序列化：[契约与序列化](contracts-and-codecs.md)
- 流式、取消、超时和 metadata：[调用、流式与取消](calls-and-streaming.md)
- Host/DI 部署：[Hosting 与服务生命周期](hosting-and-services.md)
