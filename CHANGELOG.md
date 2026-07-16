# 更新日志

本文档记录项目的重要变更，格式参考 Keep a Changelog，并遵循语义化版本。

## [Unreleased]

### 新增

- Source Generator 原生 DTO/闭合集合 Codec、稳定字段 ID、未知字段跳过、required 校验与 64 层类型图边界。
- `[RpcSerializable]`、`[RpcMember]`、`[RpcIgnore]`、`[RpcRequired]`、`[RpcExternalCodec]`。
- append-only generated Codec manifest；Runtime Context 在 Build 时冻结 manifest 快照。
- Protocol v2 stream/connection 双层字节窗口、`WindowUpdate` 与单个大帧临时借用。

### 变更

- `IRpcCodec<T>.Serialize` 统一写入 `IBufferWriter<byte>`；协议回填使用 `IRpcByteBufferWriter`。
- AOT Smoke 不再依赖 MemoryPack，覆盖 class、record、struct、嵌套数组和生成 manifest。
- client/server stream sender 在额度不足时异步等待；消费、取消、超时和断连统一释放额度等待者。
- stream dispatcher 按已编码字节记账，迟到的已取消 stream data 只丢弃并计数，不重建 dispatcher。

## [0.4.0] - 2026-07-17

本版本合并原实施路线图的 0.4 安全基线与 0.5 Runtime/Protocol v2 重构。

### 新增

- 实例级 `SharpLinkRuntimeContext`、性能预设与不可变配置快照。
- TCP/UDS、NamedPipe、AnonymousPipe 的 Client Factory、Server Listener 和独立 Connection 模型。
- 15 字节固定头的 Protocol v2、能力协商、Ping/Pong、Cancel、GoAway、deadline 与 metadata。
- `SharpLinkCallOptions`、`SharpLinkMetadata`、扩展错误码及服务端调用上下文。
- 字节有界单写者 SendPump、强制 flush marker 与资源耗尽保护。
- Client/Server 原子生命周期、自动重连、断连单次收敛和优雅排空。
- NuGet-only PackageSmoke；`SharpLink.Sdk` 自动携带 Source Generator。

### 变更

- `ConnectAsync` 成功返回、失败抛结构化异常，不再返回 `bool`。
- Client/Server 主生命周期统一为 `StopAsync` / `DisposeAsync`。
- 默认 Unary timeout 为 30 秒；stream 默认不设置 timeout。
- 自定义 Codec、Buffer Pool 与并发配置归属实例 Context，不再互相覆盖全局状态。
- 默认认证明确为 Anonymous；删除默认 Password 和旧字符串错误 wire 格式。
- 所有正式包目标框架更新为 .NET 10。

### 修复

- Parser/Codec 在切片、分配前验证所有网络长度，非法数据稳定映射为协议/数据错误。
- pending request、stream、server concurrency、anonymous pipe offer 与 writer retention 增加硬边界。
- 修复 timeout 调度器取消墓碑导致的吞吐退化，取消节点现在立即从有界堆移除。
- 修复 send/dispose、heartbeat/stop、read/write fault 和 GoAway/new request 等生命周期竞态。
- CI 与 PackageSmoke 使用隔离 NuGet 缓存，避免同版本旧包污染验证。

### 兼容性说明

- 本版本包含公共 API 与线协议破坏性升级，不提供 Protocol v1 兼容层。
- c32 Unary QPS 为前一基线的 96.75%，保留原基线并在 0.5.1 优先处理。

## [0.2.0] - 2026-02-18

### 新增

- 引入 `RpcCodecRegistry`，支持统一注册与复用类型 Codec。
- 引入可选 MemoryPack 回退序列化器。
- 增强 blittable struct 容器编解码。

### 变更与修复

- 区分正常关闭与异常断连日志。
- 修复 Hosting 正常停机噪音与 pending request 失败传播问题。
- 本版本包含破坏性变更。

## [0.1.0] - 2026-02-12

### 新增

- 初始化 Abstractions、Runtime、Client、Server、SDK、Hosting 与 Generator。
- 支持 Unary、OneWay、客户端流、服务端流和双向流。
- 增加示例、Unit/Integration/AOT/Load/Benchmark 测试和 CI 工作流。

### 变更与修复

- 优化流式调用固定分配。
- 修复初始生命周期、取消和断连边界问题。
