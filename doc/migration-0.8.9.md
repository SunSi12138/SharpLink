# SharpLink 0.8.9 迁移指南

English: [`en/migration-0.8.9.md`](en/migration-0.8.9.md)

0.8.9 不改变 public API、Protocol v2 或 generated Manifest。Hosted Client 与异步 server listener 的重复 Stop/Dispose 现在等待同一终止结果；多个 listener 所有权资源失败时可能暴露聚合异常。无配置迁移要求。
