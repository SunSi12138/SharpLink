# SharpLink

一个面向 .NET 的高性能 RPC 框架（当前主目标框架为 `net10.0`），支持：

- Source Generator 自动生成 `Proxy/Stub/Codec/Assembly Manifest`
- Unary、`[Oneway]`、客户端流、服务端流、双向流、多流参数
- Protocol v2 协议级取消（`ProtocolV2FrameType.Cancel`）
- TLS/mTLS、认证、Interceptor、deadline、背压、健康检查与 OpenTelemetry
- 自动服务注册、`Singleton/Connection/Call` 生命周期、运行时程序集安全注册/注销
- `Microsoft.Extensions.Hosting`、DI、readiness 与优雅排空
- `Socket / NamedPipe / AnonymousPipe / UDS` 传输，以及实验性的同用户共享内存传输
- 内置无反射 DTO Codec，并通过通用 Codec Adapter 接入 `SharpPack` 等复杂图序列化器

## 项目结构

核心项目（`src/`）：

- `SharpLink.Abstractions`：Protocol v2 公共模型、公共接口、通道与传输抽象
- `SharpLink.Runtime`：`RpcSession`、`StreamManager`、实例级 Codec Provider、传输实现与底层收发逻辑
- `SharpLink.Sdk`：`IService`、`[RpcContract]`、`[RpcService]`、`[Oneway]`、`[Timeout]` 等契约标记
- `SharpLink.Client`：客户端 Builder、连接生命周期、请求管理与代理调用通道
- `SharpLink.Server`：服务端 Builder、连接管理、Stub 分发、心跳与取消处理
- `SharpLink.Hosting`：`IServiceCollection` 扩展与 HostedService 集成
- `SharpLink.Generator`：契约/服务分析器与 `Proxy/Stub` 代码生成
- `SharpLink.Serializer.SharpPack`：精确依赖 SharpPack `[1.1.0]` 的 Codec Adapter（`memorypack-binary/v1`）

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
- 契约及其 containing type 必须 public；公开 nested contract 受支持并获得确定性唯一生成类型名
- RPC route 必须是普通 instance method；`ref/out/in`、by-ref return、static method 与 abstract property/indexer/event 会在编译期报告错误
- Contract 所在程序集生成 Descriptor、Proxy、contract-based Stub 和 Codec；Service 所在程序集生成 Activator、生命周期与显式依赖
- 每个生成程序集只有一个可由程序集特性直接定位的 Manifest，不使用 `Assembly.GetTypes()` 扫描
- Server `Build()` 默认快照当时已加载的 Manifest 并自动注册 `[RpcService]`；Build 后加载的插件需要显式 `RegisterAssembly`
- 可以通过程序集级特性缩小扫描范围：

```csharp
[assembly: SharpLinkRpcContracts(typeof(MyContract1), typeof(MyContract2))]
```

## Multi-cluster clients

`SharpLinkMultiClusterClientBuilder` owns several isolated child clients. A contract is mapped to a
single child while its proxy is created; RPC calls made through that proxy do not consult a
coordinator, cluster name, or per-call routing context.

Declare static contract-assembly routes in the application or host assembly, not in a reusable
contract package:

```csharp
[assembly: SharpLinkClusterContractAssembly("orders", typeof(OrderContractsMarker))]
[assembly: SharpLinkClusterContractAssembly("payments", typeof(PaymentContractsMarker))]
```

Configure each slot with the existing child builder API. The `UseCluster` method inside the delegate
still configures endpoint topology for that one slot; it is not the multi-cluster coordinator API.

```csharp
var client = SharpLinkMultiClusterClientBuilder.Create()
    .AddCluster("orders", child => child.UseTcp("127.0.0.1", 5101))
    .AddCluster("payments", child => child.UseTcp("127.0.0.1", 5102))
    .Build();

await client.ConnectAsync();
var orders = client.Get<IOrderService>();
var payments = client.Get<IPaymentService>();
```

Every slot is required by default. A slot intentionally reserved for plugins must explicitly opt in:

