# 更新日志

本文档记录项目的重要变更。

格式参考 Keep a Changelog，并遵循语义化版本（SemVer）。

## [0.1.0] - 2026-02-12

### 新增
- 初始化 SharpLink 核心项目结构（`Abstractions/Runtime/Client/Server/Sdk/Hosting/Generator`）。
- 基于 Source Generator 的 RPC Proxy/Stub 生成链路。
- 支持 Unary、Oneway、客户端流、服务端流、双向流调用模型。
- 协议级取消能力与流分发能力。
- 基于 `ILogger` 的日志配置能力。
- `BufferWriterPool` 与可配置池化参数。
- 示例项目：`HelloWorld`、`Streaming`、`HostApplication`、`Cancel`、`OnwWay`、`Log`。
- 测试项目：集成测试、AOT Smoke、LoadTest、Benchmarks、UnitTests（TUnit）。
- CI 工作流：`pr-quick`、`nightly`、`release-gate`。

### 变更
- 优化流式调用路径，减少固定额外分配。
- 将协议测试迁移到 `SharpLink.UnitTests`（TUnit），并更新 CI 对应步骤。
- 增强 LoadTest 指标：新增实时滚动 QPS/延迟指标，便于 Grafana 观察波动。

### 修复
- 修复多处生命周期与释放路径上的取消/断连边界问题。
- 修复若干 Builder、日志配置、Demo 编译与运行问题。


## [0.2.0] - 2026-02-18

### 新增
- 引入 `RpcCodecRegistry` 体系，支持统一注册与复用类型编解码器。
- 对于引用类型需要使用MemoryPack作为fallout支持`RpcCodecRegistry.Initialize(MemoryPackCodec.Resolver)`。
- 增强 `StructCodec`：增加 `blittable` 类型容器的高性能序列化/反序列化支持。 

### 变更
- 编解码流程支持 fallback 到配置的序列化器（`MemoryPackCodec.Resolver`），
  提升兼容性。
- 连接断开处理增加“正常关闭”路径，区分 graceful shutdown 与异常断连。
- 客户端断连日志分流：新增 `LogClientDisconnectedWithError`，用于异常场景单
  独记录错误级日志。

### 修复
- 修复 Host 场景中正常停机被当作异常断连输出的问题
  （`ObjectDisposedException` 日志噪音）。
- 优化断连时 pending request 的失败传播路径，避免关闭流程语义混淆。

### 兼容性说明
- 本版本为破坏性变更。