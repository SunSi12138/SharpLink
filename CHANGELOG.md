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
