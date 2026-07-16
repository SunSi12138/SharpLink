# SharpLink

一个面向 .NET 的高性能 RPC 框架（当前主目标框架为 `net10.0`），支持：

- Source Generator 自动生成 `Proxy/Stub`
- Unary、`[Oneway]`、客户端流、服务端流、双向流、多流参数
- Protocol v2 协议级取消（`ProtocolV2FrameType.Cancel`）
- `Microsoft.Extensions.Hosting` 托管集成
- `Socket / NamedPipe / AnonymousPipe / UDS` 传输
- 内置基础编解码器，并可接入 `MemoryPack` 作为复杂类型回退序列化器

## 项目结构

核心项目（`src/`）：

- `SharpLink.Abstractions`：Protocol v2 公共模型、公共接口、通道与传输抽象
- `SharpLink.Runtime`：`RpcSession`、`StreamManager`、`RpcCodecRegistry`、传输实现与底层收发逻辑
- `SharpLink.Sdk`：`IService`、`[RpcContract]`、`[RpcService]`、`[Oneway]`、`[Timeout]` 等契约标记
- `SharpLink.Client`：客户端 Builder、连接生命周期、请求管理与代理调用通道
- `SharpLink.Server`：服务端 Builder、连接管理、Stub 分发、心跳与取消处理
- `SharpLink.Hosting`：`IServiceCollection` 扩展与 HostedService 集成
- `SharpLink.Generator`：契约/服务分析器与 `Proxy/Stub` 代码生成
- `SharpLink.Serializer.MemoryPack`：MemoryPack 编解码适配

示例（`demo/`）：

- `HelloWorld`：基础调用与多类型参数
- `Streaming`：客户端流、服务端流、双向流、多流参数
- `HostApplication`：Host 模式完整示例
- `Cancel`：协议级取消示例
- `Timeout`：默认超时与显式超时示例
- `Oneway`：单向调用示例
- `Log`：日志配置示例
- `SeparatedContracts / SeparatedServer / SeparatedClient`：分离式契约与多进程示例

测试与基准（`test/`）：

- `SharpLink.UnitTests`：快速单元测试
- `SharpLink.IntegrationTests`：真实传输与生成代码集成测试
- `SharpLink.Generator.Tests`：分析器与生成器规则测试
- `SharpLink.AotSmoke`：AOT/生成器/编解码链路冒烟验证
- `SharpLink.LoadTest*`、`SharpLink.Benchmarks`：压测与基准

## 快速开始

环境要求：

- .NET SDK `10.0.102` 或兼容的 `10.0` SDK

构建与测试：

```bash
dotnet build Sharplink.slnx -v minimal
dotnet test --solution Sharplink.slnx -v minimal
```

运行示例：

```bash
dotnet run --project demo/HelloWorld/HelloWorld.csproj
dotnet run --project demo/Streaming/Streaming.csproj
dotnet run --project demo/HostApplication/HostApplication.csproj
dotnet run --project demo/Cancel/Cancel.csproj
dotnet run --project demo/Timeout/Timeout.csproj
dotnet run --project demo/Oneway/Oneway.csproj
dotnet run --project demo/Log/Log.csproj
dotnet run --project demo/SeparatedServer/SeparatedServer.csproj
dotnet run --project demo/SeparatedClient/SeparatedClient.csproj
```

## 契约发现

- RPC 契约接口必须标记 `[RpcContract]`
- RPC 服务实现必须标记 `[RpcService]`
- 契约接口必须继承 `IService`
- 生成器默认扫描“引用了 `SharpLink.Sdk`”的引用程序集
- 可以通过程序集级特性缩小扫描范围：

```csharp
[assembly: SharpLinkRpcContracts(typeof(MyContract1), typeof(MyContract2))]
```

## 序列化与 AOT

SharpLink 默认内置基础类型与 blittable 容器编解码；复杂引用类型通常需要显式接入序列化器。

JIT 场景推荐：

```csharp
var client = SharpClientBuilder.Create()
    .UseTcp("127.0.0.1", 5000)
    .UseSerializer(MemoryPackCodec.Resolver)
    .Build();
```

NativeAOT 场景：

- `MemoryPackCodec.Resolver` 不可依赖反射回退
- 需要为每个非 blittable 的契约参数/返回类型显式注册编解码器

```csharp
var client = SharpClientBuilder.Create()
    .UseTcp("127.0.0.1", 5000)
    .UseCodec(MemoryPackCodec<MyDto>.Instance)
    .UseCodec(MemoryPackCodec<MyDto[]>.Instance)
    .Build();
```

Codec 注册属于当前 Client/Server 的 `SharpLinkRuntimeContext`，同一进程内的实例不会互相覆盖。`test/SharpLink.AotSmoke` 展示了 AOT 下显式注册复杂类型编解码器的最小用法。

## 传输说明

- `NamedPipe` 在 Unix/macOS 下最终会映射到 Unix Domain Socket 路径
- 当前运行时会对超长 pipe name 做确定性缩短，避免触发平台路径长度限制
- `AnonymousPipe` 当前已覆盖本机连接、断连与本机压测回归；仓库内置 LoadTest 仅支持 `--mode local`
- 若自行基于 `IAnonymousPipeAllocator` 将句柄转交外部进程，需要由宿主明确管理句柄交接与释放时机

