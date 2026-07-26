# SharpLink 0.8.12 迁移指南

English: [`en/migration-0.8.12.md`](en/migration-0.8.12.md)

0.8.12 不改变 public API、Protocol v2 或 generated Manifest。`UseTransport` 与 `UseEndpointResolver` 交给 Client 的资源在构建失败后现在会被释放；失败后不要复用同一 transport/resolver 实例。自定义 logger、Codec Adapter 或其他构建扩展点与回滚同时失败时可能收到 `AggregateException`，首因仍为原构建失败。无配置迁移要求。
