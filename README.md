# SharpLink

<p align="center">
  <img src="assets/sharplink-icon.png" alt="SharpLink 图标" width="128" height="128" />
</p>

[![PR Quick](https://github.com/SunSi12138/SharpLink/actions/workflows/pr-quick.yml/badge.svg)](https://github.com/SunSi12138/SharpLink/actions/workflows/pr-quick.yml)
[![Nightly Regression](https://github.com/SunSi12138/SharpLink/actions/workflows/nightly.yml/badge.svg)](https://github.com/SunSi12138/SharpLink/actions/workflows/nightly.yml)
[![License: MIT](https://img.shields.io/badge/License-MIT-yellow.svg)](LICENSE)

SharpLink 是一个面向 .NET 10 的高性能 RPC 框架。契约、代理、Stub 和 DTO Codec 由 Source Generator 在编译期生成；运行时支持 Unary/Streaming、TLS、deadline、取消、背压、服务发现、韧性、OpenTelemetry 和优雅排空。

## 安装与 package map

当前 `dev` 的发布版本线为 `2.0.0`。第一次使用时按项目职责安装包：

| Project role | Install | Why |
| --- | --- | --- |
| Contracts | `SharpLink.Sdk` | 契约 Attribute/类型、`SharpLink.Abstractions` 依赖，以及随 SDK 分发的 Analyzer/Generator |
| Server | `SharpLink.Server` + `SharpLink.Sdk` | Server runtime，以及当前 Server 编译中的 service/bootstrap 生成 |
| Client | `SharpLink.Client` + `SharpLink.Sdk` | Client runtime，以及当前 Client 编译中的静态 manifest/bootstrap 生成 |
| Host/DI（可选） | `SharpLink.Hosting` | `Microsoft.Extensions.Hosting` / DI 集成 |

`SharpLink.Sdk` 的 NuGet 包会把 `SharpLink.Generator.dll` 放在 `analyzers/dotnet/cs`，所以通常**不要**再单独安装 `SharpLink.Generator`。SDK 传递依赖 `SharpLink.Abstractions`，**不依赖 `SharpLink.Runtime`**；纯 Contracts 项目不需要为了定义 RPC contract 引入完整 Runtime。

## Quick Start：Contracts → Server → Client

要求：.NET 10 SDK。

从空目录创建三个项目：

```bash
mkdir SharpLinkQuickStart
cd SharpLinkQuickStart

dotnet new classlib -n QuickStart.Contracts -f net10.0
dotnet new console -n QuickStart.Server -f net10.0
dotnet new console -n QuickStart.Client -f net10.0

dotnet add QuickStart.Contracts package SharpLink.Sdk --version 2.0.0

dotnet add QuickStart.Server reference QuickStart.Contracts/QuickStart.Contracts.csproj
dotnet add QuickStart.Server package SharpLink.Sdk --version 2.0.0
dotnet add QuickStart.Server package SharpLink.Server --version 2.0.0

dotnet add QuickStart.Client reference QuickStart.Contracts/QuickStart.Contracts.csproj
dotnet add QuickStart.Client package SharpLink.Sdk --version 2.0.0
dotnet add QuickStart.Client package SharpLink.Client --version 2.0.0
```

### 1. Contracts

Canonical source: [`samples/QuickStart.Contracts/GreetingContracts.cs`](samples/QuickStart.Contracts/GreetingContracts.cs)

最小契约只需要业务接口、DTO 和协作取消 token：

```csharp
[RpcContract]
public interface IGreetingService : IService
{
    ValueTask<GreetingReply> GreetAsync(
        GreetingRequest request,
        CancellationToken cancellationToken);
}
```

把 canonical source 复制到 Contracts 项目即可。这里不需要理解 `RuntimeContext`、Manifest、Assembly Catalog 或 generated ABI；这些属于架构/高级章节。

### 2. Server

Canonical source: [`samples/QuickStart.Server/Program.cs`](samples/QuickStart.Server/Program.cs)

Server 项目引用 Contracts，`[RpcService]` 实现业务接口，然后配置 listener 并显式启动本地 serving runtime：

```csharp
await using var server = SharpLinkServerBuilder.Create()
    .UseTcp(50051, IPAddress.Loopback)
    .Build();

await server.StartAsync();
var terminal = server.WaitForShutdownAsync();
```

Server 的 canonical lifecycle 是 `StartAsync / WaitForShutdownAsync / StopAsync`。`StartAsync` 只负责启动，`WaitForShutdownAsync` 只观察真实终态；canonical sample 的 Ctrl+C 路径显式调用 `StopAsync(TimeSpan.FromSeconds(5))` 发送 GoAway 并排空活动调用，然后等待 `terminal` 完成。

### 3. Client

Canonical source: [`samples/QuickStart.Client/Program.cs`](samples/QuickStart.Client/Program.cs)

Client 建连、等待 Ready、获取生成代理并发起一次真实 Unary RPC：

```csharp
await using var client = SharpClientBuilder.Create()
    .UseRequestTimeout(TimeSpan.FromSeconds(5))
    .UseTcp("127.0.0.1", 50051)
    .Build();

await client.ConnectAsync(timeout.Token);
await client.WaitForReadinessAsync(1, timeout.Token);

var greeting = client.Get<IGreetingService>();
var reply = await greeting.GreetAsync(
    new GreetingRequest { Name = "SharpLink" },
    timeout.Token);
```

先在终端 1 启动 Server，再在终端 2 启动 Client：

```bash
dotnet run --project QuickStart.Server
dotnet run --project QuickStart.Client
```

Client 应输出：

```text
QUICKSTART_CLIENT_PASS response=Hello, SharpLink!
```

Client sample 在退出前显式 `StopAsync()`，并继续由 `await using` 做幂等释放。Server 用 Ctrl+C 进入 5 秒优雅排空。

仓库中的三个 [`samples/QuickStart.*`](samples/) 项目是这段入门的事实源。Release package smoke 会把它们复制到临时空目录，只使用本地 `.nupkg` + `PackageReference` + fresh NuGet cache 构建三项目，并实际启动 Server/Client 完成上述 RPC；README 不维护另一份完整 sample。

### Semantic Quick Reference

| 用户问题 | 简短答案 | 进一步阅读 |
| --- | --- | --- |
| timeout/deadline 覆盖什么？ | 一个 RPC logical deadline 从调用创建开始，约束 endpoint admission/reselection、deadline-bearing request emission、response/stream lifetime、retry 与 backoff；generated Unary 的 pending table 满时默认立即本地 `ResourceExhausted`，不会排队等 slot。此前的 `ConnectAsync`、transport dial、handshake、`WaitForReadinessAsync` **不计入这个 RPC deadline**；handshake 有独立 `HandshakeTimeout`。 | [`doc/public-rpc-semantics.md`](doc/public-rpc-semantics.md)、[`doc/calls-and-streaming.md`](doc/calls-and-streaming.md) |
| `ConnectAsync` 成功意味着什么？ | 它完成 topology 自己的 connectivity 边界，不等于所有 endpoint fully ready。启动流量前必须要求 N 个 Ready endpoint 时，显式 `WaitForReadinessAsync(N)`。 | [`doc/resilience.md`](doc/resilience.md) |
| `await OneWay` 成功意味着什么？ | 只说明本地发送边界成功：无 deadline 的普通 OneWay 到 SendPump admission；带 deadline 的 OneWay 还观察 transport flush。它不证明 Server 收到、handler 执行或副作用已提交；需要远端成功确认时使用 request/response RPC。 | [`doc/public-rpc-semantics.md`](doc/public-rpc-semantics.md)、[`doc/calls-and-streaming.md`](doc/calls-and-streaming.md) |
| replacement 后旧 proxy 怎样？ | `ReplaceClusterAsync` 前取得的 multi-cluster proxy 固定绑定旧 child，要使用新 child 必须重新 `Get<T>()`；server-side module/service replacement 与 endpoint topology/policy 更新不会要求重取普通 client proxy，但已开始的 call/physical attempt 不会中途迁移。 | [`doc/public-rpc-semantics.md`](doc/public-rpc-semantics.md)、[`doc/dynamic-modules-and-multicluster.md`](doc/dynamic-modules-and-multicluster.md) |
| timeout/disconnect 后能直接 retry？ | 自动 retry 仅适用于 `[Idempotent]` Unary，并共享原 logical deadline；默认只重试 `Unavailable` / `ConnectionClosed`。timeout 或 disconnect **不证明 Server 没执行过请求**，因此只有业务上可安全重复的操作才应声明幂等并允许重试。 | [`doc/public-rpc-semantics.md`](doc/public-rpc-semantics.md)、[`doc/resilience.md`](doc/resilience.md) |

## Production-shaped template

最小 Quick Start 刻意不塞生产选项。可复制作为真实服务起点的完整模板位于：

- [`samples/ProductionTemplate.Contracts`](samples/ProductionTemplate.Contracts/)
- [`samples/ProductionTemplate.Server`](samples/ProductionTemplate.Server/)
- [`samples/ProductionTemplate.Client`](samples/ProductionTemplate.Client/)

Server 模板覆盖 TLS、连接/调用 admission、pending/stream 上限、结构化日志、SharpLink ActivitySource 观测和 30 秒 graceful drain；Client 模板覆盖 TLS hostname 校验、请求 timeout、pending 上限、日志/trace、Ready 与显式 Stop。

模板不会生成或信任测试证书。Server 从 deployment 提供的 PKCS#12 读取证书：

```bash
export SHARPLINK_TLS_CERT_PATH=/run/secrets/rpc-server.pfx
export SHARPLINK_TLS_CERT_PASSWORD='...'
dotnet run --project samples/ProductionTemplate.Server
```

Client 默认连接 `127.0.0.1:50052`，TLS `TargetHost` 默认是 `localhost`；部署环境可以显式提供：

```bash
export SHARPLINK_SERVER_IP=10.0.0.12
export SHARPLINK_TLS_TARGET_HOST=rpc.example.internal
dotnet run --project samples/ProductionTemplate.Client
```

没有设置自定义证书 callback 时，SharpLink/.NET 保持平台证书链和 hostname 校验。模板故意不提供“接受所有证书”的捷径。

下面区分模板值、framework default 和必须由 deployment 决定的值：

| Concern | Template value | Framework default | Deployment decision |
| --- | ---: | ---: | --- |
| Unary fallback timeout | 5 s | `UseRequestTimeout()` 推荐 30 s；Client Build 前需显式选择 timeout policy | 按服务 SLO/上游 deadline 调整 |
| TLS handshake timeout | 5 s | 10 s | 按网络与证书基础设施调整 |
| Client/server pending requests / connection | 1,024 | 65,536 | 按内存预算、并发和排队策略调整 |
| Concurrent server calls | 256 | active call admission 默认关闭 | 按 CPU/下游容量调整 |
| Queued calls | 512，最长 2 s | admission/queue 默认关闭 | 明确容量、字节预算和 deadline |
| Live connections / handshakes | 512 / 32 | 1,024 / 64 | 按连接风暴与资源预算调整 |
| Graceful drain | 30 s | 无统一部署默认值 | 必须覆盖典型最长正常请求，同时受平台终止窗口约束 |
| Logging/telemetry | Console + SharpLink ActivitySource listener | 框架只暴露结构化日志、`SharpLink.Client`/`SharpLink.Server` ActivitySource 与 `SharpLink` Meter | exporter、采样、日志后端由部署决定 |

如果应用已经使用 OpenTelemetry，可以直接把 SharpLink 接入现有 pipeline：

```csharp
tracerProviderBuilder.AddSource("SharpLink.Client", "SharpLink.Server");
meterProviderBuilder.AddMeter("SharpLink");
```

生产模板当前绑定 loopback，只为避免示例替用户决定网络暴露面和认证方案。真正跨主机部署时，应同时明确 listen address、TLS 证书/SNI、认证授权、网络策略和 readiness；见 [`doc/security.md`](doc/security.md) 与 [`doc/transports.md`](doc/transports.md)。

## 语义与功能文档

README 只负责把第一次 RPC 跑通。完整语义以这些文档为准：

- 公开 RPC 语义 / code-review contract：[`doc/public-rpc-semantics.md`](doc/public-rpc-semantics.md)
- 文档首页：[`doc/index.md`](doc/index.md)
- 入门与核心模型：[`doc/getting-started.md`](doc/getting-started.md)
- 契约、DTO、Codec：[`doc/contracts-and-codecs.md`](doc/contracts-and-codecs.md)
- Unary/Streaming、取消与 deadline：[`doc/calls-and-streaming.md`](doc/calls-and-streaming.md)
- TCP/TLS 与其他传输：[`doc/transports.md`](doc/transports.md)
- 安全、认证与授权：[`doc/security.md`](doc/security.md)
- 服务发现、Retry 与 Circuit Breaker：[`doc/resilience.md`](doc/resilience.md)
- Server admission：[`doc/admission-control.md`](doc/admission-control.md)
- Hosting / DI / service lifecycle：[`doc/hosting-and-services.md`](doc/hosting-and-services.md)
- 日志、Activity 与 Meter：[`doc/observability.md`](doc/observability.md)
- 多集群与动态模块：[`doc/dynamic-modules-and-multicluster.md`](doc/dynamic-modules-and-multicluster.md)
- 资源限制与调优：[`doc/limits-and-tuning.md`](doc/limits-and-tuning.md)
- 故障排查 / 迁移：[`doc/troubleshooting.md`](doc/troubleshooting.md)、[`doc/migration.md`](doc/migration.md)
- 架构 / Protocol v2：[`doc/architecture.md`](doc/architecture.md)、[`doc/protocol-v2.md`](doc/protocol-v2.md)

## Developer / repository build

下面是**贡献 SharpLink 本身**的路径，不是 NuGet consumer 的入门前置条件。

```bash
dotnet build Sharplink.slnx -c Release
dotnet test --project test/SharpLink.UnitTests/SharpLink.UnitTests.csproj -c Release
dotnet test --project test/SharpLink.Generator.Tests/SharpLink.Generator.Tests.csproj -c Release
dotnet run --project test/SharpLink.IntegrationTests/SharpLink.IntegrationTests.csproj -c Release -- --timeout 120s
```

常用 runnable demos 在 `demo/`：`HelloWorld`、`Streaming`、`HostApplication`、`Security`、`Compression`、`AdmissionControl`、`InterceptorsTelemetry`、`Resilience`、`TransportMatrix`、`MultiCluster` 等。面向用户文档应优先引用 `samples/QuickStart.*` / `samples/ProductionTemplate.*`；demo 可以继续展示单项高级能力。

发布链路会 pack 当前 NuGet artifacts、验证 package graph/Generator 分发，并在 fresh cache 中执行 package smoke。流程说明见 [`doc/releasing.md`](doc/releasing.md)。

## Contributing / release

- 贡献指南：[`CONTRIBUTING.md`](CONTRIBUTING.md)
- 发布流程：[`doc/releasing.md`](doc/releasing.md)
- 安全漏洞：请按 [`SECURITY.md`](SECURITY.md) 私下报告，不要创建公开 Issue
- 更新日志：[`CHANGELOG.md`](CHANGELOG.md)

SharpLink 使用 MIT License。
