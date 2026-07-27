# SharpLink 0.8.4 迁移指南

English: [`en/migration-0.8.4.md`](en/migration-0.8.4.md)

0.8.4 不改变 public API、Protocol v2 wire layout 或 generated Manifest 版本，Client/Server 可滚动升级。

- 动态 Manifest 发布与 Runtime Context Dispose 竞态中的 Codec lookup 现在会重试或抛出 `ObjectDisposedException`，不再返回过期 Codec。
- 自定义 fallback Codec resolver 与 native generated Codec factory 在极少数并发发布竞态中可能再次执行；实现应保持线程安全，并避免依赖“全局仅调用一次”的副作用。
- client-stream admission 的内部注册不再同步等待 buffered replay；帧仍受同一容量预算约束并按接收顺序交付。
- multi-cluster replacement 若新 generation 已发布但旧代 cleanup 失败，调用仍抛出 cleanup 异常，不过新 contract route 已可使用。

无配置迁移要求。
