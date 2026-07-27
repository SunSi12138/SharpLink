# SharpLink 0.8.5 迁移指南

English: [`en/migration-0.8.5.md`](en/migration-0.8.5.md)

0.8.5 不改变 public API、Protocol v2 wire layout 或 generated Manifest 版本，Client/Server 可滚动升级。

- Host 终止后，`ISharpLinkClientAccessor.GetClientAsync` 现在始终失败，不再允许竞态中的最后一次已发布 client 读取。
- Service factory/handler 与 scope/service cleanup 同时失败时，Server 会保留全部 cause。自定义 exception mapper 应检查 `AggregateException.InnerExceptions`，不要假设只收到一个异常。
- 固定 Client 的初始最小连接池建立失败后会完整释放已连接会话，并将状态稳定置为 `Faulted`；后续仍可按既有策略重试或 Stop。

无配置迁移要求。
