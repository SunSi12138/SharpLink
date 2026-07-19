# SharpLink

一个面向 .NET 的高性能 RPC 框架（当前主目标框架为 `net10.0`），支持：

- Source Generator 自动生成 `Proxy/Stub`
- Unary、`[Oneway]`、客户端流、服务端流、双向流、多流参数
- Protocol v2 协议级取消（`ProtocolV2FrameType.Cancel`）
- TLS/mTLS、认证、Interceptor、deadline、背压、健康检查与 OpenTelemetry
- `Microsoft.Extensions.Hosting`、DI 生命周期、readiness 与优雅排空
- `Socket / NamedPipe / AnonymousPipe / UDS` 传输，以及实验性的同用户共享内存传输
- 内置基础编解码器，并可接入 `MemoryPack` 作为复杂类型回退序列化器

## 项目结构

核心项目（`src/`）：

- `SharpLink.Abstractions`：Protocol v2 公共模型、公共接口、通道与传输抽象
- `SharpLink.Runtime`：`RpcSession`、`StreamManager`、实例级 Codec Provider、传输实现与底层收发逻辑
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
dotnet build Sharplink.slnx -c Release
dotnet test --project test/SharpLink.UnitTests/SharpLink.UnitTests.csproj -c Release
dotnet test --project test/SharpLink.Generator.Tests/SharpLink.Generator.Tests.csproj -c Release
dotnet run --project test/SharpLink.IntegrationTests/SharpLink.IntegrationTests.csproj -c Release -- --timeout 120s
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

RPC 可达的常规 DTO 会自动生成无反射 Codec，不需要注册序列化器：

```csharp
public sealed record Address([property: RpcMember(1)] string City);

public sealed class WorkOrder
{
    [RpcRequired]
    public string Number { get; init; } = string.Empty;
    public Address Address { get; init; } = new("");
    public List<string> Tags { get; init; } = [];
    [RpcIgnore]
    public string LocalCacheKey { get; init; } = string.Empty;
}
```

原生子集包含 primitive、enum、nullable、string、数组、`List`、`Dictionary`、`Memory`、`ReadOnlyMemory`、`ImmutableArray`、class/struct/record 及无环嵌套。未直接出现在 RPC 签名中的入口可标记 `[RpcSerializable]`。默认成员 ID 来自稳定成员名 hash；重命名同时要求 wire 兼容时，应保留显式 `[RpcMember(id)]`。

循环/多态对象图、任意 `object` 和第三方运行时类型继续交给显式 Codec。`[MemoryPackable]` 会自动退出原生生成；其他类型可使用 `[RpcExternalCodec]` 或程序集级声明：

```csharp
[assembly: RpcExternalCodec(typeof(ThirdPartyGraph))]

var client = SharpClientBuilder.Create()
    .UseTcp("127.0.0.1", 5000)
    .UseCodec(MemoryPackCodec<ThirdPartyGraph>.Instance)
    .Build();
```

生成 manifest 是幂等、不可重配置的进程元数据；Codec 实例与依赖仍在每个 Client/Server 的 `SharpLinkRuntimeContext` 构建时冻结，因此同进程实例不会互相覆盖。`test/SharpLink.AotSmoke` 使用纯生成 Codec 完成 NativeAOT publish/run，不扫描程序集或调用 `MakeGenericType`。

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
| SharedMemory（实验） | CI 目标，待门禁 | CI 目标，待门禁 | arm64 本机已验证 | 同机、同一用户 |

### 实验性共享内存传输

共享内存传输必须在 Client 与 Server 两端显式选择；创建、映射或握手失败会直接报错，绝不静默降级到其他传输。它只允许同机、同一操作系统用户的进程连接，数据通过每连接双向 SPSC 环传输，命名管道只承载握手、合并唤醒、关闭和存活信号。

```csharp
var server = SharpLinkServerBuilder.Create()
    .UseSharedMemory("orders", options =>
    {
        options.CapacityPerDirectionBytes = 8 * 1024 * 1024;
        options.SpinCount = 8;
        options.HandshakeTimeout = TimeSpan.FromSeconds(10);
    })
    .Build();

var client = SharpClientBuilder.Create()
    .UseSharedMemory("orders")
    .Build();
```

容量必须是 64 KiB–256 MiB 的 2 的幂；双方不一致时取较小值。显式配置优先于运行时 profile，默认值如下：

| Profile | 每方向容量 | SpinCount |
| --- | ---: | ---: |
| LowLatency | 1 MiB | 64 |
| Balanced | 8 MiB | 8 |
| Throughput | 32 MiB | 0 |

