# SharpLink 实施顺序

本文档记录当前已经确认的版本映射与执行顺序；详细协议与性能数据分别见 `protocol-v2.md` 和 `performance.md`。

## 0.4.0 checkpoint

当前 `0.4.0` 合并原路线图的 0.4 与 0.5，包含：

- Parser、Codec、资源边界、生命周期与 CI/Package 安全基线。
- 实例级 Runtime Context 与冻结配置。
- Client Factory、Server Listener、独立 Transport Connection。
- Protocol v2、字节有界单写者 SendPump。
- Client/Server 原子状态机、自动重连、GoAway 与优雅停机。
- `SharpLinkCallOptions`、deadline、metadata 与结构化错误。
- Release、Unit、Generator、Integration、NativeAOT、NuGet PackageSmoke 已通过。
- c32 Unary QPS 为前一基线的 96.75%，保留原基线并在 0.5.1 优先解决。

## 0.5.0 实施顺序

从本版本开始只使用当前版本号，不再同时引用旧路线图编号。每个编号独立提交；前一项未通过正确性与性能验收，不开始后一项。

1. `0.5.1`（已完成）：重写 PendingRequestTable，把 admission、response/error/cancel/timeout/disconnect 完成仲裁与默认 deadline 登记整合进同一槽位生命周期。
2. `0.5.2`（已完成）：将生成代理收敛为 Unary、OneWay、ClientStreaming、ServerStreaming、DuplexStreaming 五类 Invoker。
3. `0.5.3`（已完成）：实现 `PooledByteBufferWriter`，统一 payload owner 与 SendPump 归还时机；Codec 仅依赖 `IBufferWriter<byte>`，协议回填通过 `IRpcByteBufferWriter` 完成。
4. `0.5.4`（已完成）：实现 Source Generator DTO Codec 的封闭稳定子集、弱 Manifest Catalog 与 Context 快照；第三方复杂类型保持显式扩展边界。
5. `0.5.5`（已完成）：实现 stream/connection 按字节窗口、`WindowUpdate`、公平等待与取消/断连收敛。
6. `0.5.6`（已完成）：实现每 Endpoint 有界连接池、压力扩容、power-of-two choices、stream 固定绑定与 draining connection 退出。
7. （已完成）Release、Unit、Generator、Integration、NativeAOT、PackageSmoke 与 LoadTest/Benchmark 门禁全部通过；下一阶段进入 0.6。

## 后续版本

- `0.6.0`（已完成）：TLS、认证、Interceptor/异常映射、遥测、Hosting、DI、健康检查与优雅排空均已通过 Release、Unit、Generator、Integration、NativeAOT、PackageSmoke 与性能门禁。
- `0.6.6`（已完成）：可信性能基线、协商帧边界、异步请求缓冲生命周期、低风险热路径优化与旧兼容层删除。
- `0.6.7`（已完成）：每连接客户端状态、统一 PendingCallTable、无完成锁 deadline 调度和提前停止 stream 消费收敛。
- `0.6.8`（已完成）：统一服务端连接状态、取消原因仲裁、用户调用 observer 与证据驱动附加审计。
- `0.6.9`（已完成）：有界 Server Stop、Stream Dispatcher 峰值回收、Chaos/长稳与最终性能收敛。
- `0.6.10`（功能完成，待发布长稳）：完整取消契约、带原因 Cancel、monotonic 服务端 deadline、取消遥测与迟到响应限频；本地 Release/AOT/Package/性能/120 秒 Chaos Gate 已通过，创建 tag 前仍需最终提交的 24 小时连续长稳。
- `0.7.0`（已完成）：实验性 SharedMemory 传输。
- `0.7.1`（已完成）：自动服务注册、三种根服务生命周期、运行时程序集原子注册、安全排空与 collectible ALC 验证。
- `0.7.2`（已完成）：静态 Singleton Unary 快路径、请求池分配削减、历史性能二分与五传输回归矩阵。
- `0.7.5`（已完成）：静态 endpoint topology、P2C/Random/RoundRobin/LeastPending 与 custom selector。
- `0.7.6`（已完成）：动态 Resolver/DNS Discovery、snapshot generation 与 draining。
- `0.7.7`（已完成）：Logical Call/Attempt、仅 `[Idempotent]` Unary 的 Retry。
- `0.7.8`（已完成）：endpoint admission SPI、Circuit Breaker、HalfOpen probe 与 generation isolation。
- `0.7.9`（已完成）：组合验证、低基数 telemetry、迁移文档与 API freeze。
- `0.7.10`（已完成）：多 cluster Client、编译期 route Manifest 与动态模块协调。
- `0.7.11`（已完成）：通用 Codec Adapter、Manifest API v3、SharpPack 1.1.0、wire-format identity、collectible Scope 生命周期与 MemoryPack 扩展删除。
- `0.8.0`（已完成）：深度审核第一批五项 P2 修复——Codec 精确消费与规范标记、跨 stream connection credit、继承契约方法、unmanaged Adapter 请求布局；保留回归与性能实证。
- `0.8.x`（进行中）：每累计五项 P2 及以上实证改进推进一个小版本；其他低风险改进随批次合入但不单独推进版本。连续三轮审计无新改进点后结束。
- `0.8.x-rc`：上述审核收敛后执行 Chaos、长稳、跨平台/AOT/Package/性能 Release Gate，只修复正确性、稳定性、性能回退和文档问题。
- `1.0.0`：通过 RC 接受标准后发布稳定 API 与 Protocol v2。

## 提交规则

- 每个实施编号至少一个可独立构建、可测试、可回退的提交。
- 每个 `0.x.0` 边界增加版本 checkpoint；不把多个未验证阶段长期堆在工作区。
- 性能改动在提交说明或 `performance.md` 中保留同环境前后数据。
