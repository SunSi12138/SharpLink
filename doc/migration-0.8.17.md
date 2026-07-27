# SharpLink 0.8.17 迁移指南

English: [`en/migration-0.8.17.md`](en/migration-0.8.17.md)

0.8.17 不改变 Protocol v2 wire format 或 generated Manifest。握手 request 的 `RequiredCapabilities` 现在必须是 `SupportedCapabilities` 的子集；未知 request capability 仍由协商层处理，未知 negotiated response capability 则按 protocol violation 拒绝。

Runtime 配置新增聚合安全边界：`RuntimeConcurrencyOptions.StripeCount` 最大为 1,024，所有 stripe 的 `InitialMapCapacityPerStripe` 总和最大为 1,048,576 entries，`BufferWriterPoolOptions.MaxPooledWriters × MaxRetainedCapacityBytes` 最大为 64 MiB。超过边界的部署应减少预分配/保留量，或把负载拆分到更多 Runtime Context；默认值不受影响。

TLS 与分区准入配置现在在构建边界深复制。构建后修改原始 `X509ChainPolicy`、partition concurrency/rate-limit options 不再改变 live Client/Server；需要变更时应构建并发布新的实例。并发注销同一个 multi-cluster dynamic assembly 现在共享一个 operation，所有调用方观察同一结果，同时每个调用方仍可独立取消自己的等待。