```csharp
.AddCluster("plugins", child => child.UseTcp("127.0.0.1", 5103),
    slot => slot.AllowDynamicContracts = true)
```

Dynamic contracts are registered against an explicit slot with
`RegisterAssembly(cluster, assembly)`, `UnregisterAssemblyAsync(cluster, assembly, timeout)`, and
`ReplaceAssemblyAsync(cluster, oldAssembly, newAssembly, timeout)`. There is no default cluster,
per-call cluster override, cross-cluster retry, or cluster identifier on the wire. See
`doc/architecture-0.7.10.md` and `doc/migration-0.7.10.md` for lifecycle and migration details.

## 契约 Manifest 与兼容性基线

`SharpLink.Sdk` 包会把当前契约写到 `obj/<configuration>/<tfm>/SharpLink.Contracts.sharplink.json`。JSON 按 Contract、Method、DTO member、enum、union 与 Service route 的稳定 ID 排序，不包含时间戳或源码路径；`schemaFingerprint` 覆盖规范化后的完整内容，可直接作为 CI 构建产物保存。

把上一个已发布版本的文件保存到仓库，并在项目中指定基线：

```xml
<PropertyGroup>
  <SharpLinkContractBaseline>contracts/previous.sharplink.json</SharpLinkContractBaseline>
  <!-- 可选：覆盖当前 Manifest 的输出位置 -->
  <SharpLinkContractManifestOutput>artifacts/contracts/current.sharplink.json</SharpLinkContractManifestOutput>
</PropertyGroup>
```

没有基线时只生成当前 Manifest。存在基线时，`SHARPLINK024`–`SHARPLINK035` 与 `SHARPLINK037` 会在可用的 Contract、Method、DTO member 或 Service 位置报告格式错误和破坏性变化，并在消息中给出修复方式。例如 DTO 成员重命名应显式保留旧 ID：

```csharp
public sealed class Customer
{
    [RpcMember(7)] // 重命名前后都保留 7
    public string DisplayName { get; init; } = string.Empty;
}
```

新增 Contract、Method 和 optional DTO member 是兼容变化。多态契约可用 `[RpcUnionCase(tag, typeof(CaseType))]` 固定 union tag；已发布的 tag 不能改派给其他类型。分析全部发生在编译期，不进入运行时路由或 RPC 热路径，NativeAOT 继续使用生成代码而不做反射扫描。

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

循环/多态对象图和第三方运行时类型可以交给编译期选择的 Codec Adapter。引用 `SharpLink.Serializer.SharpPack` 后，`[SharpPackable]` 会自动选择 SharpPack Adapter；普通 DTO 仍优先使用 SharpLink 原生 Codec：

```csharp
using SharpPack;

[SharpPackable]
public partial class PluginGraph
{
    public PluginGraph? Parent { get; set; }
    public List<PluginGraph> Children { get; set; } = [];
}
```

没有框架自带 Attribute 的第三方类型使用通用显式绑定：

```csharp
[assembly: RpcCodecAdapter(
    typeof(ThirdPartyGraph),
    typeof(SharpPackRpcCodecAdapter))]
```

Client/Server 不需要 resolver 或手工注册自动 Adapter Codec。高级自定义 formatter 可由调用方创建 `SharpPackSerializerContext`，再通过 `SharpPackRpcCodec.Create<T>(context)` 显式 `UseCodec`；该 Codec 仍保持最高优先级且 Context 所有权属于调用方。

每个 Adapter Scope 按 `Runtime Context × generated Manifest × AdapterId` 隔离。同一 Manifest 的闭合类型共享一个 SharpPack Context；自动 Context 拥有独立 formatter graph，不使用进程级默认 formatter slot，不同 Client/Server、插件或替换代际不共享。进程 Catalog 只保存弱 Manifest 引用；动态模块排空后释放 Codec、Scope 和 Context。生成代码直接调用闭合 `CreateCodec<T>()`，不扫描程序集、不调用 `MakeGenericType` 或 `Activator.CreateInstance`。详细设计见 [`doc/architecture-0.7.11.md`](doc/architecture-0.7.11.md)；升级 0.8.x 前请阅读 [`doc/migration-0.8.36.md`](doc/migration-0.8.36.md)。

