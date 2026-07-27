# SharpLink 0.8.11 迁移指南

English: [`en/migration-0.8.11.md`](en/migration-0.8.11.md)

0.8.11 不改变 public API、Protocol v2 或 generated Manifest。正常的动态程序集注册/替换拒绝仍返回结构化错误；仅当自定义 Codec Adapter 或候选服务在事务回滚时也失败，调用方现在会收到 `AggregateException`，其第一个原因为原事务拒绝。自定义 profile-aware Server transport 的绑定失败现在也会释放新建 Runtime Context。无配置迁移要求。