该传输不提供 TLS；同用户隔离依赖命名管道权限、用户私有映射目录、随机 nonce 和映射头校验。SharpLink RPC 认证、授权、deadline、流控和心跳照常生效。普通日志和性能报告不会记录映射路径、nonce 或 payload。正式支持状态以三平台 JIT/NativeAOT、性能与 24 小时 Chaos 门禁为准，当前实验结论见 [`doc/shared-memory-experiment.md`](doc/shared-memory-experiment.md)。

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

- 默认模式明确为 Anonymous，不存在默认密码。`RequireAuthentication()` 后没有注册服务端 provider 会在 Build 阶段失败。
- client provider 会为每次连接/重连重新创建有界二进制 payload，适合刷新短期 token：

```csharp
var clientAuthenticator = SharpLinkAuthenticator.CreateClient(async cancellationToken =>
    await tokenProvider.GetPayloadAsync(cancellationToken));

var serverAuthenticator = SharpLinkAuthenticator.CreateServer(async (request, cancellationToken) =>
{
    var identity = await tokenValidator.ValidateAsync(request.Payload, cancellationToken);
    return identity is null
        ? SharpLinkAuthenticationResult.Reject()
        : SharpLinkAuthenticationResult.Authenticate(
            new SharpLinkAuthenticationContext(
                subject: identity.Subject,
                tenantId: identity.TenantId,
                scopes: identity.Scopes,
                expiresAt: identity.ExpiresAt));
});

var client = SharpClientBuilder.Create()
    .UseAuthenticator(clientAuthenticator);

var server = SharpLinkServerBuilder.Create()
    .UseAuthenticator(serverAuthenticator)
    .RequireAuthentication();
```

认证 payload 受 handshake/metadata 上限约束，provider 异常只向客户端公开通用认证失败。payload、token 和证书内容不会写入普通日志。认证上下文如果在 handshake 时已经过期会直接返回 `AuthenticationExpired`。

如果你还需要在服务方法内部读取当前身份上下文，可以直接访问：

```csharp
var subject = SharpLinkCallContext.Current?.Authentication?.Subject;
var tenantId = SharpLinkCallContext.Current?.Authentication?.TenantId;
var role = SharpLinkCallContext.Current?.Authentication?.GetClaim("role");
var canRead = SharpLinkCallContext.Current?.Authentication?.HasScope("rpc.read") ?? false;
var expiresAt = SharpLinkCallContext.Current?.Authentication?.ExpiresAt;
```

`SharpLinkCallContext.Current` 仅在服务端 RPC 调用处理期间有值。

## TCP TLS

TLS 在 TCP 建连后、SharpLink Protocol v2 handshake 前完成，并拥有独立的 10 秒默认超时。客户端默认使用平台证书链和 hostname 校验；框架不提供“接受所有证书”的默认 helper。

```csharp
var server = SharpLinkServerBuilder.Create()
    .UseTcp(5000, new SslServerAuthenticationOptions
    {
        ServerCertificate = serverCertificate,
        ClientCertificateRequired = true
    })
    .Build();

var client = SharpClientBuilder.Create()
    .UseTcp("127.0.0.1", 5000, new SslClientAuthenticationOptions
    {
        TargetHost = "rpc.example.internal",
        ClientCertificates = new X509CertificateCollection { clientCertificate }
    })
    .Build();
```

UDS、NamedPipe、AnonymousPipe 与 SharedMemory 默认依赖操作系统权限，不叠加 TLS。TLS 建立日志只记录协商协议与 cipher suite，不记录证书私钥、token 或 payload。

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

绝对 `Deadline`、相对 `Timeout`、`[Timeout]` 和客户端默认值会取最早到期时间。Unary 默认 30 秒；Server/Duplex stream 默认无超时。调用 deadline 到期时，客户端固定得到 `SharpLinkException(DeadlineExceeded)`；这与服务实现是否声明 `CancellationToken` 无关。`DisableRequestTimeout()` 只关闭客户端默认值，显式 deadline、`Timeout` 和 `[Timeout]` 仍然生效。

建议所有可能等待、访问 I/O 或占用昂贵资源的契约方法都把 `CancellationToken` 放在参数末尾。Unary 没有 token 时产生 `SHARPLINK004` Warning；Streaming 没有 token 时产生 `SHARPLINK014` Error。确认业务工作不可取消时可用 `[NonCancellable]` 显式说明，但不能同时声明该特性和 `CancellationToken`，否则产生 `SHARPLINK015` Error。此时客户端仍会按 deadline 停止等待，服务端会把调用标记为 abandoned、丢弃迟到响应并继续观察业务任务，直到任务结束后才释放该调用的 admission 与 DI scope。Streaming 的框架流泵、dispatcher 和窗口等待仍会被终止，不会因为 `[NonCancellable]` 保留连接资源。团队可以在 `.editorconfig` 中将 `dotnet_diagnostic.SHARPLINK004.severity = error` 提升为编译错误。