平台能力矩阵：

| 传输 | Windows | Linux | macOS | 使用范围 |
| --- | --- | --- | --- | --- |
| TCP | 支持 | 支持 | 支持 | 本机或跨主机 |
| UDS | 不承诺 | 支持 | 支持 | 本机 |
| NamedPipe | 支持 | 支持（映射到 UDS） | 支持（映射到 UDS） | 本机 |
| AnonymousPipe | 支持 | 支持 | 支持 | 本机协同进程 |

正式 NuGet 包中，`SharpLink.Sdk` 会携带 `SharpLink.Generator` Analyzer。通过 NuGet 使用时只需引用 SDK，无需再手工添加 Generator DLL 或 Analyzer 项目引用。

## Host 模式

`SharpLink.Hosting` 提供：

- `services.AddSharpLinkServer(...)`
- `services.AddSharpLinkClient(...)`

`SharpClientBuilder` 定义于 `SharpLink.Client`，`SharpLinkServerBuilder` 定义于 `SharpLink.Server`。

## 错误模型

- 运行时失败使用 `SharpLinkException` 和 `SharpLinkErrorCode` 区分认证、deadline、资源耗尽、断连和协议错误
- `await client.ConnectAsync(ct)` 成功后才返回；连接或握手失败直接抛结构化异常，不再返回 `bool`
- 用户 `CancellationToken` 取消保留为本地 `OperationCanceledException`；deadline 到期为 `SharpLinkException(DeadlineExceeded)`

## 认证

- 默认认证模式为 Anonymous，不存在默认密码；需要认证时客户端可用 `UseAuthenticator("token")` 发送握手消息
- 服务端支持两种写法：

```csharp
builder.UseAuthenticator(static message => message == "expected-token");

builder.UseAuthenticator(static message => message == "expected-token"
    ? SharpLinkAuthenticationResult.Success
    : SharpLinkAuthenticationResult.Reject(
        SharpLinkErrorCode.AuthenticationExpired,
        "token expired"));
```

- 第二种写法适合需要返回明确拒绝原因、错误码，或区分过期/权限不足等场景

如果你还需要在服务方法内部读取当前身份上下文，可以直接访问：

```csharp
var subject = SharpLinkCallContext.Current?.Authentication?.Subject;
var tenantId = SharpLinkCallContext.Current?.Authentication?.TenantId;
var role = SharpLinkCallContext.Current?.Authentication?.GetClaim("role");
var canRead = SharpLinkCallContext.Current?.Authentication?.HasScope("rpc.read") ?? false;
var expiresAt = SharpLinkCallContext.Current?.Authentication?.ExpiresAt;
```

`SharpLinkCallContext.Current` 仅在服务端 RPC 调用处理期间有值。

契约方法可以在尾部声明一个 `SharpLinkCallOptions`，并可在其后再声明一个 `CancellationToken`。控制参数不会进入业务 payload：

```csharp
ValueTask<Result> ExecuteAsync(
    Command command,
    SharpLinkCallOptions options,
    CancellationToken cancellationToken);

var options = new SharpLinkCallOptions
{
    Timeout = TimeSpan.FromSeconds(2),
    WaitForReady = true,
    Metadata = new SharpLinkMetadata(
        new KeyValuePair<string, string>("tenant", "factory-a"))
};
```

绝对 `Deadline`、相对 `Timeout`、`[Timeout]` 和客户端默认值会取最早到期时间。Unary 默认 30 秒；Server/Duplex stream 默认无超时。服务端可从 `SharpLinkCallContext.Current` 读取协商后的 deadline 与 metadata。

如果你希望直接在服务方法里做常见授权校验，可以使用：

```csharp
SharpLinkAuthorization.RequireScope("rpc.read");
SharpLinkAuthorization.RequireTenant("tenant-a");
SharpLinkAuthorization.RequireActiveToken();
```

这些 helper 失败时会抛出带正确 `SharpLinkErrorCode` 的 `SharpLinkException`，客户端会收到对应结构化错误，而不是退化成普通字符串异常。

## 可调优配置

- 日志：`UseLoggerFactory(...)`
- 心跳：`UseHeartbeat(...)`
- 握手认证：客户端 `UseAuthenticator("token")`，服务端 `UseAuthenticator(message => bool/result)`
- 请求超时：`UseRequestTimeout(...)`
- `RpcSession` flush：`UseRpcSessionFlush(...)`
- `BufferWriterPool`：`UseBufferWriterPool(...)`
- 运行时并发容器：`UseStateStoreConcurrency(...)`
- 性能预设与流控边界：`UseRuntime(options => options.PerformanceProfile = SharpLinkPerformanceProfile.LowLatency)`

如果你使用 `UseTcp(0, "127.0.0.1")` 让系统自动分配端口，可以在 `Build()` 前通过 `serverBuilder.Transport.LocalEndPoint` 读取实际监听端口。

## 文档

- 计划：`doc/plan.md`
- 架构：`doc/architecture.md`
- 待办与改进方向：`doc/todo.md`
- 压测：`doc/loadtest.md`
- 贡献指南：`CONTRIBUTING.md`
- 更新日志：`CHANGELOG.md`
