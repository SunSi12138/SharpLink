# SharpLink 版本路线图

本文档是版本号与范围的唯一事实来源。实施顺序见 `todo.md`，协议定义见 `protocol-v2.md`，性能数据见 `performance.md`。

## 总体目标

- `.NET 10 LTS`、Windows/Linux/macOS 与 NativeAOT 一等支持。
- TCP/UDS、NamedPipe、AnonymousPipe 使用统一会话、错误与生命周期语义。
- 默认单连接复用，可配置连接池；默认 `Balanced`，提供 `LowLatency` 与 `Throughput`。
- SDK 内置无反射 DTO Source Generator Codec；第三方序列化通过通用 Codec Adapter SPI 接入，官方复杂图扩展使用 SharpPack。
- 核心提供 TLS、认证、Interceptor、deadline、背压、健康检查、遥测与优雅停机。
- Discovery、Load Balancing、Retry 与 Circuit Breaker 内置于现有程序集；第三方注册中心仅通过显式 Resolver SPI 接入。

## 0.4.0：Runtime 与 Protocol v2 基线（已发布）

- Parser/Codec 安全边界、资源上限、生命周期收敛与 PackageSmoke。
- 实例级 Runtime Context、Transport Factory/Listener/Connection 拆分。
- Protocol v2、字节有界 SendPump、CallOptions、deadline、metadata、GoAway 与自动重连。

## 0.5.0：原生 Codec 与热路径优化（已完成）

1. `0.5.1`（已完成）：PendingRequestTable、异步 admission 与统一完成仲裁。
2. `0.5.2`（已完成）：Unary、OneWay、ClientStreaming、ServerStreaming、DuplexStreaming 五类内部 Invoker。
3. `0.5.3`（已完成）：`PooledByteBufferWriter`、`ArrayPool<byte>` 与明确的 payload ownership。
4. `0.5.4`（已完成）：受限、无反射、AOT 友好的 DTO Codec Generator；复杂对象图保留显式扩展边界。
5. `0.5.5`（已完成）：stream/connection 按字节窗口、`WindowUpdate` 与慢消费者背压。
6. `0.5.6`（已完成）：每 Endpoint 连接池、power-of-two choices 与性能门禁工具入口。

## 0.6.0：企业核心能力（已完成）

1. `0.6.1`（已完成）：TCP TLS、双向证书与独立握手超时。
2. `0.6.2`（已完成）：Anonymous/自定义认证、Authentication Context 与 token expiry。
3. `0.6.3`（已完成）：Client/Server Interceptor、`IRpcExceptionMapper` 与 `[Idempotent]`。
4. `0.6.4`（已完成）：OpenTelemetry Activity、Meter 与预编译结构化日志。
5. `0.6.5`（已完成）：DI 服务生命周期、健康检查、readiness 与优雅排空。

## 0.6.6–0.6.10：性能、稳定性与取消契约收敛

1. `0.6.6`（已完成）：可信基线、协商帧边界、异步请求缓冲生命周期、低风险热路径优化与旧兼容层删除。
2. `0.6.7`（已完成）：每连接 ClientConnection/PendingCallTable、monotonic deadline 调度和提前停止 stream 消费收敛。
3. `0.6.8`（已完成）：统一 ServerConnectionState、取消原因仲裁、用户调用 observer 与证据驱动附加审计。
4. `0.6.9`（已完成）：有界 Server Stop、Stream Dispatcher 峰值回收、Chaos/长稳和 JIT/NativeAOT 最终性能审核。
5. `0.6.10`（功能完成，待发布长稳）：Streaming 取消契约诊断、带原因 Cancel、服务端 monotonic deadline、取消遥测与迟到响应限频。

0.6.10 只增加可协商的 Protocol minor 2 capability，与 0.6.9 自动退回 legacy Cancel。完成 24 小时 release soak 与 tag Gate 前，不开始官方企业扩展包。

## 0.7.x：运行时模块与官方企业扩展包

