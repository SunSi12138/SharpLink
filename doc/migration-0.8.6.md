# SharpLink 0.8.6 迁移指南

English: [`en/migration-0.8.6.md`](en/migration-0.8.6.md)

0.8.6 不改变 public API、Protocol v2 或 generated Manifest。多资源清理失败现在可能抛出包含全部 cause 的 `AggregateException`。Generic Host 中 SharpLink Server 的后台 run-loop 异常现在会触发 Host 停止；请使用既有日志与进程重启策略处理。无配置迁移要求。