服务端可从 `SharpLinkCallContext.Current` 读取协商后的 deadline 与 metadata。

如果你希望直接在服务方法里做常见授权校验，可以使用：

```csharp
SharpLinkAuthorization.RequireScope("rpc.read");
SharpLinkAuthorization.RequireTenant("tenant-a");
SharpLinkAuthorization.RequireActiveToken();
```

这些 helper 失败时会抛出带正确 `SharpLinkErrorCode` 的 `SharpLinkException`，客户端会收到对应结构化错误，而不是退化成普通字符串异常。

## Interceptor 与业务异常

Client/Server interceptor 按注册顺序冻结到实例。没有注册 interceptor 时，调用仍直接进入生成的泛型 invoker/stub，不构建 delegate 链：

```csharp
var client = SharpClientBuilder.Create()
    .UseTcp("rpc.example.internal", 5000)
    .AddInterceptor(clientInterceptor)
    .Build();

var server = SharpLinkServerBuilder.Create()
    .AddService<IMyService, MyService>()
    .UseTcp(5000)
    .AddInterceptor(serverInterceptor)
    .UseExceptionMapper(exceptionMapper)
    .Build();
```

客户端 interceptor 可通过 `SharpLinkClientInvocationContext.Options` 增加 metadata，也可以直接返回 `SharpLinkClientInvocationResult` 短路调用。服务端 context 包含 method descriptor、request ID、deadline、metadata、peer、auth、status 和 elapsed，可用于授权、限流与审计。

默认异常 mapper 会保留显式的 `SharpLinkException`；其他业务异常只向客户端返回 `Internal` 与通用消息，Unary 和 stream 使用同一规则。仅在受控开发环境中可显式调用 `EnableDetailedErrors()`。生产环境建议实现 `IRpcExceptionMapper`，只公开经过审核的业务状态与消息。

`[Idempotent]` 只把重试资格写入生成的 `RpcMethodDescriptor`，核心不会自动重试；后续 Resilience 扩展也只允许显式标记的 Unary 方法参与重试。

## OpenTelemetry

`SharpLinkTelemetry` 暴露两个 ActivitySource 和一个 Meter，可直接加入现有 OpenTelemetry pipeline：

```csharp
tracerProviderBuilder
    .AddSource("SharpLink.Client", "SharpLink.Server");

meterProviderBuilder
    .AddMeter("SharpLink");
```

内置指标覆盖 active connections、reconnect、started/completed/failed/active/abandoned calls、duration、sent/received bytes、send queue bytes、pending requests、active streams、迟到响应，以及 protocol/auth/resource-exhausted failures。`sharplink.calls.abandoned` 使用 `rpc.sharplink.termination_reason` 区分 deadline、远端取消、consumer abandoned、停机与断连；`sharplink.responses.late_dropped` 逐次记录被安全丢弃的迟到响应。Activity 和指标不记录完整 payload、token、证书或未审核的业务异常消息。没有 listener 时不会创建 TagList、Activity、Stopwatch 对象或额外调用 observer。

## DI、健康检查与优雅排空

默认 `AddService<TContract,TService>()` 使用 Singleton，保留基础 RPC 的无 scope 快路径。需要调用级依赖时可显式选择 Scoped 或 Transient；scope 会持续到 Unary/OneWay 完成，stream 则持续到整条流完成、取消或断线：

```csharp
services.AddScoped<MyService>();
services.AddSharpLinkServer(server => server
    .AddService<IMyService, MyService>(ServiceLifetime.Scoped)
    .UseTcp(5000));
```

Hosting 会把匹配 lifetime 的服务注册加入宿主容器；如果应用已注册同一实现类型，则 lifetime 必须一致。非 Hosting 场景可使用 `UseServiceProvider(provider)`。也可以注册调用方持有的 singleton 实例，或注册由 SharpLink 管理生命周期的 provider-aware factory：

```csharp
serverBuilder
    .AddService<IMyService>(existingInstance)
    .AddService<IOtherService>(
        sp => new OtherService(sp.GetRequiredService<Dependency>()),
        ServiceLifetime.Transient);
```

实例重载不会释放调用方对象；类型注册由 DI scope/provider 释放；factory 返回值由 SharpLink 在对应生命周期边界释放。