## 协商压缩

压缩默认完全关闭。Client 与 Server 分别按本地偏好注册 Provider；握手有交集时 Server 选择自身列表中的第一个 wire profile，没有交集或只有一端启用时自动发送原始帧：

```csharp
var server = SharpLinkServerBuilder.Create()
    .UseTcp(5000)
    .UseRuntime(options =>
    {
        options.Compression.Providers.Add(
            SharpLinkCompressionProviders.CreateBrotli());
        options.Compression.MinimumPayloadBytes = 2048;
        options.Compression.MinimumSavingsBytes = 96;
        options.Compression.MinimumSavingsRatio = 0.08;
    })
    .Build();
```

内置 Provider 只提供框架自带的 Brotli，并允许为每个方向选择 `CompressionLevel`。Gzip、Deflate、Zstandard 或其他格式可通过自定义 `ISharpLinkCompressionProvider` 接入。Provider 的 `WireProfile` 必须是唯一的 1–64 字节规范 ASCII；dictionary identity 等影响解码的配置必须进入 profile，只影响编码成本的 level 不协商。例如，同一 Zstandard 实现可以分别注册 `zstd/v1` 与 `zstd-dict/0123abcd`。实现必须线程安全、NativeAOT 安全，并准确返回 consumed/written bytes。压缩只覆盖业务 payload，路由、deadline、metadata 与 stream ID 保持未压缩；默认收益门槛为 1024 B、64 B 和 5%。完整 wire 格式和故障域见 [`doc/protocol-v2.md`](doc/protocol-v2.md)。

压缩在连接握手后按每个方向自动应用，不存在 per-call 强制开关；需要控制是否尝试压缩时，应在对应 Client/Server Runtime Context 配置 Provider 或调整 payload/收益阈值。

## 主动接入控制

服务端可在创建 Service、DI Scope、Codec 调用状态和执行 Interceptor 之前启用累计 admission 规则。默认完全关闭；启用后依次取得 `Global → Contract → Method → Partition` 中所有已配置的 permit，现有每连接和进程硬并发上限仍作为最后安全边界：

```csharp
var server = SharpLinkServerBuilder.Create()
    .UseTcp(5000)
    .UseAdmissionControl(options =>
    {
        options.Global.UseConcurrency(256);
        options.MaxQueuedCalls = 512;
        options.MaxQueuedBytes = 16 * 1024 * 1024;
        options.MaxQueueDelay = TimeSpan.FromSeconds(2);
        options.AddMethod<IOrders>(nameof(IOrders.SubmitAsync), method =>
            method.UseTokenBucket(rate =>
            {
                rate.TokenLimit = 1_000;
                rate.TokensPerPeriod = 1_000;
                rate.ReplenishmentPeriod = TimeSpan.FromSeconds(1);
            }));
        options.UsePartition(
            context => context.Metadata is { Count: > 0 } metadata ? metadata[0].Value : null,
            partition =>
            {
                partition.MaxPartitions = 1_024;
                partition.IdleTimeout = TimeSpan.FromMinutes(5);
                partition.UseConcurrency(8);
            });
    })
    .Build();
```

速率策略可选 TokenBucket、FixedWindow 或 SlidingWindow，公共 API 不暴露底层 `System.Threading.RateLimiting` 类型。所有自动计时周期最多为 2,147,483,647 ms；SlidingWindow 的每个 segment 必须至少覆盖一个 `TimeSpan` tick。等待队列同时受调用数、保留字节、最长等待、调用 deadline、取消、断连和 Server Draining 限制；任一容量不足立即返回 `ResourceExhausted`。分区键为空时进入明确的默认分区，池满且没有安全可回收的空闲项时按 `partition_capacity` 拒绝，不记录真实分区键。

