# SharpLink 0.8.18 迁移指南

English: [`en/migration-0.8.18.md`](en/migration-0.8.18.md)

0.8.18 不改变 Protocol v2 wire format 或 generated Manifest。`SharpLinkFlowControlOptions.MaxConcurrentCallsPerConnection` 新增 1,048,576 硬上限；超过该值的部署应把并发拆分到更多物理连接。默认值 1,024 不变。

Generic Host 现在无论 Client `StopAsync(token)` 成功、失败或被取消，都会继续调用已转移 owner 的 `DisposeAsync`；若 Stop 与 Dispose 都失败则返回 `AggregateException`。自定义 Client 应保持 Dispose 幂等，并把 Dispose 作为最终无 token 清理边界。

Client/Server dynamic assembly 的 `gracefulTimeout` 与显式 send flush `MaxLatency` 现在支持超过原生 timer 范围的正值。`StreamManager.CompleteAll` 会先排空所有 dispatcher，再保留单项原异常或聚合多项异常；由 RpcSession 发起的终止会隔离这些用户清理异常，以确保 pipe/transport owner 最终释放。
