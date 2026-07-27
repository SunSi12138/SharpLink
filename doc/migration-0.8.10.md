# SharpLink 0.8.10 迁移指南

English: [`en/migration-0.8.10.md`](en/migration-0.8.10.md)

0.8.10 不改变 public API、Protocol v2 或 generated Manifest。自定义 transport、profile-aware factory 或 Codec Adapter 在构建与回滚同时失败时，现在可能以 `AggregateException` 返回，其第一个原因仍为主构建失败。无配置迁移要求。
