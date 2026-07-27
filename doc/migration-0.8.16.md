# SharpLink 0.8.16 迁移指南

English: [`en/migration-0.8.16.md`](en/migration-0.8.16.md)

0.8.16 不改变 Protocol v2 或 generated Manifest。`MaxPendingRequestsPerConnection` 仍须为正二次幂，并新增 1,048,576 上限；超过上限的配置必须拆分到更多物理连接。`SharpLinkBufferWriterPool` 新增 `IDisposable`，由 Runtime Context 拥有的 pool 会随 Context 关闭，之后的 `Rent` 抛出 `ObjectDisposedException`。

Server 的 `StopAsync`、`DisposeAsync` 以及共享 `RunAsync` 现在向调用方传播即时 listener/framework/service 清理失败；单项保持原异常，多项使用 `AggregateException`。超过五秒最终清理预算的异步清理仍由框架观察并以 Unhealthy/Faulted 状态记录，不会让 Stop 无限等待。Generic Host 的 `StartAsync` token 只控制启动阶段；成功发布后应使用 Host 的 Stop 流程关闭服务。