1. `0.7.0`（已完成）：实验性 SharedMemory 传输。
2. `0.7.1`（已完成）：Generator Manifest 自动服务注册、`Singleton/Connection/Call` 生命周期与运行时程序集安全注册/注销。
3. `0.7.2`（已完成）：静态 Singleton Unary 性能恢复、稳态分配削减与完整回归归因。
4. `0.7.5`（已完成）：静态 endpoint topology、内置负载均衡与 selector SPI。
5. `0.7.6`（已完成）：动态 Resolver topology、DNS Discovery 与 generation 排空。
6. `0.7.7`（已完成）：Logical Call/Attempt、仅 `[Idempotent]` Unary 的 Retry。
7. `0.7.8`（已完成）：endpoint admission SPI 与 generation-scoped Circuit Breaker。
8. `0.7.9`（已完成）：组合验证、低基数 telemetry、迁移文档与 API freeze。
9. `0.7.10`（已完成）：多 cluster Client、静态/动态 contract route 与 child lifecycle 隔离。
10. `0.7.11`（已完成）：删除 MemoryPack 扩展与 `RpcExternalCodec`，引入 manifest-scoped Codec Adapter SPI，并迁移到 SharpPack 1.1.0。

## 0.8.x：证据驱动的框架深度审核（进行中）

- `0.8.0`：修复原生 Codec 非精确消费/非规范标记、跨 stream connection credit 滞留、继承接口 RPC 遗漏，以及 unmanaged Adapter 请求绕过 Codec；详见 [`audit-0.8.0.md`](audit-0.8.0.md)。
- `0.8.1`：修复认证与 Manifest 可变快照、Resolver 释放、语义请求校验和 `List<T>` 双数组解码；详见 [`audit-0.8.1.md`](audit-0.8.1.md)。
- `0.8.2`：修复共享 Connect 取消传播、cluster 握手超时、DNS 异常吞噬、非规范 VarUInt32 与非法 UTF-8 error payload；详见 [`audit-0.8.2.md`](audit-0.8.2.md)。
- `0.8.3`：修复 endpoint nested attributes、async shutdown callback、连接与 Hosting cleanup 异常遮蔽，并删除 metadata decode 二次复制；详见 [`audit-0.8.3.md`](audit-0.8.3.md)。
- `0.8.37`：修复 generated service/DTO 可达性、keyword DTO 标识符、record/ref-like DTO 与 static abstract contract 边界；详见 [`audit-0.8.37.md`](audit-0.8.37.md)。
- `0.8.38`：修复 generated service/DTO 构造计划、pointer/function-pointer 产物与 interceptor 结构化取消状态；详见 [`audit-0.8.38.md`](audit-0.8.38.md)。
- `0.8.39`：修复 interceptor 终端状态/continuation/结果边界、client stream context capture 与 malformed request DataLoss 分类；详见 [`audit-0.8.39.md`](audit-0.8.39.md)。
- 后续每五项 P2 及以上的已复现、已修复、已验证改进形成一个小版本；P3/语法与抽象收敛可随批次提交，但不单独推进版本。
- 每批必须通过 Release、Generator、Unit、Integration 和相关性能门禁；连续三轮无新改进点后转入 RC。

## 0.8.x-rc：发布门禁

- Unit、Generator、Integration、AOT、Package、Load、StreamLoad、Benchmark 与 Chaos 分层测试。
- Windows/Linux/macOS Transport matrix，Linux/Windows 72 小时长稳与固定 runner 性能基线。
- SourceLink、符号包、确定性构建、SBOM、公共 API diff、协议文档与迁移指南。
- RC 期间不再增加功能，只修复正确性、稳定性、性能回退和文档问题。

## 1.0.0：稳定版

- 无 crash、deadlock、未观察后台异常或非注入调用失败。
- 故障收敛后 pending request、stream 与后台任务在 timeout 内归零。
- NativeAOT 无 runtime reflection fallback；SDK 单包引用即可生成契约与 DTO Codec。
- 固化公共 API 与 Protocol v2；不提供 Protocol v1 长期兼容层。

## 不进入 1.0 的范围

- 非 .NET 客户端与跨语言 IDL。
- HTTP/REST/WebSocket/浏览器网关。
- 分布式事务与服务网格控制面。
- 任意运行时反射序列化。
- Streaming/OneWay 自动重试。
- 内置具体注册中心客户端。