OneWay 默认不排队；被过载策略拒绝时服务方法不会执行，只记录 dropped/resource-exhausted 指标和限频日志。`QueueOneWayCalls=true` 才允许它进入相同有界队列。客户端本地 `await` OneWay 成功只表示 SendPump 接受了帧，不代表服务端已经执行。

Admission 指标为 `sharplink.admission.permits.active`、`calls.queued`、`calls.rejected`、`queue.duration`、`oneway.dropped` 与 `partitions.active`；拒绝只使用低基数 `scope`/`reason`。功能未启用时普通调用不创建 admission 状态、Task、TagList 或后台任务。

## 传输说明

- `NamedPipe` 在 Unix/macOS 下最终会映射到 Unix Domain Socket 路径
- 当前运行时会对超长 pipe name 做确定性缩短，避免触发平台路径长度限制
- NamedPipe 的未定义 `PipeOptions` bit 或 `PipeTransmissionMode` 会在 factory/listener 构造时立即拒绝；client 也拒绝仅供 server 使用的 `FirstPipeInstance`
- TCP keep-alive time/interval 的最大值为 2,147,483,647 秒，配置会在创建 socket 前冻结并校验
- `AnonymousPipe` 当前已覆盖本机连接、断连与本机压测回归；仓库内置 LoadTest 仅支持 `--mode local`
- 每组 AnonymousPipe handle 从首次连接尝试开始即为已消费；失败重试必须申请新 offer
- 若自行基于 `IAnonymousPipeAllocator` 将句柄转交外部子进程，应在子进程继承两个 handle 后立即调用 `offer.CompleteHandleTransfer()`（或释放 offer），让 Server 能观察子进程断连；同进程直接包装这些 handle 时不要提前完成交接

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

## 自动服务注册、DI 与生命周期

服务实现只需标记 `[RpcService]`。默认 `Singleton` 保留无调用 Scope 的快速路径；`Connection` 按认证成功的物理连接惰性创建，`Call` 为每次调用创建，并在完整 Unary、OneWay 或 Streaming 调用真正结束后释放：

```csharp
[RpcService(Lifetime = SharpLinkServiceLifetime.Connection)]
public sealed class MyService(Dependency dependency) : IMyService
{
    // Generated activator resolves Dependency from the current scope provider.
}

var server = SharpLinkServerBuilder.Create()
    .UseServiceProvider(provider)
    .UseTcp(5000)
    .Build();
```

可以按 Builder 排除、重新启用或只启用白名单服务。调用方传入的实例始终是 caller-owned Singleton；factory 产物由 SharpLink 按指定生命周期释放：

```csharp
serverBuilder
    .ExcludeService<IMyService>()
    .EnableService<IMyService>()
    .ReplaceService<IMyService>(existingInstance)
    .ReplaceService<IOtherService>(
        sp => new OtherService(sp.GetRequiredService<Dependency>()),
        SharpLinkServiceLifetime.Call);
```

`DisableAutomaticServiceRegistration()` 可切换为 `EnableService<TContract>()` 白名单模式。`EnableService` 找不到生成服务时 Build 失败，`ExcludeService` 找不到目标时无操作。Hosting 与 `UseServiceProvider` 继续管理普通依赖的生命周期，但根 RPC 服务的公共生命周期只由 `SharpLinkServiceLifetime` 定义。

### 运行时程序集注册与注销

Build 后加载的插件需要分别注册到使用其 Artifact 的 Client/Server。注册不会用异常表示预期失败，而是原子返回结构化诊断；只有完整 Manifest 验证通过才会发布：

```csharp
SharpLinkAssemblyRegistrationResult registration = server.RegisterAssembly(pluginAssembly);
if (!registration.Succeeded)
    Console.Error.WriteLine($"{registration.Error!.Code}: {registration.Error.Message}");

SharpLinkAssemblyUnregisterResult drained = await server.UnregisterAssemblyAsync(
    pluginAssembly,
    TimeSpan.FromSeconds(10),
    cancellationToken);

SharpLinkAssemblyReplacementResult replaced = await server.ReplaceAssemblyAsync(
    pluginAssembly,
    nextPluginAssembly,
    TimeSpan.FromSeconds(10),
    cancellationToken);
if (!replaced.Succeeded)
    Console.Error.WriteLine($"{replaced.Error!.Code}: {replaced.Error.Message}");
```

