# SharpLink 0.8.3 迁移指南

English: [`en/migration-0.8.3.md`](en/migration-0.8.3.md)

0.8.3 不改变 wire layout 或 `SharpLinkMetadata` public constructor signature。

- `SharpLinkEndpointSnapshot` 现在拥有 endpoint/attributes 快照；创建后修改原 dictionary 不再影响 snapshot。
- `StopAsync` 更早返回可等待的异步 operation；阻塞 cancellation callback 不再同步卡住 API 调用。
- connect 或 Hosted startup 同时遇到主失败与 cleanup 失败时会抛出以主失败在前的 `AggregateException`，调用方日志应展开 inner exceptions。
- metadata decode 的数组所有权优化只在 Runtime 内部可见。

Client/Server 可滚动升级；为获得一致诊断，建议同批部署。
