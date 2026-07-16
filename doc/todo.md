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
4. `0.5.4`（已完成）：实现 Source Generator DTO Codec 的封闭稳定子集、append-only manifest 与 Context 快照；MemoryPack/外部类型保持显式插件边界。
5. `0.5.5`（已完成）：实现 stream/connection 按字节窗口、`WindowUpdate`、公平等待与取消/断连收敛。
6. `0.5.6`（已完成）：实现每 Endpoint 有界连接池、压力扩容、power-of-two choices、stream 固定绑定与 draining connection 退出。
7. （已完成）Release、Unit、Generator、Integration、NativeAOT、PackageSmoke 与 LoadTest/Benchmark 门禁全部通过；下一阶段进入 0.6。

## 后续版本

- `0.6.0`（开发中）：`0.6.1` TCP TLS 已完成；继续认证、Interceptor、业务异常、OpenTelemetry、Hosting、健康检查与优雅排空。
- `0.7.0`：Discovery、Load Balancing 与 Resilience 官方扩展包。
- `0.8.0-rc`：Chaos、长稳、跨平台/AOT/Package/性能 Release Gate，只修复正确性、稳定性、性能回退和文档问题。
- `1.0.0`：通过 RC 接受标准后发布稳定 API 与 Protocol v2。

## 提交规则

- 每个实施编号至少一个可独立构建、可测试、可回退的提交。
- 每个 `0.x.0` 边界增加版本 checkpoint；不把多个未验证阶段长期堆在工作区。
- 性能改动在提交说明或 `performance.md` 中保留同环境前后数据。
