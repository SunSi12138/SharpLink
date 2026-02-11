# SharpLink

一个面向 .NET 的高性能 RPC 框架（当前以 `net10.0` 为主），支持：

- Source Generator 自动生成 `Proxy/Stub`
- 普通请求、单向调用（`[Oneway]`）
- 客户端流、服务端流、双向流、多流参数
- 协议级取消（`PacketType.Cancel`）
- `Microsoft.Extensions.Hosting` 托管集成
- 可插拔传输层与序列化（当前默认 MemoryPack）

## 项目结构

核心项目（`src/`）：

- `SharpLink.Abstractions`：协议常量、核心接口、会话与流管理抽象
- `SharpLink.Runtime`：基础运行时与传输实现（Socket/NamedPipe/AnonymousPipe）
- `SharpLink.Sdk`：Builder 与易用扩展（传输/配置入口）
- `SharpLink.Client`：客户端通道与请求管理
- `SharpLink.Server`：服务端连接管理、分发、心跳与取消处理
- `SharpLink.Hosting`：`IServiceCollection` 扩展与 HostedService 集成
- `SharpLink.Generator`：`[RpcService]` 生成器与编译期诊断
- `SharpLink.Serializer.MemoryPack`：MemoryPack 序列化适配

示例（`demo/`）：

- `HelloWorld`：基础调用（多参数/多类型）
- `Streaming`：流式调用（单向/双向/多流）
- `HostApplication`：Host 模式完整示例
- `Cancel`：协议级取消示例
- `OnwWay`：单向调用示例（目录名按当前仓库保持 `OnwWay`）
- `Log`：日志配置示例（默认静默 + `UseLogging` 开启 `ILogger`）

基准（`test/SharpLink.Benchmarks`）：

- Unary 场景：`Add/Echo/Payload/Array/List/Memory/Oneway`
- Streaming 场景：上传流/下载流/双向流/多流合并

## 快速开始

环境要求：

- .NET SDK `10.0`（与仓库当前 TFM 对齐）

构建：

```bash
dotnet build -v minimal
```

运行示例：

```bash
dotnet run --project demo/HelloWorld
dotnet run --project demo/Streaming
dotnet run --project demo/HostApplication
dotnet run --project demo/Cancel
dotnet run --project demo/OnwWay
dotnet run --project demo/Log
```

运行基准：

```bash
dotnet run -c Release --project test/SharpLink.Benchmarks
# 或直接过滤
dotnet run -c Release --project test/SharpLink.Benchmarks -- --filter *UnaryBenchmarks*
dotnet run -c Release --project test/SharpLink.Benchmarks -- --filter *StreamingBenchmarks*
```

## Host 模式（简例）

`SharpLink.Hosting` 提供 `IServiceCollection` 扩展：

- `AddSharplinkServer(...)`
- `AddSharplinkClient(...)`

你可以在同一个 Host 中同时挂载 Server + Client，也可以拆分为独立进程。

## 可调优配置（简例）

- 日志：`UseLogging(...)` / `UseLoggerFactory(...)`
- BufferWriter 池：`UseBufferWriterPool(...)`

```csharp
var server = SharplinkServerBuilder.Create()
    .UseBufferWriterPool(o =>
    {
        o.InitialCapacity = 1024;
        o.MaxPooledWriters = 512;
        o.MaxRetainedCapacityBytes = 64 * 1024;
    });
```

## 文档

- 计划：`doc/plan.md`
- 架构：`doc/architecture.md`
- 待办与问题：`doc/todo.md`
- 压测：`doc/loadtest.md`
- 贡献指南：`CONTRIBUTING.md`
- 社区行为准则：`CODE_OF_CONDUCT.md`
- 更新日志：`CHANGELOG.md`