`ReplaceAssemblyAsync` 在修改线上状态前完成新 Manifest、Codec、Stub、Service 与 route 验证；旧 registration 拥有的 route 可由新程序集接管，但第三方 registration 的 route 仍受冲突保护。提交时只发布一次新不可变路由快照，随后复用注销路径排空旧调用。已进入旧 registration 的 Unary 和 Stream 固定使用旧 Codec、Stub、Service 与 Scope；新请求只读取新快照。

普通注销的排空期间路由继续由原模块占有，新调用得到 `Unavailable: RPC module is draining`。替换和注销超时都会定点取消旧模块调用和流；业务代码不配合取消时 `ReferencesReleased=false`，框架在计数最终归零后后台完成释放。Client API 语义相同。NativeAOT 的运行时注册与替换返回 `PlatformNotSupported`，静态 Manifest 路径不受影响。

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
- 服务注册与生命周期：`[RpcService]`、`EnableService` / `ExcludeService` / `ReplaceService`、`UseServiceProvider(...)` 与 `SharpLinkServiceLifetime`
- 运行时插件：Client/Server `RegisterAssembly(...)` 与 `UnregisterAssemblyAsync(...)`
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
- 0.7.1 迁移：`doc/migration-0.7.1.md`
- 0.7.2 性能与迁移：`doc/performance-0.7.2.md`、`doc/migration-0.7.2.md`
- 0.7.4 压缩、接入控制与性能：`doc/migration-0.7.4.md`、`doc/performance-0.7.4.md`
- 0.7.5 静态多 endpoint：`doc/architecture-0.7.5.md`、`doc/performance-0.7.5.md`
- 0.7.6 动态 endpoint、Resolver 与 DNS Discovery：`doc/architecture-0.7.6.md`
- 0.7.6 本地性能证据：`doc/performance-0.7.6.md`
- 0.7.7 logical call、attempt 与 retry：`doc/architecture-0.7.7.md`
- 0.7.8 endpoint admission 与 circuit breaker：`doc/architecture-0.7.8.md`
- 0.7.9 迁移、组合验证与 API freeze：`doc/migration-0.7.9.md`
- 0.7.9 本地性能与组合 smoke：`doc/performance-0.7.9.md`
- 0.8.36 Server 停止、配置优先级与协议/API 边界审核：`doc/audit-0.8.36.md`、`doc/migration-0.8.36.md`、`doc/performance-0.8.36.md`
- 0.8.35 Resolver、协议终止与双端 Chaos 门禁审核：`doc/audit-0.8.35.md`、`doc/migration-0.8.35.md`、`doc/performance-0.8.35.md`
- 0.8.34 共享内存、Chaos 门禁与继承契约审核：`doc/audit-0.8.34.md`、`doc/migration-0.8.34.md`、`doc/performance-0.8.34.md`
- 0.8.33 生成器、Builder 回滚与 Hosted 生命周期审核：`doc/audit-0.8.33.md`、`doc/migration-0.8.33.md`、`doc/performance-0.8.33.md`
- 0.8.32 运行时边界与 admission 热路径审核：`doc/audit-0.8.32.md`、`doc/migration-0.8.32.md`、`doc/performance-0.8.32.md`
- 0.8.31 Transport 所有权与 API 边界审核：`doc/audit-0.8.31.md`、`doc/migration-0.8.31.md`、`doc/performance-0.8.31.md`
- 0.6.10 性能与 Chaos：`doc/performance-0.6.10.md`、`doc/chaos-0.6.10.md`
- 贡献指南：`CONTRIBUTING.md`
- 更新日志：`CHANGELOG.md`
