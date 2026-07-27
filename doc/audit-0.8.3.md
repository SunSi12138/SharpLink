# SharpLink 0.8.3 深度审核

English: [`en/audit-0.8.3.md`](en/audit-0.8.3.md)

本批以 0.8.2 commit `422305d` 为基线，审核不可变拓扑、Client/Hosting 异步生命周期和 metadata 分配。四项生命周期/可变性 P2 先以失败测试实证，第五项以三启动 BenchmarkDotNet 分配实证。

| 等级 | 问题与实证 | 修复与验证 |
| --- | --- | --- |
| P2 | `SharpLinkEndpointSnapshot` 虽保护顶层数组，仍保留原 `SharpLinkEndpoint` 及其可变 Attributes dictionary。 | snapshot 深复制 endpoint 并冻结 nested attributes；源字典与强转 mutation probe 均无法改写发布值。 |
| P2 | Client 与 multi-cluster 的 async Stop 在首次 await 前同步 `Cancel()`；阻塞 callback 会让 `StopAsync` 本身无法返回。 | 使用并 await `CancelAsync`，callback 异常加入 cleanup failure 而不跳过后续释放。 |
| P2 | fixed/static/dynamic connect 的 session/transport Dispose 异常会覆盖 handshake/connect 主失败。 | 共享失败清理路径保留主异常；清理也失败时按主异常在前聚合。 |
| P2 | Client/multi-cluster/Server HostedService 的启动失败清理同样可能覆盖原始启动异常，并跳过后续 token 释放。 | 所有 Hosted startup 路径保留原始失败、聚合 cleanup failure，并继续完成独立清理。 |
| P2 | metadata wire decode 已分配并验证 `KeyValuePair[]`，随后 public constructor 再分配数组并复制；两项时为 280 B/op。 | Runtime 通过受限 internal ownership factory 接管已验证数组；降为 224 B/op，public defensive-copy API 不变。 |

曾评估把 public `params T[]` 改为 `params ReadOnlySpan<T>`；.NET 10 基准显示构造仍为 80 B/op，收益不足以抵消 public binary signature 变化，因此明确撤回。性能见 [`performance-0.8.3.md`](performance-0.8.3.md)，迁移见 [`migration-0.8.3.md`](migration-0.8.3.md)。
