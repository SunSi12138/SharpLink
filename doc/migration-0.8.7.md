# SharpLink 0.8.7 迁移指南

English: [`en/migration-0.8.7.md`](en/migration-0.8.7.md)

0.8.7 不改变 public API、Protocol v2 或 generated Manifest。并发 Dispose/Stop 现在等待同一所有权清理；多个 Adapter/connection close 失败可能以 `AggregateException` 暴露。取消回调异常会被记录，不再阻断 RPC 终止。无配置迁移要求。