客户端可以直接使用协议控制帧检查远端状态，不需要定义业务契约：

```csharp
var health = await client.CheckHealthAsync(cancellationToken);
if (health.Status != SharpLinkHealthStatus.Ready)
    throw new InvalidOperationException($"RPC server is {health.Status}.");
```

`ISharpLinkServer.HealthStatus` 暴露本地 `Ready/Draining/Unhealthy`。`AddSharpLinkServer` 与 `AddSharpLinkClient` 分别注册 `sharplink_server` 和 `sharplink_remote` Microsoft health checks，并带有 `ready` tag。停机顺序固定为 readiness=false、停止 accept、发送 GoAway、等待 active calls、超时后取消、flush 必要控制帧、释放 session/listener/service scope/provider。

## 可调优配置

- 日志：`UseLoggerFactory(...)`
- 心跳：`UseHeartbeat(...)`
- 握手认证：`ISharpLinkClientAuthenticator` / `ISharpLinkServerAuthenticator` 与 `RequireAuthentication()`
- 调用管线：Client/Server `AddInterceptor(...)` 与 Server `UseExceptionMapper(...)`
- 遥测：`SharpLinkTelemetry.ClientActivitySource`、`ServerActivitySource` 与 `Meter`
- 服务生命周期：`AddService(instance/type/factory)`、`UseServiceProvider(...)` 与 `ServiceLifetime`
- 健康检查：`CheckHealthAsync()`、`ISharpLinkServer.HealthStatus` 与 Hosting health checks
- 请求超时：`UseRequestTimeout(...)`；需要真正无默认超时时使用 `DisableRequestTimeout()`
- `RpcSession` flush：`UseRpcSessionFlush(...)`
- 实例级 Buffer Writer Pool：`UseBufferWriterPool(...)`
- 运行时并发容器：`UseStateStoreConcurrency(...)`
- 性能预设与流控边界：`UseRuntime(options => options.PerformanceProfile = SharpLinkPerformanceProfile.LowLatency)`
- 客户端连接池：`UseConnectionPool(options => { options.MinConnections = 1; options.MaxConnections = 4; })`

客户端默认使用 `1/1` 单连接池，单连接选择路径不产生随机选择或临时集合。只有在已有连接承载在途请求时，池才会按压力异步扩容；多连接使用 power-of-two choices 比较在途请求数。stream 在创建时固定到同一连接，收到 `GoAway` 的连接停止接收新调用并在在途请求归零后退出。`Throughput` 预设在用户未显式配置连接池时使用 `1/min(Environment.ProcessorCount, 4)`，其他预设保持 `1/1`。

`AnonymousPipe` 的一次句柄 offer 只支持一个客户端连接，因此其 `MaxConnections` 必须为 `1`。

### 0.6.6 兼容入口迁移

0.6.6 删除了会跨 Client/Server 实例互相覆盖状态的进程级兼容入口：

- `RpcCodecRegistry` / `RpcCodec`：业务配置迁移到 Client/Server Builder 的 `UseCodec<T>(...)` 或 `UseSerializer(...)`；底层组件从所属 `IRpcRuntimeContext.Codecs` 解析 Codec。
- `BufferWriterPool`：容量和保留策略迁移到 Builder 的 `UseBufferWriterPool(...)`；框架内部从所属 Context 的 `Buffers` 租借和归还。独立工具代码可直接使用 `PooledByteBufferWriter`。
- `RuntimeConcurrency`：迁移到每个 Builder 的 `UseStateStoreConcurrency(...)`。
- 旧的 Client 调用排列组合入口已删除；业务调用只通过 Source Generator 代理，底层扩展只实现 `IRpcChannel` 的五类 invoker。

这些配置在 `Build()` 时冻结；同进程的不同 Client/Server 可以使用不同 Codec、Pool 和并发参数而互不污染。

如果你使用 `UseTcp(0, "127.0.0.1")` 让系统自动分配端口，可以在 `Build()` 前通过 `serverBuilder.Transport.LocalEndPoint` 读取实际监听端口。

## 文档

- 计划：`doc/plan.md`
- 架构：`doc/architecture.md`
- 待办与改进方向：`doc/todo.md`
- 压测：`doc/loadtest.md`
- Protocol v2：`doc/protocol-v2.md`
- 0.6.10 迁移：`doc/migration-0.6.10.md`
- 0.6.10 性能与 Chaos：`doc/performance-0.6.10.md`、`doc/chaos-0.6.10.md`
- 贡献指南：`CONTRIBUTING.md`
- 更新日志：`CHANGELOG.md`
